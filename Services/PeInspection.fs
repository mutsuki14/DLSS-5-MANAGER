namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Text
open System.Reflection.PortableExecutable

/// Static PE inspection only; never loads the selected executable or its DLLs.
/// Layout: https://learn.microsoft.com/windows/win32/debug/pe-format
module PeInspection =
    type Image =
        { Architecture: string
          Imports: string[]
          GraphicsApis: string[] }

    let private apiForImport (name: string) =
        match name.ToLowerInvariant() with
        | "d3d12.dll" -> Some "dx12"
        | "d3d11.dll" -> Some "dx11"
        | "d3d10.dll" | "d3d10_1.dll" -> Some "dx10"
        | "d3d9.dll" -> Some "dx9"
        | "d3d8.dll" -> Some "dx8"
        | "ddraw.dll" -> Some "ddraw"
        | "opengl32.dll" -> Some "opengl"
        | "vulkan-1.dll" -> Some "vulkan"
        | _ -> None // dxgi alone cannot distinguish DX10, DX11 and DX12.

    let inspect (path: string) : Image option =
        try
            use stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
            use pe = new PEReader(stream)
            let header = pe.PEHeaders.PEHeader
            if isNull header then None
            else
                let architecture =
                    match pe.PEHeaders.CoffHeader.Machine with
                    | Machine.I386 -> "32"
                    | Machine.Amd64 -> "64"
                    | Machine.Arm64 -> "arm64"
                    | _ -> "unsupported"
                let imports = ResizeArray<string>()
                let readName (rva: int) =
                    let block = pe.GetSectionData(rva)
                    let bytes = block.GetContent(0, min 260 block.Length) |> Seq.toArray
                    match Array.tryFindIndex ((=) 0uy) bytes with
                    | Some n when n > 0 ->
                        let name = Encoding.ASCII.GetString(bytes, 0, n).ToLowerInvariant()
                        if not (name.Contains('/') || name.Contains('\\')) then imports.Add(name)
                    | _ -> ()
                let readTable (directory: DirectoryEntry) stride nameOffset delay =
                    if directory.RelativeVirtualAddress > 0 && directory.Size >= stride then
                        let block = pe.GetSectionData(directory.RelativeVirtualAddress)
                        let size = min block.Length (min directory.Size (stride * 4096))
                        let bytes = block.GetContent(0, size) |> Seq.toArray
                        let mutable offset = 0
                        let mutable doneReading = false
                        while offset + stride <= bytes.Length && not doneReading do
                            if bytes.[offset..offset + stride - 1] |> Array.forall ((=) 0uy) then
                                doneReading <- true
                            else
                                let raw = uint64 (BitConverter.ToUInt32(bytes, offset + nameOffset))
                                // Old delay-load descriptors use VAs; modern ones use RVAs.
                                let rva =
                                    if delay && (BitConverter.ToUInt32(bytes, offset) &&& 1u) = 0u then
                                        if raw >= header.ImageBase then raw - header.ImageBase else UInt64.MaxValue
                                    else raw
                                if rva > 0UL && rva <= uint64 Int32.MaxValue then readName (int rva)
                                offset <- offset + stride
                readTable header.ImportTableDirectory 20 12 false
                readTable header.DelayImportTableDirectory 32 4 true
                let names = imports |> Seq.distinct |> Seq.sort |> Seq.toArray
                Some { Architecture = architecture; Imports = names
                       GraphicsApis = names |> Array.choose apiForImport |> Array.distinct |> Array.sort }
        with
        | :? IOException | :? UnauthorizedAccessException | :? BadImageFormatException
        | :? ArgumentException | :? InvalidOperationException | :? OverflowException -> None
