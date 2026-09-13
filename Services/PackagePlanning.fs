namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Text
open System.Text.Json
open System.Security.Cryptography
open DLSS_5_MANAGER.Services.RuntimePackages

/// Read-only decisions and the exact declarative file plan reviewed by the user.
module PackagePlanning =
    type Evidence =
        { Architecture: string; Apis: string[]; ApiSource: string; NativeDlss: bool
          Dx12Runtime: bool; AntiCheat: bool; EngineMarkers: string[]; ExeName: string }
    type Assessment = { ProfileId: string; Reasons: string[] }
    type Row =
        { Source: string; Target: string; RelativeTarget: string; SourceHash: string
          BeforeHash: string; Action: string; Bytes: int64; BeforeBytes: int64 }
    type Preview =
        { PackageId: string; ProfileId: string; ExePath: string; ExeHash: string
          Rows: Row[]; Errors: string[]; Warnings: string[]; Advice: string; Components: GameComponents.Status[]; Versions: string[]
          BackupDirectory: string; WriteBytes: int64; BackupBytes: int64; Token: string }

    let evidence gameDir exe (package: Package) =
        let image = PeInspection.inspect exe
        let rule = CompatibilityRules.tryFind exe
        let direct = image |> Option.map (fun p -> p.GraphicsApis) |> Option.defaultValue [||]
        let ruleApi = rule |> Option.map (fun r -> r.Api) |> Option.defaultValue ""
        let apis, source =
            if direct.Length > 0 then direct, "PE 导入表 / PE imports"
            elif ruleApi <> "" && ruleApi <> "dxgi" then [|ruleApi|], "导入的游戏规则（待游戏内确认）/ imported game rule"
            else [||], "证据不足 / insufficient evidence"
        let root = Path.GetDirectoryName(exe)
        let native = GameAnalyzer.findDlssFolders gameDir exe |> List.exists (fun folder -> folder.Files |> List.exists (fun f -> f.Name.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase)))
        let markers = package.Manifest.Profiles |> Array.collect (fun p -> p.EngineMarkers) |> Array.distinct
                      |> Array.filter (fun name -> File.Exists(Path.Combine(root, name)))
        // Normalize chunk variants so RE games cannot fall through to a generic route.
        let markers = if (GameComponents.reEngineMarkers exe).Length > 0 then Array.append markers [|"re_chunk_000.pak"|] |> Array.distinct else markers
        let antiCheat = (rule |> Option.exists (fun r -> r.AntiCheat)) ||
                        [|"EasyAntiCheat"; "EasyAntiCheat_EOS"; "BattlEye"; "ACE"; "AntiCheatExpert"|]
                        |> Array.exists (fun name -> Directory.Exists(Path.Combine(root, name)) || Directory.Exists(Path.Combine(gameDir, name)))
        { Architecture = image |> Option.map (fun p -> p.Architecture) |> Option.defaultValue "unknown"
          Apis = apis; ApiSource = source; NativeDlss = native; AntiCheat = antiCheat
          Dx12Runtime = apis |> Array.contains "dx12" || File.Exists(Path.Combine(root, "D3D12Core.dll")) || File.Exists(Path.Combine(root, "D3D12", "D3D12Core.dll"))
          EngineMarkers = markers; ExeName = Path.GetFileName(exe) }

    let assess (facts: Evidence) (profile: Profile) =
        let expected = if profile.Architecture = "x86" then "32" else "64"
        { ProfileId = profile.Id
          Reasons = [|
            for dependency in GameComponents.requirements profile do yield! dependency.Errors
            if GameComponents.isReRoute profile && not (Array.contains "re_chunk_000.pak" facts.EngineMarkers) then
                yield "缺少 RE 引擎证据，不能部署 REFramework / No RE Engine evidence for REFramework."
            if facts.Architecture <> expected then yield "游戏位数与路线不符 / Architecture mismatch."
            if facts.Apis.Length = 0 then yield "尚未识别图形 API，请选择真实游戏 EXE / Graphics API unknown; select the game executable."
            elif not (facts.Apis |> Array.exists (fun api -> profile.Apis |> Array.contains api)) then yield "图形 API 与路线不符 / Graphics API mismatch."
            if facts.Apis |> Array.contains "vulkan" then yield "本运行包不支持 Vulkan / This package does not support Vulkan."
            if facts.NativeDlss <> (profile.NativeUpscaler = "present") then yield "原生 DLSS 文件证据与路线条件不符 / Native DLSS evidence does not match this route."
            if facts.NativeDlss && not (facts.Apis |> Array.contains "dx12") then yield "纯 DX10/11 原生 DLSS 场景在随包说明中标为不生效 / Native DLSS on DX10/11 is unsupported by this package."
            if profile.Dx12Runtime = "present" && not facts.Dx12Runtime then yield "缺少 D3D12 运行时证据 / No D3D12 runtime evidence."
            if profile.Dx12Runtime = "absent" && facts.Dx12Runtime then yield "此路线要求没有 D3D12 运行时 / This route requires no D3D12 runtime."
            if profile.EngineMarkers |> Array.exists (fun marker -> not (facts.EngineMarkers |> Array.contains marker)) then yield "未找到专用引擎标记 / Required engine marker missing."
            if profile.EngineMarkers.Length = 0 && facts.EngineMarkers.Length > 0 then yield "发现专用引擎标记，请使用对应 RE 路线 / Use the matching RE route."
            if profile.ExecutableNames.Length > 0 && not (profile.ExecutableNames |> Array.exists (fun name -> name.Equals(facts.ExeName, StringComparison.OrdinalIgnoreCase))) then yield "专用路线不适用于该 EXE / This route is restricted to another executable."
            if facts.ExeName.Equals("yysls.exe", StringComparison.OrdinalIgnoreCase) && profile.Id <> "yanyun-x64" then yield "该 EXE 需要燕云专用路线 / This executable needs the Yanyun route."
            if facts.AntiCheat then yield "发现反作弊规则或目录标记，暂停自动部署 / Anti-cheat evidence blocks automated deployment."
          |] }

    let advice (facts: Evidence) (package: Package) =
        let candidates = package.Manifest.Profiles |> Array.map (assess facts) |> Array.filter (fun a -> a.Reasons.Length = 0)
        String.Join("\n", [|
            sprintf "位数 / Architecture: %s · API: %s" facts.Architecture (String.Join(", ", facts.Apis))
            "API 依据 / Source: " + facts.ApiSource
            if facts.NativeDlss then "发现 nvngx_dlss.dll；文件存在不证明游戏正在使用 DLSS / DLSS file found; active use is unverified."
            else "扫描未发现原生 DLSS 文件；动态加载或扫描权限可能影响判断 / No native DLSS found in scanned folders."
            "匹配候选 / Candidates: " + (if candidates.Length = 0 then "无 / none" else String.Join(", ", candidates |> Array.map (fun p -> p.ProfileId)))
            if Array.contains "re_chunk_000.pak" facts.EngineMarkers then
                "RE Engine：选择包含 REFramework 的 RE 专用路线 / Select an RE-specific route with REFramework."
            "适配结论来自静态证据，尚未进行游戏内验证 / Static evidence only; not tested in game."
        |])

    let private spaceErrors (requirements: (string * int64)[]) =
        requirements |> Array.groupBy (fun (path, _) -> Path.GetPathRoot(path)) |> Array.collect (fun (root, items) ->
            try
                let drive = DriveInfo(root)
                let required = items |> Array.sumBy snd
                if drive.IsReady && drive.AvailableFreeSpace < required then [|"磁盘空间不足 / Insufficient free space: " + root|] else [||]
            with _ -> [|"无法读取磁盘剩余空间 / Cannot read free space: " + root|])

    let create gameDir exe backupDirectory hasInstall (package: Package) profileId =
        let profile = package.Manifest.Profiles |> Array.find (fun p -> p.Id = profileId)
        let exe = Path.GetFullPath(exe)
        let root = Path.GetDirectoryName(exe)
        DeploymentSafety.validatePath [|root|] exe
        let facts = evidence gameDir exe package
        let errors = ResizeArray<string>((assess facts profile).Reasons)
        if hasInstall then errors.Add("请先还原当前安装，再为所选版本生成新预览 / Restore the current installation before changing package versions or routes.")
        if HealthCheck.runningGame exe then errors.Add("请先关闭游戏 / Close the game before installation.")
        let warnings = ResizeArray<string>()
        if facts.Apis.Length > 1 then warnings.Add("检测到多个图形 API；请确认游戏启动时使用所选路线的 API / Multiple APIs found; confirm the game's launch mode.")
        let excluded = profile.Files |> Array.filter (GameComponents.deploys profile >> not) |> Array.map (fun f -> f.Component) |> Array.distinct
        if excluded.Length > 0 then warnings.Add("未启用的可选组件 / Excluded optional components: " + String.Join(", ", excluded) + "；适配条件尚未验证 / Compatibility conditions have not been verified.")
        warnings.Add("保留已有配置；请检查组件开关与加载设置 / Existing settings are kept; review component switches and loading configuration.")
        warnings.Add("逐游戏挂载点与全局驱动设置不会自动应用；本预览仅按清单部署 / Per-game hook overrides and global driver settings are not applied.")
        let files = profile.Files |> Array.filter (GameComponents.deploys profile)
        if (files |> Array.exists (fun f -> f.Target="OptiScaler.ini" && f.Policy="seed")) && File.Exists(Path.Combine(root,"OptiScaler.ini")) then
            warnings.Add("已有 OptiScaler.ini 将保留；使用神经渲染需确认 [DlssNr] 中 Enabled=true / Confirm NR is enabled in your existing Aurora settings.")
        let mirrors = profile.Mirrors |> Array.filter (fun path -> Directory.Exists(Path.Combine(root, path)))
        let allFiles =
            [| yield! files
               for mirror in mirrors do
                   for f in files do
                       if f.Target.StartsWith("033-runtime/", StringComparison.OrdinalIgnoreCase) then
                           yield { f with Target = mirror + "/" + f.Target } |]
        if package.Manifest.Provenance.Length > 0 then
            warnings.Add("在线组合使用所选 Release；游戏版本、驱动和组件之间的兼容性仍需实机验证 / Online component combinations require in-game verification.")
            let consumerPattern = "(?i)^(renodx-dlss.*\\.addon64|nvngx\\.dll\\.addon64|033-engine\\.dll|OptiScaler\\.dll)$"
            for folder in [|root; Path.Combine(root,"host64"); Path.Combine(root,"033-runtime"); Path.Combine(root,"033-runtime","host64","033-runtime")|] do
                if Directory.Exists(folder) then
                    for path in Directory.EnumerateFiles(folder) do
                        if System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path),consumerPattern) &&
                           not (allFiles |> Array.exists (fun f -> (RuntimePackages.resolve root f.Target).Equals(path,StringComparison.OrdinalIgnoreCase))) then
                            errors.Add("发现其他神经渲染组件，请先处理或还原现有安装 / Competing neural component: " + Path.GetRelativePath(root,path))
        let seen = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let rows = [| for file in allFiles do
                        try
                            let target = RuntimePackages.resolve root file.Target
                            if target.Equals(exe, StringComparison.OrdinalIgnoreCase) then invalidOp "Package cannot replace the game executable."
                            if not (seen.Add(target)) then invalidOp ("Duplicate deployment target: " + target)
                            let source = RuntimePackages.verifyFile package.Root file
                            let before = DeploymentSafety.hashFile target
                            let action =
                                if file.Policy = "seed" && before <> "" then "keep"
                                elif before = file.Hash then "same"
                                elif before = "" then "add"
                                else "replace"
                            if action = "replace" && (file.Role = "entry" || file.Role = "mod-loader") then
                                errors.Add("挂载 DLL 已被占用，请先处理原有模组 / Hook slot already occupied: " + file.Target)
                            if action = "replace" && (File.GetAttributes(target) &&& FileAttributes.ReadOnly) <> enum 0 then errors.Add("文件为只读 / Read-only file: " + file.Target)
                            yield { Source = source; Target = target; RelativeTarget = file.Target; SourceHash = file.Hash
                                    BeforeHash = before; Action = action; Bytes = FileInfo(source).Length
                                    BeforeBytes = if before = "" then 0L else FileInfo(target).Length }
                        with ex -> errors.Add(ex.Message) |]
        let components = GameComponents.requirements profile |> Array.map (fun dependency ->
            let componentRows = rows |> Array.filter (fun r -> dependency.Files |> Array.exists (fun f -> f.Target = r.RelativeTarget))
            let blocked = dependency.Errors.Length > 0 || componentRows.Length <> dependency.Files.Length
            let conflict = dependency.Id = "reframework" && (componentRows |> Array.exists (fun r -> r.Action = "replace"))
            let unchanged = componentRows.Length > 0 && (componentRows |> Array.forall (fun r -> r.Action = "same" || r.Action = "keep"))
            let status: GameComponents.Status =
                { Name = dependency.Name; Reason = dependency.Reason
                  State = if blocked then "缺失或无效 / Missing or invalid" elif conflict then "冲突 / Conflict" elif unchanged then "与运行包一致，将保留 / Matches package; kept" else "随路线安装 / Included in installation"
                  Detail = String.Join("\n", [|
                      yield! dependency.Errors
                      if blocked && dependency.Errors.Length = 0 then yield "文件校验失败，见下方阻止原因 / File validation failed; see the blocked reasons below."
                      if conflict then yield "已有不同的 dinput8.dll；先还原或手动处理现有模组，再重新预览 / Resolve the existing dinput8.dll before retrying."
                      for f in dependency.Files do yield f.Target + " · SHA-256 " + f.Hash.Substring(0, 16)
                  |]) }
            status)
        let changed = rows |> Array.filter (fun r -> r.Action = "add" || r.Action = "replace")
        let writes = changed |> Array.sumBy (fun r -> r.Bytes)
        let backups = changed |> Array.sumBy (fun r -> r.BeforeBytes)
        // Budget original backups + rollback snapshots and a same-directory staging copy.
        let largest = changed |> Array.map (fun r -> r.Bytes) |> Array.append [|0L|] |> Array.max
        errors.AddRange(spaceErrors [|root, writes + largest; backupDirectory, backups * 2L|])
        let exeHash = DeploymentSafety.hashFile exe
        let tokenData = JsonSerializer.Serialize((package.Id, profile.Id, exe, exeHash, rows, facts, components, warnings.ToArray(), backupDirectory))
        { PackageId = package.Id; ProfileId = profile.Id; ExePath = exe; ExeHash = exeHash
          Rows = rows; Errors = errors.ToArray(); Warnings = warnings.ToArray(); Advice = advice facts package; Components = components; Versions = package.Manifest.Provenance
          BackupDirectory = backupDirectory; WriteBytes = writes; BackupBytes = backups
          Token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokenData))) }

    let format (preview: Preview) =
        let mib n = float n / 1048576.0
        String.Join("\n", [|
            yield sprintf "运行包 / Package: %s\n路线 / Route: %s" (preview.PackageId.Substring(0, 12)) preview.ProfileId
            for version in preview.Versions do yield "[组件版本 / Component version] " + version
            yield "目标 / Target: " + preview.ExePath
            yield "备份 / Backups: " + preview.BackupDirectory
            yield sprintf "写入 / Writes: %.1f MiB · 原件备份 / Originals: %.1f MiB（另需临时副本 / plus staging）" (mib preview.WriteBytes) (mib preview.BackupBytes)
            if preview.Components.Length > 0 then
                yield "必需组件 / Required components:\n" + GameComponents.format preview.Components
            for warning in preview.Warnings do yield "[提示 / Note] " + warning
            for error in preview.Errors do yield "[阻止 / Blocked] " + error
            yield ""
            for row in preview.Rows do
                let action = match row.Action with "add" -> "新增 / Add" | "replace" -> "替换并备份 / Replace + backup" | "keep" -> "保留配置 / Keep settings" | _ -> "相同，跳过 / Unchanged"
                yield sprintf "[%s] %s (%.2f MiB)" action row.RelativeTarget (mib row.Bytes)
        |])
