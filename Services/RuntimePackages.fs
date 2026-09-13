namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Text.Json
open System.Security.Cryptography
open System.Collections.Generic
open System.Text.RegularExpressions

/// Imports data and payload bytes only. No package scripts or installers run.
module RuntimePackages =
    type PayloadFile =
        { Source: string; Target: string; Hash: string; Architecture: string
          Policy: string; Role: string; Component: string }
    type Profile =
        { Id: string; Architecture: string; Files: PayloadFile[]; Apis: string[]
          NativeUpscaler: string; Dx12Runtime: string; EngineMarkers: string[]
          ExecutableNames: string[]; Mirrors: string[]; Protocol: string }
    type Manifest = { Version: string; Profiles: Profile[]; Notices: (string * string)[] }
    type Package =
        { Id: string; Root: string; Manifest: Manifest }
        member this.Display = sprintf "%s · %s · %d routes" this.Manifest.Version (this.Id.Substring(0, 12)) this.Manifest.Profiles.Length
        override this.ToString() = this.Display

    let defaultStore () = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Manager", "RuntimePackages")

    // Use Windows path rules even in Linux CI. Reject ADS, aliases and reserved devices.
    let relativePath (value: string) =
        if String.IsNullOrWhiteSpace(value) || value.Length > 240 then invalidOp "Invalid package path."
        let path = value.Replace('\\', '/')
        let parts = path.Split('/')
        for part in parts do
            if part = "" || part = "." || part = ".." || part.EndsWith(".") || part.EndsWith(" ") ||
               part |> Seq.exists (fun c -> int c < 32 || "<>:\"|?*".Contains(c)) ||
               Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)", RegexOptions.IgnoreCase) then
                invalidOp ("Unsafe package path: " + value)
        path

    let resolve root path =
        let full = Path.GetFullPath(Path.Combine(root, relativePath path))
        DeploymentSafety.validatePath [|root|] full
        full

    let private str (e: JsonElement) (name: string) =
        match e.TryGetProperty(name) with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> ""
    let private arr (e: JsonElement) (name: string) =
        match e.TryGetProperty(name) with
        | true, v when v.ValueKind = JsonValueKind.Array -> v.EnumerateArray() |> Seq.toArray
        | false, _ -> [||]
        | _ -> invalidOp ("Expected array: " + name)
    let private strings e name = arr e name |> Array.map (fun v -> v.GetString())
    let private checksum (value: string) =
        if isNull value || not (Regex.IsMatch(value, "^[A-Fa-f0-9]{64}$")) then invalidOp "A payload checksum is missing or invalid."
        value.ToUpperInvariant()

    let parse (json: string) =
        if Encoding.UTF8.GetByteCount(json) > 2 * 1024 * 1024 then invalidOp "Package manifest exceeds 2 MiB."
        use doc = JsonDocument.Parse(json.TrimStart('\uFEFF'), JsonDocumentOptions(MaxDepth = 32))
        let root = doc.RootElement
        if str root "Kind" <> "033-managed-package" || root.GetProperty("Schema").GetInt32() <> 1 then
            invalidOp "Expected a schema 1 033-managed-package manifest."
        let version = str root "Version"
        if String.IsNullOrWhiteSpace(version) || version.Length > 160 then invalidOp "Invalid package version."
        let ids = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let profiles =
            arr root "Profiles" |> Array.map (fun p ->
                let id = str p "Id"
                if not (Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{0,79}$")) || not (ids.Add(id)) then invalidOp "Invalid or duplicate route ID."
                let arch = str p "Architecture"
                if arch <> "x86" && arch <> "x64" then invalidOp ("Unsupported route architecture: " + arch)
                let targets = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                let files = arr p "Files" |> Array.map (fun f ->
                    let source, target = relativePath (str f "Source"), relativePath (str f "Target")
                    if not (source.StartsWith("payload/", StringComparison.OrdinalIgnoreCase)) then invalidOp "Payload source must be inside payload/."
                    if not (targets.Add(target)) then invalidOp ("Duplicate route target: " + target)
                    let policy, arch = str f "Policy", str f "Architecture"
                    if policy <> "replace" && policy <> "seed" then invalidOp "Unknown file policy."
                    if arch <> "" && arch <> "x86" && arch <> "x64" then invalidOp "Unknown payload architecture."
                    { Source = source; Target = target; Hash = checksum (str f "Hash"); Architecture = arch
                      Policy = policy; Role = str f "Role"; Component = str f "Component" })
                if files.Length = 0 || files.Length > 2048 then invalidOp "Invalid route file count."
                let suit = p.GetProperty("Suitability")
                let native = str suit "NativeUpscaler"
                if native <> "present" && native <> "absent" then invalidOp "Unknown native upscaler requirement."
                { Id = id; Architecture = arch; Files = files; Apis = strings suit "Apis"
                  NativeUpscaler = native; Dx12Runtime = str suit "Dx12Runtime"
                  EngineMarkers = strings suit "EngineMarkers" |> Array.map relativePath
                  ExecutableNames = strings p "ExecutableNames" |> Array.map relativePath
                  Mirrors = strings p "MirrorDirectories" |> Array.map relativePath; Protocol = str p "Protocol" })
        if profiles.Length = 0 || profiles.Length > 128 then invalidOp "Invalid route count."
        let notices = arr root "Notices" |> Array.map (fun n ->
            let source = relativePath (str n "Source")
            if not (source.StartsWith("notices/", StringComparison.OrdinalIgnoreCase)) then invalidOp "Notices must be inside notices/."
            source, checksum (str n "Hash"))
        { Version = version; Profiles = profiles; Notices = notices }

    let private sources manifest =
        let refs = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        let references = Array.append (manifest.Profiles |> Array.collect (fun p -> p.Files |> Array.map (fun f -> f.Source, f.Hash))) manifest.Notices
        for path, hash in references do
            match refs.TryGetValue(path) with
            | true, previous when previous <> hash -> invalidOp ("Conflicting source checksums: " + path)
            | _ -> refs.[path] <- hash
        refs

    let private manifestId (bytes: byte[]) = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()

    let load (store: string) (id: string) =
        if not (Regex.IsMatch(id, "^[a-f0-9]{64}$")) then invalidOp "Invalid package ID."
        let root = Path.Combine(Path.GetFullPath(store), id)
        let path = resolve root "033-package.json"
        if FileInfo(path).Length > 2L * 1024L * 1024L then invalidOp "Package manifest exceeds 2 MiB."
        let bytes = File.ReadAllBytes(path)
        if manifestId bytes <> id then invalidOp "Stored package manifest changed; import the package again."
        { Id = id; Root = root; Manifest = parse (Encoding.UTF8.GetString(bytes)) }

    let list (store: string) =
        if not (Directory.Exists(store)) then [||]
        else
            try
                Directory.GetDirectories(store) |> Array.choose (fun dir ->
                    try Some (load store (Path.GetFileName(dir))) with _ -> None)
                |> Array.sortByDescending (fun p -> p.Manifest.Version, p.Id)
            with :? IOException | :? UnauthorizedAccessException -> [||]

    let verifyFile root (file: PayloadFile) =
        let source = resolve root file.Source
        if DeploymentSafety.hashFile source <> file.Hash then invalidOp ("Payload missing or checksum mismatch: " + file.Source)
        if file.Architecture <> "" then
            let expected = if file.Architecture = "x86" then "32" else "64"
            match PeInspection.inspect source with
            | Some image when image.Architecture = expected -> ()
            | _ -> invalidOp ("Payload PE architecture mismatch: " + file.Source)
        source

    let verify (package: Package) =
        for f in package.Manifest.Profiles |> Array.collect (fun p -> p.Files) |> Array.distinctBy (fun f -> f.Source, f.Architecture) do
            verifyFile package.Root f |> ignore
        for source, expected in package.Manifest.Notices do
            if DeploymentSafety.hashFile (resolve package.Root source) <> expected then invalidOp ("Notice checksum mismatch: " + source)

    let importZip (store: string) (zipPath: string) (report: string -> unit) =
        let store = Path.GetFullPath(store)
        use archive = ZipFile.OpenRead(zipPath)
        if archive.Entries.Count > 10000 then invalidOp "Archive exceeds 10,000 entries."
        let entries = Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase)
        let mutable total = 0L
        for entry in archive.Entries do
            let name = relativePath (entry.FullName.TrimEnd('/'))
            if (entry.ExternalAttributes >>> 16 &&& 0xF000) = 0xA000 || (entry.ExternalAttributes &&& 0x400) <> 0 then invalidOp "Archive links are not supported."
            total <- total + entry.Length
            if entry.Length > 1024L * 1024L * 1024L || total > 4L * 1024L * 1024L * 1024L then invalidOp "Archive exceeds extraction limits (1 GiB/file, 4 GiB total)."
            if not (entries.TryAdd(name, entry)) then invalidOp ("Duplicate archive path: " + name)
        let manifests = entries.Keys |> Seq.filter (fun n -> n = "033-package.json" || n.EndsWith("/033-package.json", StringComparison.OrdinalIgnoreCase)) |> Seq.toArray
        if manifests.Length <> 1 then invalidOp "Archive must contain exactly one 033-package.json."
        let manifestName = manifests.[0]
        let prefix = manifestName.Substring(0, manifestName.Length - "033-package.json".Length)
        let entry = entries.[manifestName]
        if entry.Length > 2L * 1024L * 1024L then invalidOp "Package manifest exceeds 2 MiB."
        let copyBounded (entry: ZipArchiveEntry) (output: Stream) =
            use input = entry.Open()
            let buffer = Array.zeroCreate<byte> 65536
            let mutable count, read = 0L, 1
            while read > 0 do
                read <- input.Read(buffer, 0, buffer.Length)
                count <- count + int64 read
                if count > entry.Length then invalidOp "Archive entry exceeds declared length."
                output.Write(buffer, 0, read)
            if count <> entry.Length then invalidOp "Archive entry is truncated."
        use memory = new MemoryStream()
        copyBounded entry memory
        let bytes = memory.ToArray()
        let manifest = parse (Encoding.UTF8.GetString(bytes))
        let refs = sources manifest
        let missing = refs.Keys |> Seq.filter (fun name -> not (entries.ContainsKey(prefix + name))) |> Seq.toArray
        if missing.Length > 0 then invalidOp ("Missing package components:\n" + String.Join("\n", missing))
        DeploymentSafety.validatePath [|store|] (Path.Combine(store, "operation.lock"))
        Directory.CreateDirectory(store) |> ignore
        use importLock = DeploymentSafety.acquireLock store
        let id = manifestId bytes
        let final = Path.Combine(store, id)
        let stage = Path.Combine(store, "import-" + Guid.NewGuid().ToString("N"))
        // Reject a redirected store before extracting any payload.
        DeploymentSafety.validatePath [|store|] (Path.Combine(stage, "033-package.json"))
        Directory.CreateDirectory(stage) |> ignore
        try
            File.WriteAllBytes(Path.Combine(stage, "033-package.json"), bytes)
            for KeyValue(source, expected) in refs do
                report ("验证 / Verify: " + source)
                let target = resolve stage source
                Directory.CreateDirectory(Path.GetDirectoryName(target)) |> ignore
                use output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                copyBounded entries.[prefix + source] output
                output.Dispose()
                if DeploymentSafety.hashFile target <> expected then invalidOp ("Checksum mismatch: " + source)
            let package = { Id = id; Root = stage; Manifest = manifest }
            // Check each distinct architecture once; shared models may be hundreds of MiB.
            for f in manifest.Profiles |> Array.collect (fun p -> p.Files) |> Array.distinctBy (fun f -> f.Source, f.Architecture) do
                if f.Architecture <> "" then
                    let expected = if f.Architecture = "x86" then "32" else "64"
                    match PeInspection.inspect (resolve stage f.Source) with
                    | Some image when image.Architecture = expected -> ()
                    | _ -> invalidOp ("Payload PE architecture mismatch: " + f.Source)
            if Directory.Exists(final) then
                let existing = load store id
                verify existing
                existing
            else
                Directory.Move(stage, final)
                { package with Root = final }
        finally
            if Directory.Exists(stage) then Directory.Delete(stage, true)
