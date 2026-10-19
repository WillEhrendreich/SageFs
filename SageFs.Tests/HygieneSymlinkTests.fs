/// Hygiene when the caller's path to the repository goes through a symlink, which is every temp dir on a Mac
/// (`/var/folders` is a link to `/private/var/folders`) and every checkout under a linked home. Git prints real
/// paths, so the scan has to name the same directory the caller did, in the one spelling the OS gives it.
module SageFs.Tests.HygieneSymlinkTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.HygieneGather
open SageFs.Tests.HygieneSandbox

/// A real directory and a symlink to it, the shape a Mac's temp dir has. Disposing removes both.
type LinkedParent() =
  // The temp dir may itself sit behind a link (a Mac's does), so `real` is only real once `outer` is. The link under
  // test is the one made below, and `real` is the directory it points at by construction.
  let outer = canonicalPath (Directory.CreateTempSubdirectory("sagefs-hygiene-link-").FullName)
  let real = Path.Combine(outer, "real")
  let link = Path.Combine(outer, "link")
  do
    Directory.CreateDirectory real |> ignore
    Directory.CreateSymbolicLink(link, real) |> ignore
  /// The directory itself.
  member _.Real = real
  /// The same directory, reached through the link.
  member _.Link = link
  interface IDisposable with
    member _.Dispose() = try Directory.Delete(outer, true) with _ -> ()
  interface IAsyncDisposable with
    member this.DisposeAsync() =
      (this :> IDisposable).Dispose()
      System.Threading.Tasks.ValueTask()

/// `path` with its leading `fromRoot` swapped for `toRoot`: the same place, spelled the other way.
let private respell (fromRoot: string) (toRoot: string) (path: string) : string =
  match path.StartsWith(fromRoot, StringComparison.Ordinal) with
  | true -> toRoot + path.Substring fromRoot.Length
  | false -> path

let private respellLocations (fromRoot: string) (toRoot: string) (loc: Locations) : Locations =
  let r = respell fromRoot toRoot
  { loc with Repo = r loc.Repo; GateDir = r loc.GateDir; DataDir = r loc.DataDir; HostCacheDir = r loc.HostCacheDir; TempDir = r loc.TempDir }

let private confirm (plan: Plan) : Confirmation =
  match Confirmation.safeOnly plan plan.Id with
  | Result.Ok c -> c
  | Result.Error e -> failtestf "could not confirm: %A" e

let private noneAlive : int -> int64 option -> bool = fun _ _ -> false

/// The sandbox, made under a symlinked parent, with the locations a caller who holds the link's spelling would pass.
/// `realRoot` is where the sandbox really is, spelled by the OS with no link in it.
type LinkedSandbox(parent: LinkedParent) =
  let sb = new Sandbox(parent.Link)
  let name = Path.GetFileName sb.Root
  let linkRoot = Path.Combine(parent.Link, name)
  let realRoot = Path.Combine(parent.Real, name)
  member _.Sandbox = sb
  member _.RealRoot = realRoot
  /// The directory of a worktree the sandbox made, spelled as the OS would print it.
  member _.RealWorktree(worktree: string) : string = Path.Combine(realRoot, "repo", ".claude", "worktrees", worktree)
  /// The repository and a worktree of it, spelled through the link, as a session's working directory would be.
  member _.LinkRepo : string = Path.Combine(linkRoot, "repo")
  member _.LinkWorktree(worktree: string) : string = Path.Combine(linkRoot, "repo", ".claude", "worktrees", worktree)
  member this.LocationsAsCallerSpelledThem : Locations = respellLocations sb.Root linkRoot sb.Locations
  member this.Scan() : Scan = { sb.Scan() with Loc = this.LocationsAsCallerSpelledThem }
  member this.Roots : Roots = rootsOf this.LocationsAsCallerSpelledThem
  member this.Effects : AsyncEffects =
    HygieneEdge.effects { MakeScan = (fun () -> this.Scan()); Git = runGit; IsAlive = noneAlive }
  interface IDisposable with
    member _.Dispose() = (sb :> IDisposable).Dispose()
  interface IAsyncDisposable with
    member this.DisposeAsync() =
      (this :> IDisposable).Dispose()
      System.Threading.Tasks.ValueTask()

