namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Security.Cryptography
open System.Threading

/// GitHub release identities are pinned before any bytes enter an installation plan.
module ComponentReleases =
    type Source =
        { Id: string; Group: string; Name: string; Repository: string; TagPattern: string; AssetPattern: string; Nightly: bool }
        override this.ToString() = this.Name
    [<CLIMutable>]
    type Asset = { Id: int64; Name: string; Url: string; Size: int64; Digest: string; Updated: string }
    [<CLIMutable>]
    type Release =
        { Id: int64; Tag: string; Published: DateTimeOffset; Preview: bool; Assets: Asset[] }
        override this.ToString() = this.Tag + (if this.Preview then " · 预发布 / Preview" else "")
    [<CLIMutable>]
    type Receipt = { SourceId: string; Repository: string; Release: Release; Asset: Asset; Sha256: string }
    type Cached =
        { Root: string; Receipt: Receipt }
        member this.Source = this.Receipt.SourceId
        member this.Display = sprintf "%s · %s · %s" this.Receipt.Repository this.Receipt.Release.Tag this.Receipt.Asset.Name
        override this.ToString() = this.Display

    let sources = [|
        { Id="aurora"; Group="optiscaler"; Name="OptiScaler Aurora"; Repository="abc354402600/OptiScaler-Aurora"; TagPattern="^(aurora-v|nightly)"; AssetPattern="(?i)^OptiScaler.*\\.(7z|zip)$"; Nightly=false }
        { Id="reframework-nightly"; Group="reframework"; Name="REFramework · 通用 Nightly"; Repository="praydog/REFramework-nightly"; TagPattern=".*"; AssetPattern="^REFramework\\.zip$"; Nightly=true }
        { Id="reframework"; Group="reframework"; Name="REFramework · 正式版（按游戏）"; Repository="praydog/REFramework"; TagPattern=".*"; AssetPattern="^(RE[23478](_TDB[0-9]+)?|DMC5|MHRISE|MHWILDS|SF6|DD2)\\.zip$"; Nightly=false }
        { Id="feeder"; Group="feeder"; Name="DLSS5 Feeder"; Repository="jlrouzies-fr/DLSS5-Feeder"; TagPattern=".*"; AssetPattern="(?i)^DLSS5-Feeder-.*\\.zip$"; Nightly=false }
        { Id="renodx"; Group="renodx"; Name="RenoDX DLSS5 · Krish（RHI 分发）"; Repository="RankFTW/rhi-repo"; TagPattern="^renodx-dlss5-"; AssetPattern="^renodx-dlss5_.*\\.zip$"; Nightly=false }
        { Id="renodx-sf"; Group="renodx"; Name="RenoDX DLSS · ShortFuse（RHI 分发）"; Repository="RankFTW/rhi-repo"; TagPattern="^renodx-dlss-SF-"; AssetPattern="^renodx-dlss_SF_.*\\.zip$"; Nightly=false }
        { Id="dlssnr"; Group="dlssnr"; Name="DLSS Neural Rendering 模型（RHI 分发）"; Repository="RankFTW/rhi-repo"; TagPattern="^dlssnr-[0-9.]+$"; AssetPattern="^nvngx_dlssnr_[0-9.]+\\.zip$"; Nightly=false }
        { Id="dlss-sr"; Group="dlss-sr"; Name="DLSS Super Resolution（RHI 分发）"; Repository="RankFTW/rhi-repo"; TagPattern="^dlss-[0-9.]+$"; AssetPattern="^nvngx_dlss_[0-9.]+\\.zip$"; Nightly=false }
    |]
    let source id = sources |> Array.find (fun s -> s.Id = id)
    let defaultStore () = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DLSS5Manager","ComponentDownloads")
    let private str (e: JsonElement) name = match e.TryGetProperty(name: string) with true,v when v.ValueKind = JsonValueKind.String -> v.GetString() | _ -> ""
    let private flag (e: JsonElement) name = match e.TryGetProperty(name: string) with true,v when v.ValueKind = JsonValueKind.True -> true | _ -> false
    let private isPreview (s: Source) (tag: string) marked = s.Nightly || marked || Regex.IsMatch(tag,"(?i)(alpha|beta|preview|nightly|snapshot|(^|[-.])pre[0-9]|(^|[-.])rc[0-9])")

    let validateAssetUrl (s: Source) (asset: Asset) =
        let uri = Uri(asset.Url, UriKind.Absolute)
        if uri.Scheme <> "https" || uri.Host <> "github.com" || not uri.IsDefaultPort || uri.UserInfo <> "" || uri.Query <> "" || uri.Fragment <> "" ||
           not (uri.AbsolutePath.StartsWith("/" + s.Repository + "/releases/download/",StringComparison.Ordinal)) then invalidOp "Release asset URL is outside its selected GitHub source."
        RuntimePackages.relativePath asset.Name |> ignore
        if asset.Name.Contains('/') || asset.Name.Contains('\\') || asset.Id <= 0L || asset.Size <= 0L || asset.Size > 1024L*1024L*1024L then invalidOp "Invalid release asset identity or size."
        if asset.Digest <> "" && not (Regex.IsMatch(asset.Digest,"^sha256:[a-fA-F0-9]{64}$")) then invalidOp "Unsupported release checksum."

    let parseReleases (s: Source) includePreview json =
        use doc = JsonDocument.Parse(json: string)
        if doc.RootElement.ValueKind <> JsonValueKind.Array then invalidOp "GitHub did not return a release list."
        [| for r in doc.RootElement.EnumerateArray() do
               let tag = str r "tag_name"
               let preview = isPreview s tag (flag r "prerelease")
               if not (flag r "draft") && (includePreview || not preview) && Regex.IsMatch(tag,s.TagPattern) then
                   let assets = [| for a in r.GetProperty("assets").EnumerateArray() do
                                       let name = str a "name"
                                       if Regex.IsMatch(name,s.AssetPattern) && str a "state" = "uploaded" then
                                           let asset = { Id=a.GetProperty("id").GetInt64(); Name=name; Url=str a "browser_download_url"; Size=a.GetProperty("size").GetInt64(); Digest=str a "digest"; Updated=str a "updated_at" }
                                           validateAssetUrl s asset
                                           yield asset |]
                   if assets.Length > 0 then
                       yield { Id=r.GetProperty("id").GetInt64();Tag=tag;Published=DateTimeOffset.Parse(str r "published_at");Preview=preview;Assets=assets }
        |] |> Array.sortByDescending (fun r -> r.Published, r.Id)

    let private http =
        let handler = new HttpClientHandler(AllowAutoRedirect=false)
        let client = new HttpClient(handler, Timeout=TimeSpan.FromMinutes(15.0))
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5Manager-Fork/1.3")
        client

    let private send (uri: Uri) (ct: CancellationToken) = async {
        let mutable target = uri
        let mutable result: HttpResponseMessage option = None
        let mutable hops = 0
        while result.IsNone do
            ct.ThrowIfCancellationRequested()
            let! response = http.GetAsync(target,HttpCompletionOption.ResponseHeadersRead,ct) |> Async.AwaitTask
            if int response.StatusCode >= 300 && int response.StatusCode < 400 then
                use response = response
                hops <- hops + 1
                if hops > 5 || isNull response.Headers.Location then invalidOp "Invalid download redirect."
                let next = if response.Headers.Location.IsAbsoluteUri then response.Headers.Location else Uri(target,response.Headers.Location)
                if next.Scheme <> "https" || not next.IsDefaultPort || next.UserInfo <> "" ||
                   not ([|"github.com";"api.github.com";"release-assets.githubusercontent.com";"objects.githubusercontent.com"|] |> Array.contains next.Host) then invalidOp "Download redirected outside GitHub's asset hosts."
                target <- next
            elif response.StatusCode = HttpStatusCode.Forbidden || int response.StatusCode = 429 then
                response.Dispose()
                invalidOp "GitHub 请求受限，请稍后重试 / GitHub rate limit or access restriction; retry later."
            elif not response.IsSuccessStatusCode then
                let code = int response.StatusCode
                response.Dispose()
                invalidOp (sprintf "GitHub HTTP %d; no files were applied." code)
            else result <- Some response
        return result.Value
    }

    let private copyBounded (input: Stream) (output: Stream) limit expected (ct: CancellationToken) report = async {
        let bytes = Array.zeroCreate<byte> 131072
        let mutable total, read = 0L, 1
        while read > 0 do
            let! n = input.ReadAsync(bytes,0,bytes.Length,ct) |> Async.AwaitTask
            read <- n; total <- total + int64 n
            if total > limit then invalidOp "Download exceeds the declared size limit."
            do! output.WriteAsync(bytes,0,n,ct) |> Async.AwaitTask
            report total
        if expected >= 0L && total <> expected then invalidOp "Download was truncated; nothing was activated."
    }

    let listReleases (s: Source) includePreview (ct: CancellationToken) = async {
        let found = ResizeArray<Release>()
        let mutable page, more = 1, true
        while more && page <= 4 do
            use! response = send (Uri(sprintf "https://api.github.com/repos/%s/releases?per_page=50&page=%d" s.Repository page)) ct
            use! input = response.Content.ReadAsStreamAsync(ct) |> Async.AwaitTask
            use memory = new MemoryStream()
            do! copyBounded input memory (16L*1024L*1024L) -1L ct ignore
            let json = Encoding.UTF8.GetString(memory.ToArray())
            found.AddRange(parseReleases s includePreview json)
            use doc = JsonDocument.Parse(json)
            more <- doc.RootElement.GetArrayLength() = 50
            page <- page + 1
        return found.ToArray() |> Array.distinctBy (fun r -> r.Id) |> Array.sortByDescending (fun r -> r.Published,r.Id)
    }

    let cacheKey (s: Source) (release: Release) (asset: Asset) =
        JsonSerializer.Serialize((s.Id,s.Repository,release.Id,release.Tag,asset)) |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> fun s -> s.ToLowerInvariant()

    let verifyCached (cached: Cached) =
        let s = source cached.Source
        if cached.Receipt.Repository <> s.Repository then invalidOp "Cached component source changed."
        validateAssetUrl s cached.Receipt.Asset
        let path = RuntimePackages.resolve cached.Root cached.Receipt.Asset.Name
        if not (File.Exists(path)) || FileInfo(path).Length <> cached.Receipt.Asset.Size || DeploymentSafety.hashFile path <> cached.Receipt.Sha256 then invalidOp "Cached component changed; download it again."
        if cached.Receipt.Asset.Digest <> "" && not (cached.Receipt.Asset.Digest.Substring(7).Equals(cached.Receipt.Sha256,StringComparison.OrdinalIgnoreCase)) then invalidOp "Cached release checksum mismatch."
        path

    let downloadUsing (request: Uri -> CancellationToken -> Async<HttpResponseMessage>) store (s: Source) (release: Release) (asset: Asset) report (ct: CancellationToken) = async {
        validateAssetUrl s asset
        if not (release.Assets |> Array.contains asset) then invalidOp "Asset does not belong to the selected release."
        let root = Path.Combine(Path.GetFullPath(store),cacheKey s release asset)
        Directory.CreateDirectory(store) |> ignore
        use cacheLock = DeploymentSafety.acquireLock (Path.Combine(store,"locks",Path.GetFileName(root)))
        ct.ThrowIfCancellationRequested()
        let cached =
            if Directory.Exists(root) then
                try
                    let receiptPath = RuntimePackages.resolve root "receipt.json"
                    let receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(receiptPath))
                    if isNull (box receipt) || isNull (box receipt.Release) || receipt.SourceId <> s.Id || receipt.Release.Id <> release.Id || receipt.Release.Tag <> release.Tag || receipt.Asset <> asset then invalidOp "Cache receipt does not match this release."
                    let cached = { Root=root; Receipt=receipt }
                    verifyCached cached |> ignore
                    Some cached
                with
                | :? IOException | :? JsonException | :? InvalidOperationException ->
                    report "重新下载损坏的缓存 / Downloading a fresh copy of the damaged cache..."
                    Directory.Delete(root,true)
                    None
            else None
        match cached with
        | Some cached ->
            report ("复用已校验下载 / Using verified cache: " + asset.Name)
            return cached
        | None ->
            let staging = Path.Combine(store,".download-"+Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(staging) |> ignore
            try
                let path = RuntimePackages.resolve staging asset.Name
                use! response = request (Uri(asset.Url)) ct
                use! input = response.Content.ReadAsStreamAsync(ct) |> Async.AwaitTask
                do! async {
                    use output = new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)
                    let mutable last = -1L
                    do! copyBounded input output asset.Size asset.Size ct (fun count ->
                        let percent = count * 100L / asset.Size
                        if percent >= last + 5L then last <- percent; report (sprintf "%s · %d%%" asset.Name percent))
                    output.Flush(true)
                }
                let digest = DeploymentSafety.hashFile path
                if asset.Digest <> "" && not (asset.Digest.Substring(7).Equals(digest,StringComparison.OrdinalIgnoreCase)) then invalidOp "GitHub SHA-256 mismatch; download discarded."
                let receipt = {SourceId=s.Id;Repository=s.Repository;Release=release;Asset=asset;Sha256=digest}
                File.WriteAllText(Path.Combine(staging,"receipt.json"),JsonSerializer.Serialize(receipt))
                ct.ThrowIfCancellationRequested()
                Directory.Move(staging,root)
                return { Root=root; Receipt=receipt }
            finally
                if Directory.Exists(staging) then Directory.Delete(staging,true)
    }

    let download store s release asset report ct = downloadUsing send store s release asset report ct
