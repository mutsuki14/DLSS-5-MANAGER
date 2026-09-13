#load "../Models/GameItem.fs"
#load "../Services/ExtrasStore.fs"
#load "../Services/PeInspection.fs"
#load "../Services/DeploymentSafety.fs"
#load "../Services/CompatibilityRules.fs"
#load "../Services/RuntimePackages.fs"
#load "../Services/GameComponents.fs"
#load "../Services/GameAnalyzer.fs"
#load "../Services/HealthCheck.fs"
#load "../Services/PackagePlanning.fs"
#load "../Services/EmulatorCatalog.fs"
#load "../Services/ModInstaller.fs"

open System
open System.IO
open System.Text
open System.Text.Json
open DLSS_5_MANAGER.Services

let mutable passed = 0
let mutable failed = 0
let test name action =
    try action (); passed <- passed + 1; printfn "PASS %s" name
    with ex -> failed <- failed + 1; eprintfn "FAIL %s: %s" name (ex.ToString())
let equal expected actual = if expected <> actual then failwithf "Expected %A, received %A" expected actual
let rejects action =
    let mutable rejected = false
    try action () with _ -> rejected <- true
    if not rejected then failwith "Expected operation to be rejected"
let tempTest action =
    let dir = Path.Combine(Path.GetTempPath(), "dlss5-regression-" + Guid.NewGuid().ToString("N"))
    let game, backup = Path.Combine(dir, "game"), Path.Combine(dir, "backups")
    Directory.CreateDirectory(game) |> ignore
    Directory.CreateDirectory(backup) |> ignore
    try action game backup
    finally
        for p in Directory.GetFiles(dir, "*", SearchOption.AllDirectories) do
            try File.SetAttributes(p, FileAttributes.Normal) with _ -> ()
        Directory.Delete(dir, true)
let write (path: string) (text: string) = File.WriteAllText(path, text); path
let hash = DeploymentSafety.hashFile

// Real PE fixtures with import RVAs in a section, not embedded string scanning.
let makePe path machine is64 (imports: string[]) delay =
    let bytes = Array.zeroCreate<byte> 4096
    let u16 offset value = Array.Copy(BitConverter.GetBytes(uint16 value), 0, bytes, offset, 2)
    let u32 offset value = Array.Copy(BitConverter.GetBytes(uint32 value), 0, bytes, offset, 4)
    let u64 offset value = Array.Copy(BitConverter.GetBytes(uint64 value), 0, bytes, offset, 8)
    u16 0 0x5a4d; u32 0x3c 0x80; u32 0x80 0x4550
    u16 0x84 machine; u16 0x86 1
    let optional = 0x98
    let optionalSize = if is64 then 240 else 224
    u16 0x94 optionalSize; u16 0x96 0x102
    u16 optional (if is64 then 0x20b else 0x10b)
    if is64 then u64 (optional + 24) 0x140000000UL else u32 (optional + 28) 0x400000
    u32 (optional + 32) 0x1000; u32 (optional + 36) 0x200
    u32 (optional + 56) 0x2000; u32 (optional + 60) 0x200
    let directories = optional + (if is64 then 112 else 96)
    u32 (directories - 4) 16
    let stride = if delay then 32 else 20
    let table = directories + (if delay then 13 else 1) * 8
    u32 table 0x1000; u32 (table + 4) ((imports.Length + 1) * stride)
    let section = optional + optionalSize
    Array.Copy(Encoding.ASCII.GetBytes(".rdata"), 0, bytes, section, 6)
    u32 (section + 8) 0xE00; u32 (section + 12) 0x1000
    u32 (section + 16) 0xE00; u32 (section + 20) 0x200
    imports |> Array.iteri (fun i name ->
        let descriptor = 0x200 + i * stride
        let nameOffset = 0x800 + i * 260
        if delay then u32 descriptor 1
        u32 (descriptor + (if delay then 4 else 12)) (nameOffset - 0x200 + 0x1000)
        Array.Copy(Encoding.ASCII.GetBytes(name + "\000"), 0, bytes, nameOffset, name.Length + 1))
    File.WriteAllBytes(path, bytes)
    path

test "PE32 reads normal imports and ignores loose DLL markers" (fun () -> tempTest (fun game _ ->
    let exe = makePe (Path.Combine(game,"game.exe")) 0x14c false [|"D3D9.dll"|] false
    write (Path.Combine(game,"d3d12.dll")) "unrelated mod" |> ignore
    equal "32" (GameAnalyzer.detectArchitecture exe)
    equal "dx9" (GameAnalyzer.detectGraphicsApi exe)))
test "PE32+ reads Vulkan delay imports" (fun () -> tempTest (fun game _ ->
    let exe = makePe (Path.Combine(game,"game.exe")) 0x8664 true [|"vulkan-1.dll"|] true
    equal "vulkan" (GameAnalyzer.detectGraphicsApi exe)
    equal "vulkan" (GameAnalyzer.detectReShadeApi exe)
    equal 0 (HealthCheck.selectionIssues exe "optiscaler" "64" "vulkan").Length
    equal 1 (HealthCheck.selectionIssues exe "dx12" "64" "dx12").Length))
