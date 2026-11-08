/// The workspace hygiene ledger: an append-only record of what the daemon saw of each agent worktree and what it did about it.
///
/// It exists so two facts are READ and not guessed. First, who made a worktree and whether they are finished with it, which is
/// what "resumable" means: a merged worktree whose agent went quiet an hour ago may still be resumed, one that went quiet
/// last week may not. Second, what was removed and where its tip was, so anything the daemon tidied can be put back.
///
/// Everything here is pure. A sweep observes the worktrees (`Observed`), `transitions` says which lines to append, and
/// `fold` reads the lines back into one `WorktreeRecord` per path. The file and the clock are the edge's
/// (`SageFs/HygieneLedgerStore.fs`).
module SageFs.HygieneLedger

open System
open SageFs.WorkspaceHygiene

/// Who made a worktree, as far as anything recorded. The harness names its agents `agent-<id>`, in the directory and in the git
/// lock it holds while the agent runs; a SageFS session records its MCP connection, whose agent name is only a label.
[<RequireQualifiedAccess>]
type Maker =
  | HarnessAgent of agentId: string
  | SessionOwner of agentName: string * connectionId: string
  | MakerUnrecorded

/// The commit a worktree or branch pointed at, when a removal could read it.
[<RequireQualifiedAccess>]
type Tip =
  | AtTip of sha: string
  | TipUnknown

/// Whether what was removed held work that had reached the base branch, and how.
[<RequireQualifiedAccess>]
type MergeState =
  | MergedBy of MergeHow
  | NotMerged
  /// Git could not say, so nothing may be assumed: not merged, and not unmerged either. The edge refuses to remove on it.
  | Undecided
  /// What was removed holds no work (a log, a temp run, a registry entry), so there is no merge to speak of.
  | NotAboutWork

/// One thing removed, with where its tip was, so it can be restored.
type Removal =
  { Kind: LeftoverKind
    Target: string
    Branch: BranchLabel
    /// `git branch restore/<name> <sha>` brings the work back.
    Tip: Tip
    Merged: MergeState
    Why: string }

[<RequireQualifiedAccess>]
type LedgerEvent =
  /// A worktree the sweep had not seen before.
  | FirstSeen of path: string * branch: BranchLabel * tip: string * maker: Maker
  /// A worktree that was in use at the last sweep and is not any more: its agent is finished with it, for now.
  | WentQuiet of path: string
  /// A worktree that was quiet and is in use again: somebody resumed it.
  | Resumed of path: string
  /// Uncommitted files were committed to a branch.
  | Salvaged of path: string * branch: string * sha: string * files: int
  | Removed of Removal
  /// A worktree the ledger had on disk was not among the worktrees git listed: somebody removed it by hand. A worktree made
  /// later at the same path is a new one and starts over.
  | Vanished of path: string
  /// A salvage ref was deleted because its worktree was gone and the base has everything on it, or it outlived its retention.
  | SalvageReaped of salvageRef: string * sha: string * why: string
  /// A rescue branch was made for a worktree.
  | Rescued of path: string * branch: string * sha: string

type LedgerLine = { At: DateTime; Event: LedgerEvent }

/// What a sweep saw a worktree doing.
[<RequireQualifiedAccess>]
type Activity =
  | InUse
  | Quiet

/// What a sweep saw of one worktree.
type Observed =
  { Path: string
    Branch: BranchLabel
    Tip: string
    Maker: Maker
    Activity: Activity }

/// What the ledger last recorded a worktree doing, and since when it has been quiet.
[<RequireQualifiedAccess>]
type RecordedActivity =
  | RecordedInUse
  | RecordedQuietSince of DateTime

[<RequireQualifiedAccess>]
type SalvageState =
  | NotSalvaged
  | SalvagedTo of branch: string

[<RequireQualifiedAccess>]
type Presence =
  | OnDisk
  | RemovedByHygiene
  /// Not on disk and not removed by hygiene: removed by hand, noticed by a later sweep.
  | GoneFromDisk

/// What the ledger knows of one worktree, read back from its lines.
type WorktreeRecord =
  { Path: string
    Branch: BranchLabel
    Tip: string
    Maker: Maker
    FirstSeen: DateTime
    Activity: RecordedActivity
    Salvage: SalvageState
    Presence: Presence }

/// The worktree a line is about, whatever the event.
let pathOf (event: LedgerEvent) : string =
  match event with
  | LedgerEvent.FirstSeen(path, _, _, _)
  | LedgerEvent.WentQuiet path
  | LedgerEvent.Resumed path
  | LedgerEvent.Salvaged(path, _, _, _)
  | LedgerEvent.Vanished path
  | LedgerEvent.Rescued(path, _, _) -> path
  | LedgerEvent.SalvageReaped(salvageRef, _, _) -> salvageRef
  | LedgerEvent.Removed removal -> removal.Target

