#r "../bin/Debug/net8.0/SharpCompress.dll"
#load "RegressionTests.fsx"
#load "../Services/ComponentReleases.fs"
#load "../Services/ComponentArchives.fs"
#load "../Services/ComponentPackages.fs"

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.Services.ComponentReleases
open RegressionTests

let ct = CancellationToken.None
let run work = Async.RunSynchronously(work)
let assetFor sourceId name (bytes: byte[]) : Asset =
    let s = source sourceId
    { Id=123L;Name=name;Url="https://github.com/"+s.Repository+"/releases/download/v1/"+name
      Size=int64 bytes.Length;Digest="sha256:"+(digest bytes).ToLowerInvariant();Updated="2026-09-13T00:00:00Z" }
let releaseFor (asset: Asset) : Release = { Id=456L;Tag="v1";Published=DateTimeOffset.UtcNow;Preview=false;Assets=[|asset|] }
let response (bytes: byte[]) (_: Uri) (_: CancellationToken) = async {
    let response = new HttpResponseMessage(HttpStatusCode.OK)
    response.Content <- new ByteArrayContent(bytes)
    return response }
let cacheFixture folder sourceId name entries =
    let zip = zipBytes (Path.Combine(folder,Guid.NewGuid().ToString("N")+".zip")) entries
    let bytes = File.ReadAllBytes(zip)
    let asset = assetFor sourceId name bytes
    downloadUsing (response bytes) (Path.Combine(folder,"downloads")) (source sourceId) (releaseFor asset) asset ignore ct |> run
let modelBase folder =
    let package = importFixture folder "online-base"
    let binary = package.Manifest.Profiles.[0].Files.[0] |> RuntimePackages.verifyFile package.Root
    let files =
        [| for f in package.Manifest.Profiles.[0].Files do yield f,RuntimePackages.verifyFile package.Root f
           for name in ["nvngx_dlss.dll";"nvngx_dlssnr.dll"] do
               yield {package.Manifest.Profiles.[0].Files.[0] with Target="033-runtime/"+name;Role="model"},binary |]
    RuntimePackages.compose (Path.Combine(folder,"packages")) "models-base" package.Manifest.Profiles.[0] files [||] [||]
let onlineFixture folder id =
    let bytes = makePe (Path.Combine(folder,Guid.NewGuid().ToString("N")+".dll")) 0x8664 true [||] false |> File.ReadAllBytes
    if id="feeder" then cacheFixture folder id "DLSS5-Feeder-v1.zip" [|"dlss5-feed.addon64",bytes;"reshade-shaders/Shaders/DLSS5_Feed.fx",Encoding.UTF8.GetBytes("shader")|]
    elif id="renodx" then cacheFixture folder id "renodx-dlss5_v1.zip" [|"renodx-dlss5.addon64",bytes;"install.ps1",Encoding.UTF8.GetBytes("throw 'must not run'")|]
    else cacheFixture folder id "REFramework.zip" [|"dinput8.dll",bytes|]

test "Catalog orders publication dates and excludes beta tags even when GitHub marks them stable" (fun () ->
    let bytes=[|1uy|]
    let asset=assetFor "feeder" "DLSS5-Feeder-v1.zip" bytes
    let item tag date draft marked assetName =
        {| id=456L;tag_name=tag;published_at=date;draft=draft;prerelease=marked
           assets=[|{|id=asset.Id;name=assetName;state="uploaded";browser_download_url=asset.Url;size=asset.Size;digest=asset.Digest;updated_at=asset.Updated|}|] |}
    let json=JsonSerializer.Serialize([|item "v2-beta.1" "2026-09-13T00:00:00Z" false false asset.Name;
                                      item "v1" "2026-09-11T00:00:00Z" false false asset.Name;
                                      item "v9" "2026-09-14T00:00:00Z" true false asset.Name;
                                      item "v1.5" "2026-09-12T00:00:00Z" false false asset.Name;
                                      item "v10" "2026-09-15T00:00:00Z" false false "installer.exe"|])
    equal [|"v1.5";"v1"|] (parseReleases (source "feeder") false json |> Array.map (fun r -> r.Tag))
    equal "v2-beta.1" (parseReleases (source "feeder") true json).[0].Tag)