test "Multiple imported renderers remain ambiguous" (fun () -> tempTest (fun game _ ->
    let exe = makePe (Path.Combine(game,"game.exe")) 0x8664 true [|"d3d11.dll";"d3d12.dll"|] false
    write (Path.Combine(game,"d3d12.dll")) "marker" |> ignore
    equal "" (GameAnalyzer.detectGraphicsApi exe)))
test "ARM64 and x86 cannot select x64-only payloads" (fun () -> tempTest (fun game _ ->
    let arm = makePe (Path.Combine(game,"arm.exe")) 0xaa64 true [||] false
    equal "arm64" (GameAnalyzer.detectArchitecture arm)
    if (HealthCheck.selectionIssues arm "optiscaler" "64" "dx12").Length = 0 then failwith "ARM64 accepted"
    let x86 = makePe (Path.Combine(game,"x86.exe")) 0x14c false [||] false
    equal 1 (HealthCheck.selectionIssues x86 "dx12" "64" "dx12").Length))
test "Malformed/truncated executable is never treated as x64" (fun () -> tempTest (fun game _ ->
    let exe = write (Path.Combine(game,"bad.exe")) "MZ invalid"
    equal None (PeInspection.inspect exe)
    equal 1 (HealthCheck.selectionIssues exe "dx12" "64" "dx12").Length))
test "Install and uninstall preserve original bytes and timestamp" (fun () -> tempTest (fun game backup ->
    let original = write (Path.Combine(game,"dxgi.dll")) "original"
    let whenWritten = DateTime(2020,1,2,3,4,5,DateTimeKind.Utc)
    File.SetLastWriteTimeUtc(original,whenWritten)
    let payload = write (Path.Combine(backup,"payload")) "mod"
    let added = Path.Combine(game,"addon.dll")
    let tracker = DeploymentSafety.Tracker([|game|], backup, [||])
    tracker.Copy(payload,original); tracker.Copy(payload,added)
    let files = tracker.Entries
    tracker.Commit()
    equal 0 (DeploymentSafety.restore [|game|] backup files).Length
    equal "original" (File.ReadAllText(original))
    equal whenWritten (File.GetLastWriteTimeUtc(original))
    equal false (File.Exists(added))
    equal 0 (DeploymentSafety.restore [|game|] backup files).Length))
test "Missing or corrupted backups stop the entire restore before writes" (fun () -> tempTest (fun game backup ->
    let a = write (Path.Combine(game,"a.dll")) "original-a"
    let b = write (Path.Combine(game,"b.dll")) "original-b"
    let payload = write (Path.Combine(backup,"payload")) "mod"
    let tracker = DeploymentSafety.Tracker([|game|], backup, [||])
    tracker.Copy(payload,a); tracker.Copy(payload,b)
    let files = tracker.Entries
    write files.[0].BackupPath "corrupted" |> ignore
    rejects (fun () -> DeploymentSafety.restore [|game|] backup files |> ignore)
    equal "mod" (File.ReadAllText(b))
    File.Delete(files.[0].BackupPath)
    equal 1 (DeploymentSafety.checkRestore [|game|] backup files).Length))
test "Game updates block restore and repair without overwriting new DLLs" (fun () -> tempTest (fun game backup ->
    let target = write (Path.Combine(game,"game.dll")) "original"
    let payload = write (Path.Combine(backup,"payload")) "mod"
    let tracker = DeploymentSafety.Tracker([|game|],backup,[||])
    tracker.Copy(payload,target)
    write target "game-update" |> ignore
    rejects (fun () -> DeploymentSafety.restore [|game|] backup tracker.Entries |> ignore)
    rejects (fun () -> DeploymentSafety.Tracker([|game|],backup,tracker.Entries) |> ignore)
    equal "game-update" (File.ReadAllText(target))))
test "Changed user settings survive uninstall" (fun () -> tempTest (fun game backup ->
    let config = write (Path.Combine(game,"ReShade.ini")) "before"
    let payload = write (Path.Combine(backup,"payload")) "installed-settings"
    let tracker = DeploymentSafety.Tracker([|game|],backup,[||])
    tracker.Copy(payload,config)
    write config "user-settings" |> ignore
    equal [|config|] (DeploymentSafety.restore [|game|] backup tracker.Entries)
    equal "user-settings" (File.ReadAllText(config))))
test "Failed repair rolls back to pre-attempt mod, retains original backup" (fun () -> tempTest (fun game backup ->
    let target = write (Path.Combine(game,"game.dll")) "original"
    let v1 = write (Path.Combine(backup,"v1")) "mod-v1"
    let v2 = write (Path.Combine(backup,"v2")) "mod-v2"
    let installed = DeploymentSafety.Tracker([|game|],backup,[||])
    installed.Copy(v1,target)
    let prior = installed.Entries
    installed.Commit()
    let repair = DeploymentSafety.Tracker([|game|],backup,prior)
    repair.Copy(v2,target)
    let newTarget = Path.Combine(game,"added.dll")
    repair.Copy(v2,newTarget)
    equal 0 (repair.Rollback()).Length
    equal "mod-v1" (File.ReadAllText(target))
    equal false (File.Exists(newTarget))
    DeploymentSafety.restore [|game|] backup prior |> ignore
    equal "original" (File.ReadAllText(target))))