let private targetsOf (leftovers: Leftover list) : string list =
  leftovers |> List.map (fun l -> Target.describe (Leftover.target l))

/// These cases make a directory symlink, and creating one on Windows needs a privilege a runner may not hold, so they are
/// registered only where a symlink can be made. On Windows they do not exist; they are not skipped.
let private linkCases : Test list = [
    testTask "each worktree is found at the path the OS prints for it, with the standing its git state earns" {
      use parent = new LinkedParent()
      use linked = new LinkedSandbox(parent)
      populate linked.Sandbox |> ignore
      let! leftovers = gather (linked.Scan()) None
      let at name = standingOfPath leftovers (linked.RealWorktree name)
      at "clean-merged" |> Expect.equal "merged at the base head is an ancestor" (Standing.Merged MergeHow.Ancestor)
      at "squashed" |> Expect.equal "cherry-picked is patch-equivalent" (Standing.Merged MergeHow.PatchEquivalent)
      match at "dirty-real" with
      | Standing.DirtyReal _ -> ()
      | other -> failtestf "expected DirtyReal, got %A" other
    }

    testTask "no target the scan names goes through the link" {
      use parent = new LinkedParent()
      use linked = new LinkedSandbox(parent)
      populate linked.Sandbox |> ignore
      let! leftovers = gather (linked.Scan()) None
      let throughTheLink = targetsOf leftovers |> List.filter (fun t -> t.Contains parent.Link)
      throughTheLink |> Expect.isEmpty "every target is spelled the way the OS prints it"
    }

    testTask "tidy removes what is merged, because the path git prints is inside the root the caller named" {
      use parent = new LinkedParent()
      use linked = new LinkedSandbox(parent)
      populate linked.Sandbox |> ignore
      let! found = gather (linked.Scan()) None
      let plan = Planner.plan found
      let! _ = Executor.runAsync linked.Effects linked.Roots (confirm plan) plan
      Directory.Exists(linked.RealWorktree "clean-merged") |> Expect.isFalse "the merged worktree is gone"
      Directory.Exists(linked.RealWorktree "unmerged") |> Expect.isTrue "the unmerged one is still there"
      Directory.Exists(linked.RealWorktree "dirty-real") |> Expect.isTrue "the one with real work is still there"
    }

    testTask "a scan made with the link's spelling is the one a panel asking from the link's spelling reads back" {
      use parent = new LinkedParent()
      use linked = new LinkedSandbox(parent)
      populate linked.Sandbox |> ignore
      let! (snapshot: HygieneService.Snapshot) = HygieneService.Cache.refresh linked.LocationsAsCallerSpelledThem (fun () -> LiveFacts.none)
      let realRepo = Path.Combine(linked.RealRoot, "repo")
      snapshot.Repo |> Expect.equal "the snapshot names the repository the way the OS prints it" realRepo
      for asked in [ linked.LinkRepo; linked.LinkWorktree "clean-merged" ] do
        match HygieneService.viewFor asked with
        | HygieneService.HygieneView.Scanned((shown: HygieneService.Snapshot), _) -> shown.Repo |> Expect.equal (sprintf "the view from %s is the scan" asked) realRepo
        | HygieneService.HygieneView.NotScanned repo -> failtestf "from %s: NotScanned %s" asked repo
        | HygieneService.HygieneView.Scanning repo -> failtestf "from %s: Scanning %s" asked repo
        | HygieneService.HygieneView.Tidying repo -> failtestf "from %s: Tidying %s" asked repo
        | HygieneService.HygieneView.ScanFailed(repo, why) -> failtestf "from %s: ScanFailed %s (%s)" asked repo why
        | HygieneService.HygieneView.NoRepository -> failtestf "from %s: NoRepository" asked
    }

  ]

[<Tests>]
let symlinkTests =
  let registered = match OperatingSystem.IsWindows() with | true -> [] | false -> linkCases
  testList "Workspace hygiene: a repository reached through a symlink" (registered @ [
    testCase "a lookup that finds nothing names the path it was asked for and the targets it had" <| fun _ ->
      let had : Leftover list = []
      let message =
        try
          standingOfPath had "/no/such/dir" |> ignore
          ""
        with ex -> ex.Message
      message |> Expect.stringContains "names the path asked for" "/no/such/dir"
      message |> Expect.stringContains "says how many targets it had" "0 target"
  ])