test "Component download URLs stay within the selected repository" (fun () ->
    let asset=assetFor "feeder" "DLSS5-Feeder-v1.zip" [|1uy|]
    for url in ["http://github.com/jlrouzies-fr/DLSS5-Feeder/releases/download/v1/a.zip";"https://github.com/other/repo/releases/download/v1/a.zip";"https://github.com.evil.test/file.zip";"https://github.com:444/jlrouzies-fr/DLSS5-Feeder/releases/download/v1/a.zip"] do
        rejects (fun () -> validateAssetUrl (source "feeder") {asset with Url=url}))

test "Downloads validate hashes, reuse cache without a request and distinguish replaced release assets" (fun () -> tempTest (fun _ folder ->
    let bytes=Encoding.UTF8.GetBytes("download payload")
    let asset=assetFor "feeder" "DLSS5-Feeder-v1.zip" bytes
    let release=releaseFor asset
    let store=Path.Combine(folder,"cache")
    let cached=downloadUsing (response bytes) store (source "feeder") release asset ignore ct |> run
    let noNetwork _ _ = async {return failwith "cache unexpectedly requested the network"}
    let again=downloadUsing noNetwork store (source "feeder") release asset ignore ct |> run
    equal cached.Root again.Root
    equal false (cacheKey (source "feeder") release asset = cacheKey (source "feeder") release {asset with Id=124L})
    File.AppendAllText(verifyCached cached,"tampering")
    rejects (fun () -> verifyCached cached |> ignore)
    let repaired=downloadUsing (response bytes) store (source "feeder") release asset ignore ct |> run
    equal bytes (File.ReadAllBytes(verifyCached repaired))))

test "Truncated, corrupt and cancelled downloads do not activate cache entries" (fun () -> tempTest (fun _ folder ->
    let bytes=[|1uy;2uy;3uy|]
    let asset=assetFor "feeder" "DLSS5-Feeder-v1.zip" bytes
    for payload in [ [|1uy|]; [|3uy;2uy;1uy|] ] do
        let store=Path.Combine(folder,Guid.NewGuid().ToString("N"))
        rejects (fun () -> downloadUsing (response payload) store (source "feeder") (releaseFor asset) asset ignore ct |> run |> ignore)
        equal false (Directory.Exists(Path.Combine(store,cacheKey (source "feeder") (releaseFor asset) asset)))
        equal 0 (Directory.GetDirectories(store,".download-*")).Length
    use cancelled=new CancellationTokenSource()
    cancelled.Cancel()
    rejects (fun () -> downloadUsing (response bytes) (Path.Combine(folder,"cancelled")) (source "feeder") (releaseFor asset) asset ignore cancelled.Token |> run |> ignore)))

test "Archive extraction rejects traversal, duplicate names and links" (fun () -> tempTest (fun _ folder ->
    for names in [[|"../outside.dll"|];[|"dinput8.dll";"DINPUT8.DLL"|];[|"C:/outside.dll"|];[|"payload/NUL.dll"|]] do
        let zip=zipBytes (Path.Combine(folder,Guid.NewGuid().ToString("N")+".zip")) (names |> Array.map (fun n -> n,[|1uy|]))
        rejects (fun () -> ComponentArchives.extract zip (Path.Combine(folder,Guid.NewGuid().ToString("N"))) ct |> ignore)
    let zip=Path.Combine(folder,"link.zip")
    do
        use archive=System.IO.Compression.ZipFile.Open(zip,System.IO.Compression.ZipArchiveMode.Create)
        let entry=archive.CreateEntry("link")
        entry.ExternalAttributes <- (0xA1FF <<< 16)
        use writer=new StreamWriter(entry.Open())
        writer.Write("../outside")
    rejects (fun () -> ComponentArchives.extract zip (Path.Combine(folder,"links")) ct |> ignore)))

