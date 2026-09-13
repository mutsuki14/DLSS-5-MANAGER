namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Diagnostics

/// Read-only checks. Findings describe observed files/logs, not in-game certification.
module HealthCheck =
    type Finding = { Level: string; Code: string; Message: string; Hint: string }
    let private finding level code message hint = { Level = level; Code = code; Message = message; Hint = hint }

    let selectionIssues (exePath: string) (mode: string) (arch: string) (api: string) =
        [| match PeInspection.inspect exePath with
           | None -> yield "The selected file is not a readable Windows PE executable."
           | Some image ->
               if image.Architecture = "arm64" || image.Architecture = "unsupported" then
                   yield "This executable requires an architecture for which no payload is bundled."
               let effectiveArch = if mode = "dx9" || mode = "dx11" then arch else "64"
               if (image.Architecture = "32" || image.Architecture = "64") && image.Architecture <> effectiveArch then
                   yield sprintf "The executable is %s-bit, but this route deploys %s-bit files." image.Architecture effectiveArch
               if image.GraphicsApis = [|"vulkan"|] && mode <> "emulator" && not (mode = "optiscaler" && api = "vulkan") then
                   yield "The executable imports Vulkan. Select the Vulkan OptiScaler route."
               if image.GraphicsApis = [|"dx9"|] && mode <> "dx9" then
                   yield "The executable imports DirectX 9. Select the DX9 route."
               if image.GraphicsApis = [|"dx8"|] || image.GraphicsApis = [|"ddraw"|] then
                   yield "A DirectX 8 / DirectDraw import was detected; this manager does not bundle that route."
               if image.GraphicsApis = [|"dx11"|] && mode = "optiscaler" then
                   yield "The selected OptiScaler route targets DX12/Vulkan; this executable imports DX11. Select DX11." |]

    let runningGame (exePath: string) =
        if not (OperatingSystem.IsWindows()) || String.IsNullOrWhiteSpace(exePath) then false
        else
            Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath))
            |> Array.fold (fun running candidate ->
                use p = candidate
                let matches =
                    try String.Equals(p.MainModule.FileName, Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase)
                    with _ -> true // Cannot inspect a matching process: ask the user to close it.
                running || matches) false

    /// Byte-bounded and shareable with a running game. Never ReadAllText on logs.
    let readTail (path: string) (maxBytes: int) =
        if maxBytes <= 0 then invalidArg "maxBytes" "A positive byte limit is required."
        use stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
        let count = int (min stream.Length (int64 maxBytes))
        let truncated = stream.Length > int64 count
        stream.Seek(-int64 count, SeekOrigin.End) |> ignore
        let bytes = Array.zeroCreate<byte> count
        let mutable read = 0
        let mutable eof = false
        while read < count && not eof do
            let n = stream.Read(bytes, read, count - read)
            if n = 0 then eof <- true else read <- read + n
        let text = Encoding.UTF8.GetString(bytes, 0, read)
        if truncated then
            let newline = text.IndexOf('\n')
            if newline < 0 then "" else text.Substring(newline + 1)
        else text

    let analyzeLog (relative: string) (text: string) =
        let findings = ResizeArray<Finding>()
        // Only 033's own stage marker has these semantics. Never infer a
        // rendered frame from the existence of a DLL, panel or generic log.
        let stages = Regex.Matches(text, @"\[033 NR stage\]\s+reason=(\d+)")
        if stages.Count > 0 then
            let code = stages.[stages.Count - 1].Groups.[1].Value
            if code = "24" then
                findings.Add(finding "INFO" "nr-observed" (relative + ": latest recorded 033 NR stage succeeded.") "This is evidence from a recorded session, not a live performance measurement.")
            else
                findings.Add(finding "WARN" "nr-stage" (relative + ": latest recorded 033 NR reason=" + code + ".") "Review this session's core log and runtime files before changing settings.")
        elif relative.EndsWith("dlss5-033.log", StringComparison.OrdinalIgnoreCase) && not (String.IsNullOrWhiteSpace(text)) then
            findings.Add(finding "WARN" "nr-unconfirmed" (relative + ": no 033 NR stage result in the inspected tail.") "For the 033 route, check the package's DLSS Super Resolution / Ray Reconstruction instructions and run a new game session.")
        if Regex.IsMatch(text, @"(?im)(\bERROR\b.*(failed|unable|could not|missing)|failed to (load|create)|unhandled exception|device_removed|device_hung)") then
            findings.Add(finding "WARN" "runtime-error" (relative + ": error markers found in the log tail.") "Review the original log with its timestamp; old or recovered errors do not prove the current session failed.")
        if Regex.IsMatch(text, @"(?i)frame \d+ delivered") then
            findings.Add(finding "INFO" "feed-observed" (relative + ": frame delivery was recorded.") "Frame delivery alone does not confirm neural rendering or generated frames.")
        findings.ToArray()

    let survey (exePath: string) (installDir: string) =
        let findings = ResizeArray<Finding>()
        match PeInspection.inspect exePath with
        | None -> findings.Add(finding "ERROR" "invalid-pe" "No readable Windows executable was selected." "Choose the actual game .exe, not a shortcut or launcher.")
        | Some image ->
            findings.Add(finding "INFO" "architecture" ("Executable architecture: " + image.Architecture) "Read from the PE header; ARM64 and x64 are distinct.")
            let apis = if image.GraphicsApis.Length = 0 then "not established" else String.Join(", ", image.GraphicsApis)
            findings.Add(finding "INFO" "imports" ("Graphics APIs in PE imports: " + apis) "Imports include delay-loaded DLLs. Multiple APIs require the game's selected renderer to be checked.")
        match CompatibilityRules.tryFind exePath with
        | Some rule ->
            findings.Add(finding "INFO" "game-rule" ("Imported rule: " + rule.Title + " / API: " + rule.Api + " / mount: " + rule.Mount) "Package advice is not a guarantee for this manager's payload or your game build.")
            if rule.Note <> "" then findings.Add(finding "INFO" "rule-note" rule.Note "Note supplied by the imported package.")
            if rule.AntiCheat then findings.Add(finding "WARN" "rule-anticheat" "The imported rule flags this game for anti-cheat restrictions." "Check the game's rules before using injected DLLs; no anti-cheat bypass is provided.")
        | None -> ()
        let exeDir = if String.IsNullOrWhiteSpace(exePath) then "" else Path.GetDirectoryName(exePath)
        let roots = [|exeDir; installDir|] |> Array.filter (String.IsNullOrWhiteSpace >> not) |> Array.distinct
        let antiCheatNames = [|"EasyAntiCheat"; "EasyAntiCheat_EOS"; "BattlEye"; "start_protected_game.exe"; "EasyAntiCheat_x64.dll"; "BEClient_x64.dll"|]
        for root in roots do
            for name in antiCheatNames do
                let path = Path.Combine(root, name)
                if File.Exists(path) || Directory.Exists(path) then
                    findings.Add(finding "WARN" "anticheat-file" ("Anti-cheat marker found: " + name) "Presence is a risk signal, not a live anti-cheat detection. Check the game's mod policy.")
        if not (String.IsNullOrWhiteSpace(exeDir)) && Directory.Exists(exeDir) then
            let slots = [|"dxgi.dll"; "d3d9.dll"; "d3d11.dll"; "d3d12.dll"; "version.dll"; "winmm.dll"; "dinput8.dll"; "opengl32.dll"|]
            let occupied = slots |> Array.filter (fun name -> File.Exists(Path.Combine(exeDir, name)))
            if occupied.Length > 0 then
                findings.Add(finding "INFO" "occupied-slots" ("Existing proxy filenames: " + String.Join(", ", occupied)) "These may belong to the game or another mod. Filename alone does not prove ownership.")
            let logNames = [|"ReShade.log"; "OptiScaler.log"; "dlss5-feed.log"; "dlss5-033.log"; "033-framegen.log"|]
            let logDirs = [|""; "host64"; "033-runtime"; Path.Combine("033-runtime", "host64")|]
            let mutable found = 0
            for dir in logDirs do
                for name in logNames do
                    let relative = Path.Combine(dir, name)
                    let path = Path.Combine(exeDir, relative)
                    if File.Exists(path) then
                        found <- found + 1
                        try
                            DeploymentSafety.validatePath [|exeDir|] path
                            let text = readTail path 1048576
                            let timestamp = File.GetLastWriteTimeUtc(path).ToString("u")
                            findings.Add(finding "INFO" "log-time" (relative + " / last write " + timestamp) "Only the last 1 MiB is inspected; logs may describe an earlier session.")
                            findings.AddRange(analyzeLog relative text)
                        with ex -> findings.Add(finding "WARN" "log-unreadable" (relative + ": " + ex.Message) "The game may be writing or locking the log; try again after closing it.")
            if found = 0 then findings.Add(finding "INFO" "no-logs" "No supported runtime logs were found." "Run a game session, then check again. Installed files alone cannot confirm rendering.")
        findings.ToArray()

    let formatReport title exe mode arch api (findings: Finding[]) =
        String.Join(Environment.NewLine,
            [| yield "DLSS 5 MANAGER — Health check / 游戏体检"
               yield "UTC: " + DateTime.UtcNow.ToString("u")
               yield "Game: " + title
               yield "Executable: " + exe
               yield sprintf "Selected route: %s / %s-bit / %s" mode arch api
               yield "This report is local and contains paths. Review it before sharing."
               yield ""
               for f in findings do
                   yield sprintf "[%s] %s: %s" f.Level f.Code f.Message
                   if f.Hint <> "" then yield "  " + f.Hint |])