test "External setup failure restores overwritten settings and new artifacts" (fun () -> tempTest (fun game backup ->
    let ini = write (Path.Combine(game,"ReShade.ini")) "custom-before"
    let dll = Path.Combine(game,"dxgi.dll")
    let tracker = DeploymentSafety.Tracker([|game|],backup,[||])
    rejects (fun () -> tracker.External([|ini;dll|],fun () ->
        write ini "setup-ini" |> ignore; write dll "setup-dll" |> ignore; failwith "setup failed"))
    equal 0 (tracker.Rollback()).Length
    equal "custom-before" (File.ReadAllText(ini))
    equal false (File.Exists(dll))))
test "Rollback reports external conflicts and retains recovery snapshots" (fun () -> tempTest (fun game backup ->
    let dll = write (Path.Combine(game,"game.dll")) "original"
    let source = write (Path.Combine(backup,"payload")) "mod"
    let tracker = DeploymentSafety.Tracker([|game|],backup,[||])
    tracker.Copy(source,dll)
    write dll "external-update" |> ignore
    if (tracker.Rollback()).Length = 0 then failwith "Recovery falsely reported success"
    equal "external-update" (File.ReadAllText(dll))
    equal 1 (Directory.GetFiles(backup,"recovery.json",SearchOption.AllDirectories).Length)))
test "Out-of-root, duplicate, and symlink targets are rejected" (fun () -> tempTest (fun game backup ->
    let source = write (Path.Combine(backup,"payload")) "mod"
    let tracker = DeploymentSafety.Tracker([|game|],backup,[||])
    rejects (fun () -> tracker.Copy(source, Path.Combine(backup,"outside.dll")))
    let target = Path.Combine(game,"game.dll")
    tracker.Copy(source,target)
    equal 1 (DeploymentSafety.checkRestore [|game|] backup [|tracker.Entries.[0];tracker.Entries.[0]|]).Length
    let link = Path.Combine(game,"linked")
    try
        Directory.CreateSymbolicLink(link,backup) |> ignore
        rejects (fun () -> tracker.Copy(source,Path.Combine(link,"outside.dll")))
    with :? UnauthorizedAccessException -> printfn "SKIP symlink creation requires privileges on this host"))
test "Legacy manifests deserialize but cannot authorize destructive restore" (fun () -> tempTest (fun game backup ->
    let target = write (Path.Combine(game,"old.dll")) "legacy-mod"
    let json = JsonSerializer.Serialize({|TargetPath=target;BackupPath="";WasExisting=false|})
    let legacy = JsonSerializer.Deserialize<DeploymentSafety.InstalledFile>(json)
    equal 1 (DeploymentSafety.checkRestore [|game|] backup [|legacy|]).Length
    equal "legacy-mod" (File.ReadAllText(target))))
test "Deleted tracked hooks restore and empty external observations are omitted" (fun () -> tempTest (fun game backup ->
    let target = write (Path.Combine(game,"old-hook.dll")) "foreign-hook"
    let tracker = DeploymentSafety.Tracker([|game|],backup,[||])
    tracker.Delete(target)
    tracker.External([|Path.Combine(game,"absent.log")|],fun () -> ())
    equal 1 tracker.Entries.Length
    equal 0 (DeploymentSafety.restore [|game|] backup tracker.Entries).Length
    equal "foreign-hook" (File.ReadAllText(target))))
test "Concurrent install/restore lock rejects a second writer" (fun () -> tempTest (fun _ backup ->
    use first = DeploymentSafety.acquireLock backup
    rejects (fun () -> use second = DeploymentSafety.acquireLock backup in ())))
test "Rules import validates schema, exact names and duplicate names" (fun () ->
    let rules = CompatibilityRules.parse """{"Schema":1,"Kind":"033-game-rules","Games":{"Example.EXE":{"Title":"Example","Api":"dx12","Mount":"dxgi.dll","Banned":true}}}"""
    equal "example.exe" rules.[0].Executable
    equal true rules.[0].AntiCheat
    rejects (fun () -> CompatibilityRules.parse """{"Schema":2,"Kind":"033-game-rules","Games":{}}""" |> ignore)
    rejects (fun () -> CompatibilityRules.parse """{"Schema":1,"Kind":"033-game-rules","Games":{"../game.exe":{}}}""" |> ignore)
    rejects (fun () -> CompatibilityRules.parse """{"Schema":1,"Kind":"033-game-rules","Games":{"g.exe":{},"G.EXE":{}}}""" |> ignore))
test "Log tails are bounded and distinguish last NR result from file presence" (fun () -> tempTest (fun game _ ->
    let log = write (Path.Combine(game,"dlss5-033.log")) (String.replicate 10000 "old data\n" + "[033 NR stage] reason=24 pass=2\n[033 NR stage] reason=11\n")
    let text = HealthCheck.readTail log 100
    if Encoding.UTF8.GetByteCount(text) > 100 then failwith "Tail limit exceeded"
    let findings = HealthCheck.analyzeLog "dlss5-033.log" text
    equal false (findings |> Array.exists (fun f -> f.Code = "nr-observed"))
    equal true (findings |> Array.exists (fun f -> f.Code = "nr-stage"))
    equal true (HealthCheck.analyzeLog "dlss5-033.log" "[033 NR stage] reason=24 pass=2" |> Array.exists (fun f -> f.Code = "nr-observed"))
    equal false (HealthCheck.analyzeLog "ReShade.log" "Loaded dxgi.dll" |> Array.exists (fun f -> f.Code = "nr-observed"))))