test "REFramework release assets match the game and retain explicit old TDB variants" (fun () ->
    let a=assetFor "reframework" "RE2_TDB66.zip" [|1uy|]
    equal true (ComponentPackages.reAssetMatches "re2.exe" a)
    equal false (ComponentPackages.reAssetMatches "re3.exe" a)
    equal a (ComponentPackages.chooseAsset (source "reframework") "re2.exe" a.Name (releaseFor a))
    rejects (fun () -> ComponentPackages.chooseAsset (source "reframework") "unknown.exe" "" (releaseFor a) |> ignore))

test "Online Feeder and RenoDX replace the 033 consumer, record versions and restore user files" (fun () -> tempTest (fun game folder ->
    withRestorePoint game folder (fun item exe _ _ ->
        makePe exe 0x8664 true [|"d3d12.dll"|] false |> ignore
        let baseline=modelBase folder
        let feeder,reno=onlineFixture folder "feeder",onlineFixture folder "renodx"
        let package=ComponentPackages.build (Path.Combine(folder,"packages")) game exe (Some baseline) baseline.Manifest.Profiles.[0].Id [|feeder;reno|] ct ignore
        equal 3 package.Manifest.Provenance.Length
        let targets=package.Manifest.Profiles.[0].Files |> Array.map (fun f -> f.Target)
        for name in ["dlss5-feed.addon64";"renodx-dlss5.addon64";"nvngx_dlss.dll";"nvngx_dlssnr.dll"] do equal true (Array.contains name targets)
        equal false (Array.contains "033-runtime/core.dll" targets)
        equal false (targets |> Array.exists (fun n -> n.EndsWith(".ps1")))
        let preview=ModInstaller.previewPackage item exe package package.Manifest.Profiles.[0].Id
        equal [||] preview.Errors
        equal true (PackagePlanning.format preview |> fun s -> s.Contains("RankFTW/rhi-repo"))
        equal true (ModInstaller.installPackage item package preview (fun _ _ -> ())).Success
        let script=write (Path.Combine(game,"user.lua")) "user script"
        equal true (ModInstaller.uninstall item exe None (fun _ _ -> ())).Success
        equal false (File.Exists(Path.Combine(game,"renodx-dlss5.addon64")))
        equal "user script" (File.ReadAllText(script)))))

test "Online REFramework replacement keeps the imported base version immutable" (fun () -> tempTest (fun game folder ->
    withRestorePoint game folder (fun item exe _ _ ->
        reGame game exe
        let baseline=reFixture folder false ignore
        let original=baseline.Manifest.Profiles.[0].Files |> Array.find (fun f -> f.Target="dinput8.dll")
        let online=onlineFixture folder "reframework-nightly"
        let package=ComponentPackages.build (Path.Combine(folder,"packages")) game exe (Some baseline) baseline.Manifest.Profiles.[0].Id [|online|] ct ignore
        equal false (baseline.Id=package.Id)
        RuntimePackages.verify baseline
        equal original.Hash (DeploymentSafety.hashFile (RuntimePackages.resolve baseline.Root original.Source))
        equal [||] (ModInstaller.previewPackage item exe package package.Manifest.Profiles.[0].Id).Errors)))

