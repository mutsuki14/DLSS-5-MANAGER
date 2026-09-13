namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Text.RegularExpressions
open DLSS_5_MANAGER.Services.RuntimePackages

/// Game-specific dependencies are reviewed and deployed with the route's transaction.
/// A filename alone never proves that an existing DLL is a compatible component.
module GameComponents =
    type Requirement = { Id: string; Name: string; Reason: string; Files: PayloadFile[]; Errors: string[] }
    type Status = { Name: string; Reason: string; State: string; Detail: string }

    let reEngineMarkers (exe: string) =
        if String.IsNullOrWhiteSpace(exe) then [||]
        else
            let root = Path.GetDirectoryName(Path.GetFullPath(exe))
            if not (Directory.Exists(root)) then [||]
            else
                Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                |> Seq.map Path.GetFileName
                |> Seq.filter (fun name -> Regex.IsMatch(name, @"^re_chunk_\d+\.pak(?:\.patch_\d+\.pak)?$", RegexOptions.IgnoreCase))
                |> Seq.sort
                |> Seq.toArray

    let isReRoute (profile: Profile) =
        profile.Id = "native-re-x64" || profile.Id = "re-nodlss-x64" ||
        profile.EngineMarkers |> Array.exists (fun name -> name.Equals("re_chunk_000.pak", StringComparison.OrdinalIgnoreCase))

    let private needsReFramework (profile: Profile) = isReRoute profile || Array.contains "reframework" profile.RequiredComponents

    let deploys (profile: Profile) (file: PayloadFile) =
        file.Component = "" || (file.Component = "reframework" && needsReFramework profile)

    let requirements (profile: Profile) =
        [| if needsReFramework profile then
               let files = profile.Files |> Array.filter (fun f -> f.Target.Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase) && deploys profile f)
               yield
                   { Id = "reframework"; Name = "REFramework"
                     Reason = "RE 专用路线的必需加载器 / Required loader for the RE route."
                     Files = files
                     Errors = [|
                         if not (isReRoute profile) then yield "REFramework 只可用于 RE 专用路线 / REFramework requires an RE-specific route."
                         if files.Length <> 1 then yield "运行包缺少 REFramework dinput8.dll / Package is missing REFramework dinput8.dll."
                         elif profile.Architecture <> "x64" || files.[0].Architecture <> "x64" || files.[0].Role <> "mod-loader" || files.[0].Policy <> "replace" then
                             yield "REFramework 清单需要 x64、mod-loader 和 replace 策略 / Invalid REFramework loader declaration."
                     |] }
           // Conditional components need a supported adapter; a manifest cannot bypass its checks.
           for id in profile.RequiredComponents do
               if id <> "reframework" then
                   yield { Id = id; Name = id; Reason = "清单声明为必需 / Required by manifest."
                           Files = [||]; Errors = [|"尚不支持此必需组件的适配检查 / Unsupported required component: " + id|] }
           for prefix, name, reason in
               [| "payload/dgvoodoo/", "dgVoodoo2", "旧版 DirectX 转接 / Legacy DirectX translation."
                  "payload/host64/", "64-bit host", "32 位游戏的 64 位辅助运行时 / 64-bit helper runtime for a 32-bit game." |] do
               let files = profile.Files |> Array.filter (fun f -> deploys profile f && f.Source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
               if files.Length > 0 && (prefix <> "payload/host64/" || profile.Architecture = "x86") then
                   yield { Id = prefix; Name = name; Reason = reason; Files = files; Errors = [||] }
        |]

    let format (statuses: Status[]) =
        String.Join("\n\n", statuses |> Array.map (fun s -> sprintf "%s · %s\n%s\n%s" s.Name s.State s.Reason s.Detail))