// Exercise the public uninstaller, including durable restore-point retention.
let withRestorePoint gameDir backupDir action =
    let id = "regression_" + Guid.NewGuid().ToString("N")
    let exe = write (Path.Combine(gameDir,"game.exe")) "fixture"
    let game: DLSS_5_MANAGER.Models.GameItem =
        { AppId=id; Title="Regression"; LauncherTypeName="CUSTOM"; InstallDirectory=gameDir
          TargetExecutablePath=exe; TargetExecutableSize=0L; LocalBannerPath=""; LastManifestTimestamp=0L
          UpscaleStatus=""; DlssVersion=""; FsrVersion=""; XessVersion="" }
    let managerRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DLSS5Manager")
    let manifestPath = Path.Combine(managerRoot,"Installs",id+".json")
    let backups = Path.Combine(managerRoot,"Backups",id)
    Directory.CreateDirectory(backups) |> ignore
    try action game exe manifestPath backups
    finally
        if File.Exists(manifestPath) then File.Delete(manifestPath)
        if Directory.Exists(backups) then Directory.Delete(backups,true)

test "Public uninstall keeps restore point when backup validation fails" (fun () -> tempTest (fun game backup ->
    withRestorePoint game backup (fun item exe manifestPath backups ->
        let target = write (Path.Combine(game,"game.dll")) "original"
        let source = write (Path.Combine(backup,"payload")) "mod"
        let tracker = DeploymentSafety.Tracker([|game|],backups,[||])
        tracker.Copy(source,target)
        let manifest: ModInstaller.InstallManifest =
            { GameId=item.AppId; GameTitle=item.Title; ExecutablePath=exe; InstalledAtUtc=DateTime.UtcNow.ToString("o")
              Mode="dx12"; Arch="64"; Api="dx12"; Neural=""; Files=tracker.Entries }
        DeploymentSafety.writeJsonAtomic manifestPath manifest
        File.Delete(manifest.Files.[0].BackupPath)
        let outcome = ModInstaller.uninstall item exe None (fun _ _ -> ())
        equal false outcome.Success
        equal true (File.Exists(manifestPath))
        equal "mod" (File.ReadAllText(target)))))
test "Public uninstall only removes verified files and retires restore point on success" (fun () -> tempTest (fun game backup ->
    withRestorePoint game backup (fun item exe manifestPath backups ->
        let target = write (Path.Combine(game,"game.dll")) "original"
        let source = write (Path.Combine(backup,"payload")) "mod"
        let unrelated = write (Path.Combine(game,"ReShade.log")) "other-mod-log"
        let tracker = DeploymentSafety.Tracker([|game|],backups,[||])
        tracker.Copy(source,target)
        let manifest: ModInstaller.InstallManifest =
            { GameId=item.AppId; GameTitle=item.Title; ExecutablePath=exe; InstalledAtUtc=DateTime.UtcNow.ToString("o")
              Mode="optiscaler"; Arch="64"; Api="dx12"; Neural=""; Files=tracker.Entries }
        DeploymentSafety.writeJsonAtomic manifestPath manifest
        let outcome = ModInstaller.uninstall item exe None (fun _ _ -> ())
        equal true outcome.Success
        equal false (File.Exists(manifestPath))
        equal "original" (File.ReadAllText(target))
        equal "other-mod-log" (File.ReadAllText(unrelated)))))
test "Foreign install is left untouched without a manager restore point" (fun () -> tempTest (fun game backup ->
    withRestorePoint game backup (fun item exe _ _ ->
        let model = write (Path.Combine(game,"nvngx_dlssnr.dll")) "game-owned-runtime"
        let outcome = ModInstaller.uninstall item exe None (fun _ _ -> ())
        equal false outcome.Success
        equal "game-owned-runtime" (File.ReadAllText(model)))))
test "Switch preflight detects missing payload without touching the game" (fun () -> tempTest (fun game _ ->
    let exe = makePe (Path.Combine(game,"game.exe")) 0x8664 true [|"d3d12.dll"|] false
    let originalHash = hash exe
    let issues = ModInstaller.preflight exe ModInstaller.OptiScalerMode ModInstaller.Bit64 ModInstaller.OptiDx12
    // The source checkout has no mod files payload.
    if issues.Length = 0 then failwith "Missing payload accepted"
    equal originalHash (hash exe)))

// Declarative ZIP import and review-before-write coverage. No supplied binaries execute.
open System.IO.Compression
open System.Security.Cryptography

