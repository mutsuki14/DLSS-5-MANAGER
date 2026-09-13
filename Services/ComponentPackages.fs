namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Threading
open DLSS_5_MANAGER.Services.RuntimePackages
open DLSS_5_MANAGER.Services.ComponentReleases

/// Explicit adapters turn release payloads into immutable, reviewable package versions.
module ComponentPackages =
    let reAssetMatches (exe: string) (asset: Asset) =
        let game = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant()
        let prefix =
            match game with
            | "re2" -> "RE2" | "re3" -> "RE3" | "re4" -> "RE4" | "re7" -> "RE7" | "re8" -> "RE8"
            | "devilmaycry5" | "dmc5" -> "DMC5" | "monsterhunterrise" -> "MHRISE"
            | "monsterhunterwilds" -> "MHWILDS" | "streetfighter6" -> "SF6" | "dd2" -> "DD2"
            | _ -> ""
        prefix <> "" && (asset.Name.Equals(prefix+".zip",StringComparison.OrdinalIgnoreCase) || asset.Name.StartsWith(prefix+"_TDB",StringComparison.OrdinalIgnoreCase))

    let chooseAsset (s: Source) exe preferred (release: Release) =
        let candidates = release.Assets |> Array.filter (fun a -> s.Id <> "reframework" || reAssetMatches exe a)
        candidates |> Array.tryFind (fun a -> a.Name = preferred)
        |> Option.orElseWith (fun () -> candidates |> Array.sortByDescending (fun a -> a.Updated,a.Id) |> Array.tryFind (fun a -> not (a.Name.Contains("_TDB"))))
        |> Option.defaultWith (fun () -> invalidOp "此正式版没有适合该 EXE 的资源，请选择通用 REFramework Nightly，或确认真实游戏 EXE / No matching asset for this executable.")

    let private payload (target: string) role policy sourcePath =
        let arch =
            if Regex.IsMatch(target,"(?i)\\.(dll|exe|addon32|addon64)$") then
                match PeInspection.inspect sourcePath with
                | Some pe when pe.Architecture = "64" -> "x64"
                | Some pe when pe.Architecture = "32" -> "x86"
                | _ -> invalidOp ("组件不是受支持的 PE 文件 / Invalid component PE: " + target)
            else ""
        { Source="payload/temporary";Target=RuntimePackages.relativePath target;Hash=DeploymentSafety.hashFile sourcePath;Architecture=arch;Policy=policy;Role=role;Component="" },sourcePath

    let private exactlyOne name (paths: string[]) =
        match paths |> Array.filter (fun p -> Path.GetFileName(p).Equals(name,StringComparison.OrdinalIgnoreCase)) with
        | [|path|] -> path
        | _ -> invalidOp ("Release 必须包含唯一文件 / Expected one payload: " + name)

    let private iniSet (section: string) key value (text: string) =
        let lines = text.Replace("\r\n","\n").Split('\n') |> ResizeArray<string>
        let heading = "["+section+"]"
        let start = lines |> Seq.tryFindIndex (fun line -> line.Trim().Equals(heading,StringComparison.OrdinalIgnoreCase))
        match start with
        | None -> lines.Add(heading); lines.Add(key+"="+value)
        | Some i ->
            let finish = [i+1..lines.Count-1] |> List.tryFind (fun n -> lines.[n].TrimStart().StartsWith("[")) |> Option.defaultValue lines.Count
            match [i+1..finish-1] |> List.tryFind (fun n -> lines.[n].TrimStart().StartsWith(key+"=",StringComparison.OrdinalIgnoreCase)) with
            | Some n -> lines.[n] <- key+"="+value
            | None -> lines.Insert(finish,key+"="+value)
        String.Join("\n",lines)

    let build store gameDir exe (basePackage: Package option) profileId (downloads: Cached[]) (ct: CancellationToken) report =
        let selected = downloads |> Array.map (fun c -> (source c.Source).Group,c)
        if selected.Length = 0 || (selected |> Array.map fst |> Array.distinct).Length <> selected.Length then invalidOp "Select at most one version of each component."
        let has group = selected |> Array.exists (fun (g,_) -> g=group)
        let reGame = (GameComponents.reEngineMarkers exe).Length > 0
        let baseProfile = basePackage |> Option.map (fun p -> p.Manifest.Profiles |> Array.find (fun r -> r.Id=profileId))
        if not (has "optiscaler") && baseProfile.IsNone then invalidOp "先选择已导入的 033 运行包和路线，或添加 OptiScaler Aurora 完整运行包 / Select a base package or OptiScaler Aurora."
        if has "optiscaler" && (has "feeder" || has "renodx") then invalidOp "Aurora 与 Feeder/RenoDX 使用不同安装路线，请为本次安装选择其中一套 / Choose Aurora or the Feeder/RenoDX route."
        if has "feeder" && not (has "renodx") then invalidOp "更新 Feeder 时也需选择 RenoDX 消费端，避免混用 033 内核 / Select RenoDX alongside Feeder."
        if not (has "optiscaler") && (baseProfile |> Option.exists (fun p -> p.Id.StartsWith("online-aurora"))) then invalidOp "更新 Aurora 时需同时选择 Aurora 版本；切换 Feeder/RenoDX 路线请使用 033 基底包 / Select Aurora to update it, or choose a 033 base for Feeder/RenoDX."
        if has "renodx" && (baseProfile |> Option.exists (fun p -> p.NativeUpscaler="absent" && GameComponents.isReRoute p)) then invalidOp "RE 无原生 DLSS 路线目前保留 033 内核，可更新 REFramework 或模型；尚不支持替换 RenoDX / RenoDX replacement is not supported on the non-native RE route yet."
        if has "renodx" && (baseProfile |> Option.exists (fun p -> p.NativeUpscaler="absent")) && not (has "feeder") then invalidOp "无原生 DLSS 的路线需要同时选择 Feeder / This route also needs Feeder."
        if has "feeder" && (baseProfile |> Option.exists GameComponents.isReRoute) then invalidOp "RE 专用路线不使用通用 Feeder；原生 DLSS 路线可选择 REFramework 和 RenoDX / The RE route does not use generic Feeder."
        let work = Path.Combine(Path.GetFullPath(store),".online-"+Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(work) |> ignore
        try
            let extracted = selected |> Array.map (fun (group,cached) ->
                ct.ThrowIfCancellationRequested()
                report ("解压并检查 / Extracting: " + cached.Receipt.Asset.Name)
                let archive = verifyCached cached
                let root = Path.Combine(work,group)
                let paths = ComponentArchives.extract archive root ct
                group,(cached,root,paths)) |> Map.ofArray
            let files = ResizeArray<PayloadFile * string>()
            let notices = ResizeArray<string * string>()
            let add target role policy path = files.Add(payload target role policy path)
            let replace target role policy path =
                for i in files.Count-1 .. -1 .. 0 do if (fst files.[i]).Target.Equals(target,StringComparison.OrdinalIgnoreCase) then files.RemoveAt(i)
                add target role policy path
            let get group = let _,_,paths = extracted.[group] in paths
            let mutable profile =
                if has "optiscaler" then
                    { Id=(if reGame then "online-aurora-re-x64" else "online-aurora-x64");Architecture="x64";Files=[||];Apis=[|"dx12"|];NativeUpscaler="present";Dx12Runtime="present"
                      EngineMarkers=(if reGame then [|"re_chunk_000.pak"|] else [||]);ExecutableNames=[||];Mirrors=[||];Protocol="Aurora · 本地代理安装 / local proxy installation";RequiredComponents=(if reGame then [|"reframework"|] else [||]) }
                else baseProfile.Value
            if has "optiscaler" then
                let _,root,paths = extracted.["optiscaler"]
                let main = exactlyOne "OptiScaler.dll" paths
                let prefix = Path.GetDirectoryName(main)
                add "dxgi.dll" "entry" "replace" main
                add "nvngx.dll_dlssnr.dll" "nr-forwarder" "replace" (exactlyOne "nvngx.dll_dlssnr.dll" paths)
                let ini = exactlyOne "OptiScaler.ini" paths
                // The installation requests neural rendering; scope the change to that section only.
                let configured = Path.Combine(work,"Aurora.ini")
                let original = File.ReadAllText(ini)
                if not (original.Contains("[DlssNr]",StringComparison.OrdinalIgnoreCase)) then invalidOp "Unknown Aurora NR configuration format."
                File.WriteAllText(configured,iniSet "DlssNr" "Enabled" "true" original)
                add "OptiScaler.ini" "config" "seed" configured
                for path in paths do
                    let relative = Path.GetRelativePath(prefix,path).Replace('\\','/')
                    if relative.StartsWith("OptiScaler/",StringComparison.OrdinalIgnoreCase) && Path.GetExtension(path).Equals(".dll",StringComparison.OrdinalIgnoreCase) then add relative "runtime" "replace" path
                let bundledNr = paths |> Array.filter (fun path -> Path.GetFileName(path).Equals("nvngx_dlssnr.dll",StringComparison.OrdinalIgnoreCase))
                if bundledNr.Length = 1 then add "nvngx_dlssnr.dll" "model" "replace" bundledNr.[0]
                if not (files |> Seq.exists (fun (f,_) -> f.Target.Equals("OptiScaler/nvngx_dlssnr.dll",StringComparison.OrdinalIgnoreCase))) && not (has "dlssnr") then invalidOp "Aurora Release 缺少 NR 模型，请添加 DLSS Neural Rendering 版本 / Add an NR model version."
                if reGame && not (has "reframework") then
                    match basePackage,baseProfile with
                    | Some p,Some route when GameComponents.isReRoute route ->
                        let loader = route.Files |> Array.find (fun f -> f.Target="dinput8.dll")
                        add "dinput8.dll" "mod-loader" "replace" (RuntimePackages.verifyFile p.Root loader)
                    | _ -> invalidOp "RE 游戏的 Aurora 路线需要同时选择 REFramework / Select REFramework for this RE game."
                ignore root
            else
                let p = basePackage.Value
                let replacingConsumer = has "renodx"
                for f in profile.Files |> Array.filter (GameComponents.deploys profile) do
                    let obsolete = replacingConsumer && ([|"engine";"consumer";"nr-forwarder";"fg-provider";"feeder-addon";"feeder-client";"feeder-worker"|] |> Array.contains f.Role || f.Target.EndsWith("dlss5-033.cfg",StringComparison.OrdinalIgnoreCase))
                    if not obsolete then
                        let target =
                            if replacingConsumer && profile.Architecture="x86" then f.Target.Replace("033-runtime/host64/033-runtime/","host64/").Replace("033-runtime/host64/","host64/")
                            elif replacingConsumer && (f.Role="model" || f.Role="crt") then Path.GetFileName(f.Target)
                            else f.Target
                        files.Add({f with Target=target}, RuntimePackages.verifyFile p.Root f)
                for relative,_ in p.Manifest.Notices do
                    if not (selected |> Array.exists (fun (group,_) -> relative.StartsWith("notices/online/"+group+"/",StringComparison.OrdinalIgnoreCase))) then
                        notices.Add(relative,RuntimePackages.resolve p.Root relative)
            if has "renodx" then
                let paths = get "renodx"
                let candidates = paths |> Array.filter (fun p -> Regex.IsMatch(Path.GetFileName(p),"^renodx-dlss(5)?([-.][0-9.]+)?\\.addon64$",RegexOptions.IgnoreCase))
                if candidates.Length <> 1 then invalidOp "Expected exactly one RenoDX DLSS add-on."
                let host = if profile.Architecture="x86" then "host64/" else ""
                replace (host+Path.GetFileName(candidates.[0])) "consumer" "replace" candidates.[0]
                if not (files |> Seq.exists (fun (f,_) -> f.Target.Equals(host+"nvngx_dlssnr.dll",StringComparison.OrdinalIgnoreCase))) && not (has "dlssnr") then invalidOp "RenoDX requires a selected or bundled DLSS NR model."
                profile <- {profile with Protocol="在线 RenoDX 组合 / Online RenoDX combination";Mirrors=[||]}
            if has "feeder" then
                let paths = get "feeder"
                let arch = if profile.Architecture="x86" then "32" else "64"
                add ("dlss5-feed.addon"+arch) "feeder-addon" "replace" (exactlyOne ("dlss5-feed.addon"+arch) paths)
                if arch="32" then add "host64/dlss5-feed-host64.exe" "feeder-worker" "replace" (exactlyOne "dlss5-feed-host64.exe" paths)
                replace "reshade-shaders/Shaders/DLSS5_Feed.fx" "shader" "replace" (exactlyOne "DLSS5_Feed.fx" paths)
                let host = if arch="32" then "host64/" else ""
                if not (files |> Seq.exists (fun (f,_) -> f.Target=host+"nvngx_dlss.dll")) && not (has "dlss-sr") then invalidOp "Feeder requires a DLSS Super Resolution runtime."
            if has "reframework" then
                if not reGame || not (GameComponents.isReRoute profile) then invalidOp "Select an RE-specific route for REFramework."
                let cached,_,paths = extracted.["reframework"]
                if cached.Source="reframework" && not (reAssetMatches exe cached.Receipt.Asset) then invalidOp "This REFramework build targets another game."
                replace "dinput8.dll" "mod-loader" "replace" (exactlyOne "dinput8.dll" paths)
            for group,name in ["dlssnr","nvngx_dlssnr.dll";"dlss-sr","nvngx_dlss.dll"] do
                if has group then
                    let path = exactlyOne name (get group)
                    let targets = files |> Seq.map fst |> Seq.filter (fun f -> Path.GetFileName(f.Target).Equals(name,StringComparison.OrdinalIgnoreCase)) |> Seq.map (fun f -> f.Target) |> Seq.toArray
                    if targets.Length = 0 then
                        let prefix = if has "optiscaler" then "OptiScaler/" elif profile.Architecture="x86" then "host64/" else ""
                        replace (prefix+name) "model" "replace" path
                    else for target in targets do replace target "model" "replace" path
                    if has "optiscaler" && group="dlssnr" then replace name "model" "replace" path
            for KeyValue(group,(_,root,paths)) in extracted do
                for path in paths do
                    if Regex.IsMatch(Path.GetFileName(path),"(?i)(license|notice|readme|read-me|revision|attribution)") && Regex.IsMatch(Path.GetExtension(path),"(?i)^\\.(txt|md|json)$") then
                        notices.Add("notices/online/"+group+"/"+Path.GetRelativePath(root,path).Replace('\\','/'),path)
            for file,_ in files do
                if file.Architecture <> "" then
                    let expected = if file.Target.StartsWith("host64/",StringComparison.OrdinalIgnoreCase) || file.Target.Contains("/host64/",StringComparison.OrdinalIgnoreCase) then "x64" else profile.Architecture
                    if file.Architecture <> expected then invalidOp ("组件位数与目标进程不符 / Component architecture mismatch: " + file.Target)
            // Release choices are displayed and included in the immutable manifest hash.
            let provenance = [|
                if not (has "optiscaler") then
                    let p = basePackage.Value
                    yield sprintf "基底包 / Base package: %s · %s（未替换组件沿用 / retains unmodified components）" p.Manifest.Version p.Id
                for _,c in selected do
                    yield sprintf "%s @ %s · %s · asset %d · SHA-256 %s%s" c.Receipt.Repository c.Receipt.Release.Tag c.Receipt.Asset.Name c.Receipt.Asset.Id c.Receipt.Sha256 (if c.Receipt.Asset.Digest="" then " · 源未提供校验值 / source has no checksum" else "") |]
            let version = "Online · " + String.Join(" + ",selected |> Array.map (fun (group,c) -> group+" "+c.Receipt.Release.Tag))
            let version = if version.Length > 160 then version.Substring(0,157)+"..." else version
            ct.ThrowIfCancellationRequested()
            report "保存所选版本组合 / Saving selected component versions..."
            RuntimePackages.compose store version profile (files.ToArray()) (notices.ToArray()) provenance
        finally
            if Directory.Exists(work) then Directory.Delete(work,true)

    /// Missing route dependencies use the latest appropriate public release, and are shown in the returned lock.
    let prepare store gameDir exe basePackage profileId (choices: Cached[]) report (ct: CancellationToken) = async {
        let pending = ResizeArray<Cached>(choices)
        let has group = pending |> Seq.exists (fun c -> (source c.Source).Group=group)
        let baseProfile = basePackage |> Option.map (fun p -> p.Manifest.Profiles |> Array.find (fun r -> r.Id=profileId))
        if has "optiscaler" && (has "feeder" || has "renodx") then invalidOp "请选择 Aurora 或 Feeder/RenoDX 中的一套路线 / Choose Aurora or Feeder/RenoDX."
        let ensure id = async {
            let s = source id
            if not (has s.Group) then
                report ("补全依赖 / Resolving dependency: " + s.Name)
                let! versions = listReleases s s.Nightly ct
                let release = versions |> Array.tryHead |> Option.defaultWith (fun () -> invalidOp ("No release for dependency: " + s.Name))
                let asset = chooseAsset s exe "" release
                let! cached = download (defaultStore ()) s release asset report ct
                pending.Add(cached) }
        if has "feeder" then do! ensure "renodx"
        if has "renodx" && (baseProfile |> Option.exists (fun p -> p.NativeUpscaler="absent" && not (GameComponents.isReRoute p))) then do! ensure "feeder"
        if (GameComponents.reEngineMarkers exe).Length>0 && (has "optiscaler" || (baseProfile |> Option.exists GameComponents.isReRoute)) then do! ensure "reframework-nightly"
        let downloads = pending.ToArray()
        let package = build store gameDir exe basePackage profileId downloads ct report
        return package,downloads
    }