module Maker =
  /// The harness's agent id, from a worktree directory name (`agent-a1b2c3...`) or a lock reason
  /// (`claude agent agent-a1b2c3... (pid 6613 start 13675)`).
  let ofName (text: string) : Maker =
    let marker = "agent-"
    match text.LastIndexOf(marker, StringComparison.Ordinal) with
    | -1 -> Maker.MakerUnrecorded
    | i ->
      let rest = text.Substring(i + marker.Length)
      let id = rest |> Seq.takeWhile Char.IsLetterOrDigit |> Seq.toArray |> String
      match id with
      | "" -> Maker.MakerUnrecorded
      | id -> Maker.HarnessAgent id

  let describe (maker: Maker) : string =
    match maker with
    | Maker.HarnessAgent id -> sprintf "agent %s" id
    | Maker.SessionOwner(name, _) -> sprintf "session owner %s" name
    | Maker.MakerUnrecorded -> "unknown maker"

/// Whether the automatic tidy may take a merged worktree yet, by what the ledger has seen of it.
[<RequireQualifiedAccess>]
type Clearance =
  | Cleared
  /// The ledger has not seen it quiet for the whole grace (or has not seen it at all): not before then.
  | NotYet of until: DateTime

module Ledger =
  let private apply (records: Map<string, WorktreeRecord>) (line: LedgerLine) : Map<string, WorktreeRecord> =
    let update (path: string) (f: WorktreeRecord -> WorktreeRecord) =
      match Map.tryFind path records with
      | Some r -> Map.add path (f r) records
      | None -> records
    match line.Event with
    | LedgerEvent.FirstSeen(path, branch, tip, maker) ->
      Map.add
        path
        { Path = path
          Branch = branch
          Tip = tip
          Maker = maker
          FirstSeen = line.At
          Activity = RecordedActivity.RecordedInUse
          Salvage = SalvageState.NotSalvaged
          Presence = Presence.OnDisk }
        records
    | LedgerEvent.WentQuiet path -> update path (fun r -> { r with Activity = RecordedActivity.RecordedQuietSince line.At })
    | LedgerEvent.Resumed path -> update path (fun r -> { r with Activity = RecordedActivity.RecordedInUse })
    | LedgerEvent.Salvaged(path, branch, _, _) -> update path (fun r -> { r with Salvage = SalvageState.SalvagedTo branch })
    | LedgerEvent.Rescued _
    | LedgerEvent.SalvageReaped _ -> records
    | LedgerEvent.Vanished path ->
      update path (fun r ->
        match r.Presence with
        | Presence.OnDisk -> { r with Presence = Presence.GoneFromDisk }
        | Presence.RemovedByHygiene
        | Presence.GoneFromDisk -> r)
    | LedgerEvent.Removed removal ->
      // Only a removed worktree ends a record; a removed branch or log is its own line and nothing more.
      match removal.Kind = LeftoverKind.AgentWorktree with
      | true -> update removal.Target (fun r -> { r with Presence = Presence.RemovedByHygiene })
      | false -> records

  /// The records the lines say, in the order they were written. A worktree seen again after it was removed starts over.
  let fold (lines: LedgerLine list) : Map<string, WorktreeRecord> = lines |> List.fold apply Map.empty

  /// The lines a sweep appends for what it saw. A worktree is written when it first appears and when its activity changes,
  /// never on every sweep, so the file grows with what happens and not with how often the daemon looks.
  let transitions (now: DateTime) (known: Map<string, WorktreeRecord>) (seen: Observed list) : LedgerLine list =
    seen
    |> List.collect (fun o ->
      let line event = { At = now; Event = event }
      let first = line (LedgerEvent.FirstSeen(o.Path, o.Branch, o.Tip, o.Maker))
      let startedQuiet =
        match o.Activity with
        | Activity.Quiet -> [ line (LedgerEvent.WentQuiet o.Path) ]
        | Activity.InUse -> []
      match Map.tryFind o.Path known with
      | None -> first :: startedQuiet
      | Some record ->
        match record.Presence with
        | Presence.RemovedByHygiene
        | Presence.GoneFromDisk -> first :: startedQuiet
        | Presence.OnDisk ->
          match record.Activity, o.Activity with
          | RecordedActivity.RecordedInUse, Activity.Quiet -> [ line (LedgerEvent.WentQuiet o.Path) ]
          | RecordedActivity.RecordedQuietSince _, Activity.InUse -> [ line (LedgerEvent.Resumed o.Path) ]
          | RecordedActivity.RecordedInUse, Activity.InUse -> []
          | RecordedActivity.RecordedQuietSince _, Activity.Quiet -> [])

  /// The lines for worktrees the ledger had on disk that are not among the ones just seen: somebody removed them by hand. Only
  /// meaningful when `seen` came from a listing git actually answered; the caller says so by calling it.
  let vanished (now: DateTime) (known: Map<string, WorktreeRecord>) (seen: Observed list) : LedgerLine list =
    let present = seen |> List.map (fun o -> o.Path) |> Set.ofList
    known
    |> Map.toList
    |> List.choose (fun (path, record) ->
      match record.Presence, Set.contains path present with
      | Presence.OnDisk, false -> Some { At = now; Event = LedgerEvent.Vanished path }
      | Presence.OnDisk, true
      | Presence.RemovedByHygiene, _
      | Presence.GoneFromDisk, _ -> None)

  /// When the worktree was last in use, as far as the ledger knows: now while it is in use, the moment the sweep first saw it
  /// quiet otherwise. That is never earlier than the truth, so a grace measured from it only waits longer.
  let lastActive (now: DateTime) (record: WorktreeRecord) : DateTime =
    match record.Activity with
    | RecordedActivity.RecordedInUse -> now
    | RecordedActivity.RecordedQuietSince since -> since

  /// Until when a finished worktree stays resumable: the grace after it was last active.
  let resumableUntil (grace: TimeSpan) (now: DateTime) (record: WorktreeRecord) : DateTime = lastActive now record + grace

  let isResumable (grace: TimeSpan) (now: DateTime) (record: WorktreeRecord) : bool =
    match record.Presence with
    | Presence.RemovedByHygiene
    | Presence.GoneFromDisk -> false
    | Presence.OnDisk -> now < resumableUntil grace now record

  /// Agent worktrees are the exception that needs a ledger entry (Will, 2026-11-07: they are why the pile exists). The
  /// automatic tidy takes a merged worktree only when the ledger has a record of it, has seen it quiet, and has seen it quiet
  /// for the whole grace. A worktree the ledger has never seen waits a full grace from now, however old git says it is.
  let clearance (grace: TimeSpan) (now: DateTime) (known: Map<string, WorktreeRecord>) (path: string) : Clearance =
    match Map.tryFind path known with
    | None -> Clearance.NotYet(now + grace)
    | Some record ->
      match record.Presence, record.Activity with
      | Presence.RemovedByHygiene, _
      | Presence.GoneFromDisk, _
      | Presence.OnDisk, RecordedActivity.RecordedInUse -> Clearance.NotYet(now + grace)
      | Presence.OnDisk, RecordedActivity.RecordedQuietSince since ->
        let until = since + grace
        match now >= until with
        | true -> Clearance.Cleared
        | false -> Clearance.NotYet until

  /// The leftovers as the automatic tidy sees them: a merged agent worktree the ledger has not cleared is shown as kept until
  /// the time it could be, so the plan the sweep makes, and says, is the plan it runs. Nothing else is changed, and nothing
  /// becomes more reclaimable than it was.
  let holdBack (grace: TimeSpan) (now: DateTime) (known: Map<string, WorktreeRecord>) (leftovers: Leftover list) : Leftover list =
    leftovers
    |> List.map (fun leftover ->
      match leftover with
      | Leftover.AgentWorktree(entry, detail) when Standing.isMerged entry.Standing ->
        match entry.Target with
        | Target.Directory path ->
          match clearance grace now known path with
          | Clearance.Cleared -> leftover
          | Clearance.NotYet until -> Leftover.AgentWorktree({ entry with Standing = Standing.WithinRetention until }, detail)
        | Target.File _
        | Target.GitBranch _
        | Target.RunningProcess _ -> leftover
      | other -> other)

  // ─── Lines as text ────────────────────────────────────────────────────

  let private profile = Json.standard

  /// One line of the file: JSON on one line, so an append is atomic enough and a torn last line costs one entry.
  let encode (line: LedgerLine) : string = Json.serialize profile line

  /// A line read back. A line that does not parse is `None` and is skipped by `decodeAll`, never an exception.
  let decode (text: string) : LedgerLine option =
    match Json.deserialize<LedgerLine> profile text with
    | Result.Ok line -> Some line
    | Result.Error _ -> None

  let decodeAll (lines: string seq) : LedgerLine list =
    lines |> Seq.filter (fun l -> not (String.IsNullOrWhiteSpace l)) |> Seq.choose decode |> Seq.toList
