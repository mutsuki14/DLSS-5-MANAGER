namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Collections.Generic

/// Content ownership, verified backups and failure recovery for manager-owned files.
module DeploymentSafety =
    [<CLIMutable>]
    type InstalledFile =
        { TargetPath: string
          BackupPath: string
          WasExisting: bool
          BeforeSha256: string
          InstalledSha256: string
          BeforeAttributes: int
          BeforeWriteTimeUtc: string }

    let hashFile (path: string) =
        if Directory.Exists(path) then invalidOp ("A directory occupies a file path: " + path)
        if File.Exists(path) then
            use stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            Convert.ToHexString(SHA256.HashData(stream))
        else ""

    let private sameHash a b = String.Equals(a, b, StringComparison.OrdinalIgnoreCase)
    let private present (s: string) = not (String.IsNullOrWhiteSpace(s))

    let validatePath (roots: string[]) (path: string) =
        if not (Path.IsPathFullyQualified(path)) then invalidOp ("Expected an absolute path: " + path)
        let full = Path.GetFullPath(path)
        let comparison = if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
        let inside =
            roots |> Array.exists (fun root ->
                let prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + string Path.DirectorySeparatorChar
                full.StartsWith(prefix, comparison))
        if not inside then invalidOp ("Path is outside this game's managed folders: " + path)
        let mutable cursor = full
        while not (String.IsNullOrEmpty(cursor)) do
            // Inspect even dangling links, for which File.Exists returns false.
            try
                if (File.GetAttributes(cursor) &&& FileAttributes.ReparsePoint) <> enum 0 then
                    invalidOp ("Linked paths are not supported for deployment: " + cursor)
            with :? FileNotFoundException | :? DirectoryNotFoundException -> ()
            cursor <- Path.GetDirectoryName(cursor)
        if Directory.Exists(full) then invalidOp ("A directory occupies a file path: " + path)

    let writeJsonAtomic (path: string) (value: 'T) =
        Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
        let temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
        try
            let options = JsonSerializerOptions(WriteIndented = true)
            use stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            JsonSerializer.Serialize(stream, value, options)
            stream.Flush(true)
            stream.Dispose()
            File.Move(temp, path, true)
        finally
            if File.Exists(temp) then File.Delete(temp)

    /// Stage to the same volume, verify bytes, then replace the target atomically.
    let private replaceFile (source: string) (target: string) (expected: string) =
        Directory.CreateDirectory(Path.GetDirectoryName(target)) |> ignore
        let temp = target + ".dlss5-" + Guid.NewGuid().ToString("N") + ".tmp"
        try
            File.Copy(source, temp, false)
            if not (sameHash (hashFile temp) expected) then invalidOp ("Source changed while copying: " + source)
            File.SetAttributes(temp, FileAttributes.Normal)
            use stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            stream.Flush(true)
            stream.Dispose()
            // Respect a read-only target; do not silently strip its protection.
            File.Move(temp, target, true)
        finally
            if File.Exists(temp) then File.Delete(temp)

    let private mutableFile (path: string) =
        [ ".ini"; ".cfg"; ".log" ] |> List.contains (Path.GetExtension(path).ToLowerInvariant())

    let private validateEntry roots backupRoot (entry: InstalledFile) =
        if isNull (box entry) then invalidOp "Invalid empty restore entry."
        validatePath roots entry.TargetPath
        if entry.WasExisting then
            validatePath [| backupRoot |] entry.BackupPath
            if not (present entry.BeforeSha256) then invalidOp ("Legacy backup has no checksum: " + entry.TargetPath)
            if not (sameHash (hashFile entry.BackupPath) entry.BeforeSha256) then
                invalidOp ("Backup missing or changed: " + entry.BackupPath)
            if present entry.BeforeWriteTimeUtc then
                DateTime.Parse(entry.BeforeWriteTimeUtc, null, Globalization.DateTimeStyles.RoundtripKind) |> ignore
        elif present entry.BeforeSha256 then invalidOp ("Unexpected backup hash: " + entry.TargetPath)

    /// All conflicts are found before the first restore write. Changed settings
    /// stay in the game folder; changed binaries block the entire operation.
    let checkRestore roots backupRoot (files: InstalledFile[]) =
        let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        [| for entry in files do
               try
                   validateEntry roots backupRoot entry
                   if not (seen.Add(Path.GetFullPath(entry.TargetPath))) then invalidOp "Duplicate restore target."
                   let current = hashFile entry.TargetPath
                   let before = if entry.WasExisting then entry.BeforeSha256 else ""
                   if not (sameHash current before) then
                       if isNull entry.InstalledSha256 then
                           invalidOp ("Legacy restore point cannot prove file ownership: " + entry.TargetPath)
                       elif present current && not (sameHash current entry.InstalledSha256) && not (mutableFile entry.TargetPath) then
                           invalidOp ("File changed after installation: " + entry.TargetPath)
               with ex -> yield ex.Message |]

    let restore roots backupRoot (files: InstalledFile[]) =
        let issues = checkRestore roots backupRoot files
        if issues.Length > 0 then invalidOp (String.Join(Environment.NewLine, issues))
        let kept = ResizeArray<string>()
        for entry in Array.rev files do
            // Check again immediately before each mutation; do not lose a game update.
            let issues = checkRestore roots backupRoot [| entry |]
            if issues.Length > 0 then invalidOp (String.Join(Environment.NewLine, issues))
            let current = hashFile entry.TargetPath
            let before = if entry.WasExisting then entry.BeforeSha256 else ""
            if present current && not (sameHash current before) && not (sameHash current entry.InstalledSha256) then
                kept.Add(entry.TargetPath)
            else
                if not (sameHash current before) then
                    if entry.WasExisting then replaceFile entry.BackupPath entry.TargetPath entry.BeforeSha256
                    elif File.Exists(entry.TargetPath) then File.Delete(entry.TargetPath)
                if entry.WasExisting then
                    if present entry.BeforeWriteTimeUtc then
                        File.SetLastWriteTimeUtc(entry.TargetPath, DateTime.Parse(entry.BeforeWriteTimeUtc, null, Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime())
                    File.SetAttributes(entry.TargetPath, enum<FileAttributes>(entry.BeforeAttributes))
        kept.ToArray()

    /// Serializes writers across manager processes. The lock file is kept so
    /// two processes can never lock different inodes after a delete/recreate.
    let acquireLock (backupRoot: string) =
        Directory.CreateDirectory(backupRoot) |> ignore
        new FileStream(Path.Combine(backupRoot, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)

    type Tracker(roots: string[], backupRoot: string, prior: InstalledFile[]) =
        let entries = Dictionary<string, InstalledFile>(StringComparer.OrdinalIgnoreCase)
        let snapshots = Dictionary<string, InstalledFile>(StringComparer.OrdinalIgnoreCase)
        let runRoot = Path.Combine(backupRoot, "attempts", Guid.NewGuid().ToString("N"))
        let mutable committed = false

        let snapshot (folder: string) target =
            validatePath roots target
            let before = hashFile target
            let exists = present before
            let backup = if exists then Path.Combine(folder, Guid.NewGuid().ToString("N") + ".bak") else ""
            let attributes = if exists then int (File.GetAttributes(target)) else 0
            let timestamp = if exists then File.GetLastWriteTimeUtc(target).ToString("o") else ""
            if exists then
                Directory.CreateDirectory(folder) |> ignore
                File.Copy(target, backup, false)
                if not (sameHash (hashFile backup) before) then invalidOp ("Backup verification failed: " + target)
            { TargetPath = target; BackupPath = backup; WasExisting = exists
              BeforeSha256 = before; InstalledSha256 = before
              BeforeAttributes = attributes; BeforeWriteTimeUtc = timestamp }

        do
            for entry in prior do
                validateEntry roots backupRoot entry
                if isNull entry.InstalledSha256 then invalidOp ("Legacy restore point requires manual review: " + entry.TargetPath)
                if not (entries.TryAdd(entry.TargetPath, entry)) then invalidOp "Duplicate restore target."
                let current = hashFile entry.TargetPath
                if present current && not (sameHash current entry.InstalledSha256) && not (mutableFile entry.TargetPath) then
                    invalidOp ("File changed after installation: " + entry.TargetPath)

        member _.IsCommitted = committed
        member _.Entries = entries.Values |> Seq.filter (fun e -> e.WasExisting || present e.InstalledSha256) |> Seq.toArray

        /// Must be called before an external installer touches a known target.
        member _.Prepare(target: string) =
            validatePath roots target
            if not (snapshots.ContainsKey(target)) then
                match entries.TryGetValue(target) with
                | true, entry ->
                    let current = hashFile target
                    if present current && not (sameHash current entry.InstalledSha256) && not (mutableFile target) then
                        invalidOp ("File changed before deployment: " + target)
                | _ -> ()
                snapshots.[target] <- snapshot runRoot target
            elif not (sameHash (hashFile target) snapshots.[target].InstalledSha256) then
                invalidOp ("File changed during deployment: " + target)
            if not (entries.ContainsKey(target)) then entries.[target] <- snapshot backupRoot target

        member this.Observe(target: string) =
            let actual = hashFile target
            entries.[target] <- { entries.[target] with InstalledSha256 = actual }
            snapshots.[target] <- { snapshots.[target] with InstalledSha256 = actual }

        member this.Copy(source: string, target: string) =
            let expected = hashFile source
            if not (present expected) then invalidOp ("Payload is missing: " + source)
            this.Prepare(target)
            // Record the expected new bytes before the atomic replacement.
            entries.[target] <- { entries.[target] with InstalledSha256 = expected }
            snapshots.[target] <- { snapshots.[target] with InstalledSha256 = expected }
            replaceFile source target expected

        member this.Delete(target: string) =
            this.Prepare(target)
            File.Delete(target)
            this.Observe(target)

        member this.External(targets: string[], action: unit -> 'T) : 'T =
            targets |> Array.iter this.Prepare
            try action ()
            finally targets |> Array.iter this.Observe

        member this.WriteText(target: string, text: string) =
            let staging = Path.GetTempFileName()
            try
                File.WriteAllText(staging, text)
                this.Copy(staging, target)
            finally File.Delete(staging)

        member _.Commit() =
            committed <- true
            // Snapshot cleanup is best effort after the durable manifest commit.
            try if Directory.Exists(runRoot) then Directory.Delete(runRoot, true) with _ -> ()

        member _.Rollback() =
            if committed then [||]
            else
                let files = snapshots.Values |> Seq.toArray
                let errors = ResizeArray<string>()
                for entry in Array.rev files do
                    try
                        validateEntry roots backupRoot entry
                        let current = hashFile entry.TargetPath
                        if not (sameHash current entry.BeforeSha256) then
                            if not (sameHash current entry.InstalledSha256) then
                                invalidOp ("Recovery blocked by external modification: " + entry.TargetPath)
                            if entry.WasExisting then replaceFile entry.BackupPath entry.TargetPath entry.BeforeSha256
                            elif File.Exists(entry.TargetPath) then File.Delete(entry.TargetPath)
                        if entry.WasExisting then
                            File.SetLastWriteTimeUtc(entry.TargetPath, DateTime.Parse(entry.BeforeWriteTimeUtc).ToUniversalTime())
                            File.SetAttributes(entry.TargetPath, enum<FileAttributes>(entry.BeforeAttributes))
                    with ex -> errors.Add(ex.Message)
                if errors.Count > 0 then
                    writeJsonAtomic (Path.Combine(runRoot, "recovery.json")) files
                    errors.Add("Recovery snapshots retained at: " + runRoot)
                else
                    try if Directory.Exists(runRoot) then Directory.Delete(runRoot, true) with _ -> ()
                errors.ToArray()