let digest (bytes: byte[]) = Convert.ToHexString(SHA256.HashData(bytes))
let packageJson (version: string) (files: RuntimePackages.PayloadFile[]) =
    let profile =
        {| Id="legacy-dx12-x64"; Architecture="x64"; Files=files
           Suitability={|Apis=[|"dx12"|];NativeUpscaler="absent";Dx12Runtime="any"|}
           MirrorDirectories=[|"_storage_"|] |}
    let notice = {|Source="notices/LICENSE.txt";Hash=digest (Encoding.UTF8.GetBytes("license"))|}
    JsonSerializer.Serialize({| Kind="033-managed-package"; Schema=1; Version=version; Profiles=[|profile|]; Notices=[|notice|] |})
let zipBytes (path: string) (entries: (string * byte[])[]) =
    use archive = ZipFile.Open(path, ZipArchiveMode.Create)
    for name, bytes in entries do
        let entry = archive.CreateEntry(name)
        use stream = entry.Open()
        stream.Write(bytes, 0, bytes.Length)
    path
let packageFixture folder version transform =
    let binary = makePe (Path.Combine(folder, "fixture.dll")) 0x8664 true [||] false |> File.ReadAllBytes
    let settings = Encoding.UTF8.GetBytes("default settings")
    let payload source target bytes policy role componentName : RuntimePackages.PayloadFile =
        { Source=source;Target=target;Hash=digest bytes;Architecture=(if role="config" then "" else "x64")
          Policy=policy;Role=role;Component=componentName }
    let files = [| payload "payload/hook.dll" "dxgi.dll" binary "replace" "entry" ""
                   payload "payload/core.dll" "033-runtime/core.dll" binary "replace" "engine" ""
                   payload "payload/settings.ini" "ReShade.ini" settings "seed" "config" ""
                   payload "payload/optional.dll" "version.dll" binary "replace" "fg-bridge-loader" "mfg2030" |]
    let json = packageJson version files
    let entries = [| "bundle/033-package.json",Encoding.UTF8.GetBytes(json)
                     "bundle/payload/hook.dll",binary; "bundle/payload/core.dll",binary
                     "bundle/payload/settings.ini",settings; "bundle/payload/optional.dll",binary
                     "bundle/notices/LICENSE.txt",Encoding.UTF8.GetBytes("license")
                     "bundle/installer.ps1",Encoding.UTF8.GetBytes("throw 'must not run'") |]
    zipBytes (Path.Combine(folder,Guid.NewGuid().ToString("N")+".zip")) (transform entries), files
let importFixture folder version =
    let zip, _ = packageFixture folder version id
    RuntimePackages.importZip (Path.Combine(folder,"packages")) zip ignore

test "ZIP import retains versions and notices, excludes installer scripts, and deduplicates" (fun () -> tempTest (fun _ backup ->
    let first = importFixture backup "version-one"
    let again = importFixture backup "version-one"
    let second = importFixture backup "version-two"
    equal first.Id again.Id
    equal false (first.Id = second.Id)
    equal 2 (RuntimePackages.list (Path.Combine(backup,"packages"))).Length
    equal "license" (File.ReadAllText(Path.Combine(first.Root,"notices/LICENSE.txt")))
    equal false (File.Exists(Path.Combine(first.Root,"installer.ps1")))))
test "ZIP traversal, Windows aliases, ADS and duplicate paths are rejected" (fun () -> tempTest (fun _ backup ->
    for path in ["../escape.dll";"C:/escape.dll";"/absolute.dll";"payload/NUL.dll";"payload/x.dll:stream";"payload/trailing. "] do
        let zip, _ = packageFixture backup "invalid" (fun entries -> Array.append entries [|path,[|1uy|]|])
        rejects (fun () -> RuntimePackages.importZip (Path.Combine(backup,"packages")) zip ignore |> ignore)
    let zip, _ = packageFixture backup "invalid" (fun entries -> Array.append entries [|"BUNDLE/PAYLOAD/HOOK.DLL",[|1uy|]|])
    rejects (fun () -> RuntimePackages.importZip (Path.Combine(backup,"packages")) zip ignore |> ignore)))
test "Corrupt or missing package components never enter the version store" (fun () -> tempTest (fun _ backup ->
    for transform in [ (fun entries -> entries |> Array.filter (fun ((p: string),_) -> not (p.EndsWith("core.dll"))))
                       (fun entries -> entries |> Array.map (fun ((p: string),b) -> p, if p.EndsWith("core.dll") then [|0uy|] else b)) ] do
        let zip, _ = packageFixture backup "invalid" transform
        let store = Path.Combine(backup,"packages")
        rejects (fun () -> RuntimePackages.importZip store zip ignore |> ignore)
        equal 0 (RuntimePackages.list store).Length
        if Directory.Exists(store) then equal 0 (Directory.GetDirectories(store)).Length))
test "Manifest rejects duplicate targets, malformed checksums and unknown policies" (fun () -> tempTest (fun _ backup ->
    let _, files = packageFixture backup "fixture" id
    for bad in [ Array.append files [|{files.[0] with Target="DXGI.DLL"}|]
                 [|{files.[0] with Hash="123"}|]
                 [|{files.[0] with Policy="execute"}|]
                 [|{files.[0] with Target="../game.exe"}|] ] do
        rejects (fun () -> RuntimePackages.parse (packageJson "bad" bad) |> ignore)))
