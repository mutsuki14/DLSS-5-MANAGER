namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Text.Json

/// Imports advisory data from the user's extracted 033 package, never its scripts.
module CompatibilityRules =
    type Rule = { Executable: string; Title: string; Api: string; Mount: string; Note: string; AntiCheat: bool }

    let private storePath () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Manager", "game_rules.json")

    let parse (text: string) =
        if text.Length > 2097152 then invalidArg "text" "Rules file exceeds 2 MB."
        use doc = JsonDocument.Parse(text, JsonDocumentOptions(MaxDepth = 16))
        let root = doc.RootElement
        if root.GetProperty("Schema").GetInt32() <> 1 || root.GetProperty("Kind").GetString() <> "033-game-rules" then
            invalidArg "text" "Expected a schema 1, 033-game-rules file."
        let games = root.GetProperty("Games")
        if games.ValueKind <> JsonValueKind.Object then invalidArg "text" "Games must be an object."
        let field (item: JsonElement) name =
            match item.TryGetProperty(name: string) with
            | true, value when value.ValueKind = JsonValueKind.String ->
                let text = value.GetString()
                if text.Length > 4096 then invalidArg "text" "Rule text is too long."
                text
            | false, _ -> ""
            | _ -> invalidArg "text" ("Invalid rule field: " + name)
        let rules =
            [| for property in games.EnumerateObject() do
                   let exe = property.Name.ToLowerInvariant()
                   if not (exe.EndsWith(".exe")) || exe.IndexOfAny([|'\\'; '/'; ':'; '*'; '?'|]) >= 0 then
                       invalidArg "text" "Rules must use exact executable filenames."
                   let item = property.Value
                   let api = (field item "Api").ToLowerInvariant()
                   if not ([""; "dxgi"; "ddraw"; "dx8"; "dx9"; "dx10"; "dx11"; "dx12"; "vulkan"; "opengl"] |> List.contains api) then
                       invalidArg "text" ("Unknown rule API: " + api)
                   let banned =
                       match item.TryGetProperty("Banned") with
                       | true, value -> value.GetBoolean()
                       | _ -> false
                   yield { Executable = exe; Title = field item "Title"; Api = api
                           Mount = field item "Mount"; Note = field item "Note"; AntiCheat = banned } |]
        if rules.Length > 10000 || (rules |> Array.distinctBy _.Executable).Length <> rules.Length then
            invalidArg "text" "Too many or duplicate executable rules."
        rules

    let importFile (path: string) =
        if FileInfo(path).Length > 2097152L then invalidArg "path" "Rules file exceeds 2 MB."
        let text = File.ReadAllText(path)
        let rules = parse text
        // Re-serialize the validated document so the saved file is valid JSON.
        use doc = JsonDocument.Parse(text)
        DeploymentSafety.writeJsonAtomic (storePath ()) doc.RootElement
        rules.Length

    let tryFind (exePath: string) =
        try
            let path = storePath ()
            if not (File.Exists(path)) || FileInfo(path).Length > 2097152L then None
            else parse (File.ReadAllText(path)) |> Array.tryFind (fun r -> String.Equals(r.Executable, Path.GetFileName(exePath), StringComparison.OrdinalIgnoreCase))
        with _ -> None
