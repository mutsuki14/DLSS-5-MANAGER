namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Collections.Generic
open System.Threading
open SharpCompress.Archives
open SharpCompress.Common

/// Only extracts bytes into a new cache staging directory. Never runs release scripts.
module ComponentArchives =
    let extract path destination (ct: CancellationToken) =
        use archive = ArchiveFactory.OpenArchive(path: string)
        if archive.Type <> ArchiveType.Zip && archive.Type <> ArchiveType.SevenZip then invalidOp "Only ZIP and 7z component archives are supported."
        let names = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let entries = archive.Entries |> Seq.truncate 10001 |> Seq.toArray
        if entries.Length > 10000 || not archive.IsComplete then invalidOp "Incomplete archive or too many entries."
        let mutable total = 0L
        for e in entries do
            let name = RuntimePackages.relativePath (e.Key.TrimEnd('/','\\'))
            if not (names.Add(name)) || e.IsEncrypted || not (String.IsNullOrEmpty(e.LinkTarget)) ||
               (e.Attrib.HasValue && ((e.Attrib.Value &&& 0x400) <> 0 || (e.Attrib.Value >>> 16 &&& 0xF000) = 0xA000)) then invalidOp "Duplicate, encrypted or linked archive entry."
            total <- total + e.Size
            if e.Size < 0L || e.Size > 1024L*1024L*1024L || total > 4L*1024L*1024L*1024L then invalidOp "Component archive exceeds extraction limits."
        if Directory.Exists(destination) then invalidOp "Component extraction needs a new staging directory."
        Directory.CreateDirectory(destination) |> ignore
        try
            let buffer = Array.zeroCreate<byte> 131072
            let copyEntry (e: IEntry) (input: Stream) =
                ct.ThrowIfCancellationRequested()
                if not e.IsDirectory then
                    let target = RuntimePackages.resolve destination e.Key
                    Directory.CreateDirectory(Path.GetDirectoryName(target)) |> ignore
                    use output = new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None)
                    let mutable count, read = 0L, 1
                    while read > 0 do
                        ct.ThrowIfCancellationRequested()
                        read <- input.Read(buffer,0,buffer.Length)
                        count <- count + int64 read
                        if count > e.Size then invalidOp "Archive payload exceeds declared size."
                        output.Write(buffer,0,read)
                    if count <> e.Size then invalidOp "Truncated archive payload."
            if archive.Type = ArchiveType.SevenZip then
                // Solid 7z archives must be read sequentially, including skipped entries.
                use reader = archive.ExtractAllEntries()
                while reader.MoveToNextEntry() do
                    ct.ThrowIfCancellationRequested()
                    if not reader.Entry.IsDirectory then
                        use input = reader.OpenEntryStream()
                        copyEntry reader.Entry input
            else
                for entry in entries do
                    ct.ThrowIfCancellationRequested()
                    if not entry.IsDirectory then
                        use input = entry.OpenEntryStream()
                        copyEntry entry input
            Directory.GetFiles(destination,"*",SearchOption.AllDirectories)
        with _ ->
            Directory.Delete(destination,true)
            reraise()