test "PE architecture is verified independently from the manifest hash" (fun () -> tempTest (fun _ backup ->
    let zip, _ = packageFixture backup "wrong-arch" (fun entries ->
        entries |> Array.map (fun ((p: string),b) -> p, if p.EndsWith("033-package.json") then Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b).Replace("\"Architecture\":\"x64\"","\"Architecture\":\"x86\"")) else b))
    rejects (fun () -> RuntimePackages.importZip (Path.Combine(backup,"packages")) zip ignore |> ignore)))
test "Stored manifest modification cannot silently change a version" (fun () -> tempTest (fun _ backup ->
    let package = importFixture backup "fixture"
    File.AppendAllText(Path.Combine(package.Root,"033-package.json"), " ")
    rejects (fun () -> RuntimePackages.load (Path.Combine(backup,"packages")) package.Id |> ignore)))
test "Preview has no game writes, preserves seed files and excludes optional components" (fun () -> tempTest (fun game backup ->
    withRestorePoint game backup (fun item exe _ _ ->
        makePe exe 0x8664 true [|"d3d12.dll"|] false |> ignore
        let settings = write (Path.Combine(game,"ReShade.ini")) "user settings"
        let package = importFixture backup "fixture"
        let before = Directory.GetFiles(game,"*",SearchOption.AllDirectories) |> Array.map (fun p -> p,hash p)
        let preview = ModInstaller.previewPackage item exe package "legacy-dx12-x64"
        equal [||] preview.Errors
        equal 3 preview.Rows.Length
        equal "keep" (preview.Rows |> Array.find (fun r -> r.RelativeTarget = "ReShade.ini")).Action
        equal false (preview.Rows |> Array.exists (fun r -> r.RelativeTarget = "version.dll"))
        equal before (Directory.GetFiles(game,"*",SearchOption.AllDirectories) |> Array.map (fun p -> p,hash p))
        equal "user settings" (File.ReadAllText(settings)))))
test "Occupied proxy blocks installation; non-hook replacements are shown and backed up" (fun () -> tempTest (fun game backup ->
    withRestorePoint game backup (fun item exe _ _ ->
        makePe exe 0x8664 true [|"d3d12.dll"|] false |> ignore
        let package = importFixture backup "fixture"
        let hook = write (Path.Combine(game,"dxgi.dll")) "another mod"
        let preview = ModInstaller.previewPackage item exe package "legacy-dx12-x64"
        equal true (preview.Errors |> Array.exists (fun e -> e.Contains("Hook slot")))
        equal false (ModInstaller.installPackage item package preview (fun _ _ -> ())).Success
        equal "another mod" (File.ReadAllText(hook)))))
test "Changed targets, executable and package invalidate reviewed plans before writes" (fun () -> tempTest (fun game backup ->
    withRestorePoint game backup (fun item exe _ _ ->
        makePe exe 0x8664 true [|"d3d12.dll"|] false |> ignore
        let package = importFixture backup "fixture"
        let preview = ModInstaller.previewPackage item exe package "legacy-dx12-x64"
        let settings = write (Path.Combine(game,"ReShade.ini")) "late user config"
        equal false (ModInstaller.installPackage item package preview (fun _ _ -> ())).Success
        equal false (File.Exists(Path.Combine(game,"dxgi.dll")))
        File.Delete(settings)
        let fresh = ModInstaller.previewPackage item exe package "legacy-dx12-x64"
        File.AppendAllText(exe,"game update")
        equal false (ModInstaller.installPackage item package fresh (fun _ _ -> ())).Success
        equal false (File.Exists(Path.Combine(game,"dxgi.dll")))
        let refreshed = ModInstaller.previewPackage item exe package "legacy-dx12-x64"
        File.AppendAllText(Path.Combine(package.Root,"payload/core.dll"),"modified")
        equal false (ModInstaller.installPackage item package refreshed (fun _ _ -> ())).Success
        equal false (File.Exists(Path.Combine(game,"dxgi.dll"))))))
test "Package deploy and restore retain original binaries, user settings and unrelated files" (fun () -> tempTest (fun game backup ->
    withRestorePoint game backup (fun item exe manifest _ ->
        makePe exe 0x8664 true [|"d3d12.dll"|] false |> ignore
        Directory.CreateDirectory(Path.Combine(game,"033-runtime")) |> ignore
        let original = write (Path.Combine(game,"033-runtime/core.dll")) "original core"
        let settings = write (Path.Combine(game,"ReShade.ini")) "my settings"
        let unrelated = write (Path.Combine(game,"other.dll")) "unrelated"
        let package = importFixture backup "fixture"
        let preview = ModInstaller.previewPackage item exe package "legacy-dx12-x64"
        equal [||] preview.Errors
        equal "replace" (preview.Rows |> Array.find (fun r -> r.Target = Path.GetFullPath(original))).Action
        let outcome = ModInstaller.installPackage item package preview (fun _ _ -> ())
        if not outcome.Success then failwith outcome.Message
        equal true (File.Exists(manifest))
        equal "package:legacy-dx12-x64" (ModInstaller.installedMode item)
        equal true (ModInstaller.inspect item exe [||] [||]).Complete
        let again = ModInstaller.previewPackage item exe package "legacy-dx12-x64"
        equal true (again.Errors.Length > 0)
        let restore = ModInstaller.uninstall item exe None (fun _ _ -> ())
        if not restore.Success then failwith restore.Message
        equal false (File.Exists(manifest))
        equal false (File.Exists(Path.Combine(game,"dxgi.dll")))
        equal "original core" (File.ReadAllText(original))
        equal "my settings" (File.ReadAllText(settings))
        equal "unrelated" (File.ReadAllText(unrelated)))))