test "Consumer conflicts and incomplete online component combinations are blocked" (fun () -> tempTest (fun game folder ->
    withRestorePoint game folder (fun item exe _ _ ->
        makePe exe 0x8664 true [|"d3d12.dll"|] false |> ignore
        let baseline=modelBase folder
        let feeder,reno=onlineFixture folder "feeder",onlineFixture folder "renodx"
        rejects (fun () -> ComponentPackages.build (Path.Combine(folder,"packages")) game exe (Some baseline) baseline.Manifest.Profiles.[0].Id [|feeder|] ct ignore |> ignore)
        let package=ComponentPackages.build (Path.Combine(folder,"packages")) game exe (Some baseline) baseline.Manifest.Profiles.[0].Id [|feeder;reno|] ct ignore
        write (Path.Combine(game,"renodx-dlss-old.addon64")) "foreign consumer" |> ignore
        let preview=ModInstaller.previewPackage item exe package package.Manifest.Profiles.[0].Id
        equal true (preview.Errors |> Array.exists (fun e -> e.Contains("Competing neural component")))
        equal false (ModInstaller.installPackage item package preview (fun _ _ -> ())).Success)))

test "32-bit Feeder keeps its 64-bit worker paired and rejects missing or wrong-architecture workers" (fun () -> tempTest (fun game folder ->
    withRestorePoint game folder (fun item exe _ _ ->
        makePe exe 0x14c false [|"d3d11.dll"|] false |> ignore
        let baseline=modelBase folder
        let oldProfile=baseline.Manifest.Profiles.[0]
        let client=makePe (Path.Combine(folder,"client.dll")) 0x14c false [||] false
        let host=makePe (Path.Combine(folder,"host.dll")) 0x8664 true [||] false
        let profile={oldProfile with Id="legacy-d3d10-11-x86";Architecture="x86";Apis=[|"dx11"|]}
        let files=[|for f in oldProfile.Files do
                       if f.Role="entry" then
                           yield {f with Architecture="x86";Hash=hash client},client
                       else
                           let target=if f.Target.StartsWith("033-runtime/") then f.Target.Replace("033-runtime/","033-runtime/host64/033-runtime/") else f.Target
                           yield {f with Target=target},RuntimePackages.verifyFile baseline.Root f
                    yield {oldProfile.Files.[0] with Target="033-runtime/host64/dxgi.dll";Role="host-present";Hash=hash host},host |]
        let baseline=RuntimePackages.compose (Path.Combine(folder,"packages")) "x86-base" profile files [||] [||]
        let clientBytes,hostBytes=File.ReadAllBytes(client),File.ReadAllBytes(host)
        let parts=[|"dlss5-feed.addon32",clientBytes;"reshade-shaders\\Shaders\\DLSS5_Feed.fx",Encoding.UTF8.GetBytes("shader")|]
        let make worker=cacheFixture folder "feeder" "DLSS5-Feeder-v1.zip" (Array.append parts worker)
        let reno=onlineFixture folder "renodx"
        let build feeder=ComponentPackages.build (Path.Combine(folder,"packages")) game exe (Some baseline) profile.Id [|feeder;reno|] ct ignore
        rejects (fun () -> build (make [||]) |> ignore)
        rejects (fun () -> build (make [|"host64\\dlss5-feed-host64.exe",clientBytes|]) |> ignore)
        let package=build (make [|"host64\\dlss5-feed-host64.exe",hostBytes|])
        let targets=package.Manifest.Profiles.[0].Files |> Array.map (fun f -> f.Target)
        for target in ["dlss5-feed.addon32";"host64/dlss5-feed-host64.exe";"host64/dxgi.dll";"host64/renodx-dlss5.addon64";"host64/nvngx_dlssnr.dll";"host64/nvngx_dlss.dll"] do equal true (Array.contains target targets)
        equal [||] (ModInstaller.previewPackage item exe package profile.Id).Errors)))

