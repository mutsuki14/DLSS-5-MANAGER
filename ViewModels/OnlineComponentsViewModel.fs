namespace DLSS_5_MANAGER.ViewModels

open System
open System.Collections.ObjectModel
open System.Threading
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.Services.ComponentReleases

/// The user chooses a source, a release channel/version and its game-specific asset.
type OnlineComponentsViewModel(changed: unit -> unit) =
    inherit ViewModelBase()
    let versions = ObservableCollection<Release>()
    let assets = ObservableCollection<Asset>()
    let pending = ObservableCollection<Cached>()
    let mutable selectedSource = sources.[0]
    let mutable selectedVersion = Unchecked.defaultof<Release>
    let mutable selectedAsset = Unchecked.defaultof<Asset>
    let mutable includePreview = false
    let mutable latest = true
    let mutable loaded = false
    member _.Sources = sources
    member _.Versions = versions
    member _.Assets = assets
    member _.Pending = pending
    member _.HasPending = pending.Count > 0
    member _.SourceInfo = "https://github.com/" + selectedSource.Repository + "/releases"
    member this.SelectedSource
        with get () = selectedSource
        and set value =
            if not (isNull (box value)) && this.SetProperty(&selectedSource,value) then
                loaded <- false
                versions.Clear(); assets.Clear()
                selectedVersion <- Unchecked.defaultof<Release>; selectedAsset <- Unchecked.defaultof<Asset>
                this.IncludePreview <- value.Nightly
                for p in ["SelectedVersion";"SelectedAsset";"SourceInfo"] do this.RaisePropertyChanged(p)
    member this.IncludePreview
        with get () = includePreview
        and set value =
            if this.SetProperty(&includePreview,value) then loaded <- false
    member this.UseLatest
        with get () = latest
        and set value = this.SetProperty(&latest,value) |> ignore
    member this.SelectedVersion
        with get () = selectedVersion
        and set value =
            if this.SetProperty(&selectedVersion,value) then
                assets.Clear()
                if not (isNull (box value)) then for a in value.Assets do assets.Add(a)
                selectedAsset <- if assets.Count=1 then assets.[0] else Unchecked.defaultof<Asset>
                this.RaisePropertyChanged("SelectedAsset")
    member this.SelectedAsset
        with get () = selectedAsset
        and set value = this.SetProperty(&selectedAsset,value) |> ignore
    member this.SetVersions(releases: Release[], exe: string) =
        versions.Clear()
        for r in releases do versions.Add(r)
        this.SelectedVersion <- if versions.Count>0 then versions.[0] else Unchecked.defaultof<Release>
        if versions.Count>0 then
            try this.SelectedAsset <- ComponentPackages.chooseAsset selectedSource exe "" versions.[0]
            with _ -> ()
        loaded <- true
    member this.Add(cached: Cached) =
        let group = (source cached.Source).Group
        for i in pending.Count-1 .. -1 .. 0 do if (source pending.[i].Source).Group=group then pending.RemoveAt(i)
        pending.Add(cached)
        this.RaisePropertyChanged("HasPending")
        changed()
    member this.Remove(cached: Cached) =
        pending.Remove(cached) |> ignore
        this.RaisePropertyChanged("HasPending")
        changed()
    member this.Clear() =
        pending.Clear()
        this.RaisePropertyChanged("HasPending")
        changed()
    member _.Download(exe: string, report: string -> unit, ct: CancellationToken) = async {
        let s, requested, wanted, allowPreview, current = selectedSource,selectedVersion,selectedAsset,includePreview,latest
        let! release = async {
            if current then
                let! releases = listReleases s allowPreview ct
                return releases |> Array.tryHead |> Option.defaultWith (fun () -> invalidOp "没有可用 Release；Nightly 来源需要允许预发布 / No matching release; enable previews for Nightly.")
            else
                if not loaded || isNull (box requested) then invalidOp "先刷新版本列表，再选择固定版本 / Refresh the catalog and choose a fixed release."
                if requested.Preview && not allowPreview then invalidOp "请开启预发布选项 / Enable preview releases."
                return requested }
        let preferred = if isNull (box wanted) then "" else wanted.Name
        let asset = ComponentPackages.chooseAsset s exe preferred release
        return! download (defaultStore ()) s release asset report ct
    }