test "Mid-deployment failure rolls back previous writes and retains no new manifest" (fun () -> tempTest (fun game backup ->
    withRestorePoint game backup (fun item exe manifest _ ->
        makePe exe 0x8664 true [|"d3d12.dll"|] false |> ignore
        let package = importFixture backup "fixture"
        let preview = ModInstaller.previewPackage item exe package "legacy-dx12-x64"
        let outcome = ModInstaller.installPackage item package preview (fun _ progress -> if progress > 0.0 then failwith "simulated disk failure")
        equal false outcome.Success
        equal false (File.Exists(manifest))
        equal false (File.Exists(Path.Combine(game,"dxgi.dll"))))))
test "Existing mirror directories receive runtime files only" (fun () -> tempTest (fun game backup ->
    withRestorePoint game backup (fun item exe _ _ ->
        makePe exe 0x8664 true [|"d3d12.dll"|] false |> ignore
        Directory.CreateDirectory(Path.Combine(game,"_storage_")) |> ignore
        let package = importFixture backup "fixture"
        let preview = ModInstaller.previewPackage item exe package "legacy-dx12-x64"
        equal [||] preview.Errors
        equal true (preview.Rows |> Array.exists (fun r -> r.RelativeTarget = "_storage_/033-runtime/core.dll"))
        equal false (preview.Rows |> Array.exists (fun r -> r.RelativeTarget = "_storage_/dxgi.dll")))))
test "Compatibility explains unknown APIs, bitness mismatch, native DX11 and anti-cheat" (fun () -> tempTest (fun game backup ->
    let package = importFixture backup "fixture"
    let exe = makePe (Path.Combine(game,"game.exe")) 0x8664 true [|"d3d12.dll"|] false
    let facts = PackagePlanning.evidence game exe package
    let profile = package.Manifest.Profiles.[0]
    equal 0 (PackagePlanning.assess facts profile).Reasons.Length
    for invalid in [ {facts with Apis=[||]}; {facts with Architecture="32"}; {facts with AntiCheat=true}; {facts with Apis=[|"vulkan"|]} ] do
        equal true ((PackagePlanning.assess invalid profile).Reasons.Length > 0)
    let native = {profile with Apis=[|"dx11"|];NativeUpscaler="present"}
    equal true ((PackagePlanning.assess {facts with Apis=[|"dx11"|];NativeDlss=true} native).Reasons |> Array.exists (fun r -> r.Contains("unsupported")))))

// REFramework is a required route dependency, including older manifests without Component tags.
let reFixture folder tagged transform =
    let zip, _ = packageFixture folder "re-components" (fun entries ->
        entries |> Array.map (fun ((path: string),bytes) ->
            if not (path.EndsWith("033-package.json")) then path,bytes
            else
                let doc = System.Text.Json.Nodes.JsonNode.Parse(bytes)
                let profile = doc.["Profiles"].[0]
                profile.["Id"] <- System.Text.Json.Nodes.JsonValue.Create("re-nodlss-x64")
                profile.["Suitability"].["EngineMarkers"] <- System.Text.Json.Nodes.JsonNode.Parse("[\"re_chunk_000.pak\"]")
                let loader = profile.["Files"].[3]
                loader.["Target"] <- System.Text.Json.Nodes.JsonValue.Create("dinput8.dll")
                loader.["Role"] <- System.Text.Json.Nodes.JsonValue.Create("mod-loader")
                loader.["Component"] <- System.Text.Json.Nodes.JsonValue.Create(if tagged then "reframework" else "")
                if tagged then profile.["RequiredComponents"] <- System.Text.Json.Nodes.JsonNode.Parse("[\"reframework\"]")
                transform profile
                path,Encoding.UTF8.GetBytes(doc.ToJsonString())))
    RuntimePackages.importZip (Path.Combine(folder,"packages")) zip ignore
let reGame game exe =
    makePe exe 0x8664 true [|"d3d12.dll"|] false |> ignore
    write (Path.Combine(game,"re_chunk_001.pak.patch_002.pak")) "RE engine data" |> ignore

test "RE chunk variants select the dedicated route; DLL names alone do not identify the engine" (fun () -> tempTest (fun game backup ->
    let exe = makePe (Path.Combine(game,"game.exe")) 0x8664 true [|"d3d12.dll"|] false
    write (Path.Combine(game,"dinput8.dll")) "unrelated loader" |> ignore
    equal [||] (GameComponents.reEngineMarkers exe)
    reGame game exe
    let package = reFixture backup false ignore
    let facts = PackagePlanning.evidence game exe package
    equal 0 (PackagePlanning.assess facts package.Manifest.Profiles.[0]).Reasons.Length
    let generic = importFixture backup "generic"
    equal true ((PackagePlanning.assess (PackagePlanning.evidence game exe generic) generic.Manifest.Profiles.[0]).Reasons.Length > 0)
    equal true (ModInstaller.preflight exe ModInstaller.Dx12Auto ModInstaller.Bit64 ModInstaller.OptiDx12 |> Array.exists (fun e -> e.Contains("REFramework")))))