test "Aurora enables only NR, keeps runtime paths, excludes scripts and overrides both NR model locations" (fun () -> tempTest (fun game folder ->
    withRestorePoint game folder (fun item exe _ _ ->
        makePe exe 0x8664 true [|"d3d12.dll"|] false |> ignore
        let pe=makePe (Path.Combine(folder,"aurora.dll")) 0x8664 true [||] false |> File.ReadAllBytes
        File.WriteAllBytes(Path.Combine(game,"nvngx_dlss.dll"),pe)
        let aurora=cacheFixture folder "aurora" "OptiScaler_Aurora_v1.zip" [|
            "OptiScaler.dll",pe;"nvngx.dll_dlssnr.dll",pe
            "OptiScaler/nvngx_dlssnr.dll",pe;"OptiScaler/nvngx_dlss.dll",pe
            "OptiScaler/streamline/sl.common.dll",pe
            "OptiScaler.ini",Encoding.UTF8.GetBytes("[Other]\nEnabled=false\n[DlssNr]\nEnabled=false\n[Following]\nEnabled=false\n")
            "setup_windows.bat",Encoding.UTF8.GetBytes("exit 1")
            "runtime_sync.ps1",Encoding.UTF8.GetBytes("throw 'must not run'")
            "Licenses/RenoDX_ATTRIBUTION.txt",Encoding.UTF8.GetBytes("attribution") |]
        let nrBytes=Array.copy pe
        nrBytes.[4095] <- 42uy
        let nr=cacheFixture folder "dlssnr" "nvngx_dlssnr_1.0.zip" [|"nvngx_dlssnr.dll",nrBytes|]
        let package=ComponentPackages.build (Path.Combine(folder,"packages")) game exe None "" [|aurora;nr|] ct ignore
        let files=package.Manifest.Profiles.[0].Files
        for target in ["nvngx_dlssnr.dll";"OptiScaler/nvngx_dlssnr.dll"] do
            let file=files |> Array.find (fun f -> f.Target=target)
            equal (digest nrBytes) file.Hash
        let ini=files |> Array.find (fun f -> f.Target="OptiScaler.ini") |> RuntimePackages.verifyFile package.Root |> File.ReadAllText
        equal "[Other]\nEnabled=false\n[DlssNr]\nEnabled=true\n[Following]\nEnabled=false\n" ini
        equal true (files |> Array.exists (fun f -> f.Target="OptiScaler/streamline/sl.common.dll"))
        equal false (files |> Array.exists (fun f -> f.Target.EndsWith(".bat") || f.Target.EndsWith(".ps1")))
        equal 1 package.Manifest.Notices.Length
        equal [||] (ModInstaller.previewPackage item exe package package.Manifest.Profiles.[0].Id).Errors)))

test "A composed Feeder and RenoDX base can replace its consumer version without duplicate files or notices" (fun () -> tempTest (fun game folder ->
    let exe=makePe (Path.Combine(game,"game.exe")) 0x8664 true [|"d3d12.dll"|] false
    let baseline=modelBase folder
    let feeder=onlineFixture folder "feeder"
    let pe=makePe (Path.Combine(folder,"reno.dll")) 0x8664 true [||] false |> File.ReadAllBytes
    let reno=cacheFixture folder "renodx" "renodx-dlss5_v1.zip" [|"renodx-dlss5.addon64",pe;"README.txt",Encoding.UTF8.GetBytes("old notes")|]
    let build baseline reno=ComponentPackages.build (Path.Combine(folder,"packages")) game exe (Some baseline) baseline.Manifest.Profiles.[0].Id [|feeder;reno|] ct ignore
    let first=build baseline reno
    pe.[4095] <- 17uy
    let updated=cacheFixture folder "renodx" "renodx-dlss5_v2.zip" [|"renodx-dlss5.addon64",pe;"README.txt",Encoding.UTF8.GetBytes("new notes")|]
    let next=build first updated
    equal false (next.Id=first.Id)
    let consumer=next.Manifest.Profiles.[0].Files |> Array.filter (fun f -> f.Role="consumer")
    equal 1 consumer.Length
    equal (digest pe) consumer.[0].Hash
    equal "new notes" (File.ReadAllText(RuntimePackages.resolve next.Root "notices/online/renodx/README.txt"))))

printfn "\nAll suites: %d passed, %d failed" RegressionTests.passed RegressionTests.failed
if RegressionTests.failed>0 then exit 1