test "RE route without its loader is blocked even if a foreign dinput8.dll exists" (fun () -> tempTest (fun game backup ->
    withRestorePoint game backup (fun item exe _ _ ->
        reGame game exe
        let dll = write (Path.Combine(game,"dinput8.dll")) "unknown existing loader"
        let package = reFixture backup false (fun p -> p.["Files"].AsArray().RemoveAt(3))
        let preview = ModInstaller.previewPackage item exe package "re-nodlss-x64"
        equal true (preview.Errors |> Array.exists (fun e -> e.Contains("missing REFramework")))
        equal "缺失或无效 / Missing or invalid" preview.Components.[0].State
        equal false (ModInstaller.installPackage item package preview (fun _ _ -> ())).Success
        equal "unknown existing loader" (File.ReadAllText(dll)))))

test "Bundled and explicitly tagged REFramework install and restore with the route" (fun () -> tempTest (fun game backup ->
    withRestorePoint game backup (fun item exe _ _ ->
        reGame game exe
        Directory.CreateDirectory(Path.Combine(game,"reframework/autorun")) |> ignore
        let script = write (Path.Combine(game,"reframework/autorun/user.lua")) "user mod"
        for tagged in [false;true] do
            let package = reFixture backup tagged ignore
            let preview = ModInstaller.previewPackage item exe package "re-nodlss-x64"
            equal [||] preview.Errors
            equal "随路线安装 / Included in installation" preview.Components.[0].State
            equal true (PackagePlanning.format preview |> fun t -> t.Contains("REFramework"))
            equal true (ModInstaller.installPackage item package preview (fun _ _ -> ())).Success
            equal true (File.Exists(Path.Combine(game,"dinput8.dll")))
            equal true (ModInstaller.uninstall item exe None (fun _ _ -> ())).Success
            equal false (File.Exists(Path.Combine(game,"dinput8.dll")))
            equal "user mod" (File.ReadAllText(script)))))

test "Matching pre-existing REFramework stays unowned and survives route restore" (fun () -> tempTest (fun game backup ->
    withRestorePoint game backup (fun item exe _ _ ->
        reGame game exe
        let package = reFixture backup false ignore
        let loader = package.Manifest.Profiles.[0].Files |> Array.find (fun f -> f.Target = "dinput8.dll")
        let dll = Path.Combine(game,"dinput8.dll")
        File.Copy(RuntimePackages.resolve package.Root loader.Source,dll)
        let preview = ModInstaller.previewPackage item exe package "re-nodlss-x64"
        equal [||] preview.Errors
        equal "与运行包一致，将保留 / Matches package; kept" preview.Components.[0].State
        equal true (ModInstaller.installPackage item package preview (fun _ _ -> ())).Success
        equal true (ModInstaller.uninstall item exe None (fun _ _ -> ())).Success
        equal loader.Hash (hash dll))))

test "REFramework conflicts and changes after review prevent all writes" (fun () -> tempTest (fun game backup ->
    withRestorePoint game backup (fun item exe _ _ ->
        reGame game exe
        let package = reFixture backup true ignore
        let clean = ModInstaller.previewPackage item exe package "re-nodlss-x64"
        let dll = write (Path.Combine(game,"dinput8.dll")) "another mod version"
        equal false (ModInstaller.installPackage item package clean (fun _ _ -> ())).Success
        let conflict = ModInstaller.previewPackage item exe package "re-nodlss-x64"
        equal "冲突 / Conflict" conflict.Components.[0].State
        equal false (ModInstaller.installPackage item package conflict (fun _ _ -> ())).Success
        equal false (File.Exists(Path.Combine(game,"dxgi.dll")))
        equal "another mod version" (File.ReadAllText(dll)))))

test "Invalid REFramework declarations and unknown required components cannot bypass checks" (fun () -> tempTest (fun game backup ->
    let exe = makePe (Path.Combine(game,"game.exe")) 0x8664 true [|"d3d12.dll"|] false
    reGame game exe
    for field,value in ["Policy","seed";"Role","entry"] do
        let package = reFixture backup true (fun p -> p.["Files"].[3].[field] <- System.Text.Json.Nodes.JsonValue.Create(value))
        equal true ((PackagePlanning.create game exe backup false package "re-nodlss-x64").Errors.Length > 0)
    let package = reFixture backup true (fun p -> p.["RequiredComponents"] <- System.Text.Json.Nodes.JsonNode.Parse("[\"mfg2030\"]"))
    equal true ((PackagePlanning.create game exe backup false package "re-nodlss-x64").Errors |> Array.exists (fun e -> e.Contains("Unsupported required component")))
    rejects (fun () -> reFixture backup true (fun p -> p.["RequiredComponents"] <- System.Text.Json.Nodes.JsonNode.Parse("[\"reframework\",\"reframework\"]")) |> ignore)))

printfn "\n%d passed, %d failed" passed failed
if failed > 0 then exit 1
