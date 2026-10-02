namespace SageFs

open System
open System.IO

/// What a cohort is ABOUT — the key that decides whether two agents share a conductor seat, a claim
/// table and a landing queue, or are two unrelated cohorts that happen to talk to one daemon.
///
/// WHY THIS EXISTS. The daemon held exactly ONE cohort for its whole lifetime, keyed by one global
/// ledger (`~/.SageFs/cohort.ledger.db`), with no codebase dimension anywhere in `Cohort.fs` — there
/// is no `WorkingDirectory` or `RepoRoot` field on `CohortState` at all. Consequences, all observed
/// rather than predicted:
///   * An agent doing Nehemiah work in `/home/will/Work/nehemiah` and one working SageFs in
///     `/home/will/Work/SageFs` are the SAME cohort, so the first to ever join became the conductor
///     for both, and the conductor-only tools (`set_integration_ref`, `reassign_claim`,
///     `mint_member`, `revoke_member`) are globally exclusive across unrelated work.
///   * "First joiner becomes conductor" is true once per DAEMON, not once per repo, so a long-lived
///     daemon can only ever have one conductor for its entire life.
///   * Sub-agents that inherit one MCP connection are one member, which is correct (a claim follows
///     the connection), but a scope KEYED BY CONNECTION would make that worse rather than better.
///     Keying by repository is the dimension work actually contends over.
///
/// WHY PER-REPOSITORY IS THE DEFAULT, AND WHY IT IS A VALUE WITH AN OVERRIDE. Claims are about files
/// and projects, so the unit two agents actually contend over is the repository: two agents editing
/// one repo must see each other's claims and share a conductor, and two agents in unrelated repos
/// should not. That is also the least surprising default for someone who has never read this file —
/// it matches what every other tool in the product does with a working directory. But the mapping
/// from "which working directory" to "which scope" is a POLICY, so it is a parameter rather than a
/// hard-coded rule: a caller who wants one cohort per project, per solution, or one global cohort
/// for the whole machine can say so, and the coordinator tests it. The default and the alternatives
/// are the same type, so adding a strategy is a new case rather than a new mechanism.
///
/// NOT `[<RequireQualifiedAccess>]`: this DU is small, closed, and always used immediately beside
/// `Scope`, so qualifying every case as `CohortScope.Repository` would only add noise. The closed
/// string sets that DO carry that attribute are ones matched from a string far away.
type CohortScope =
  /// Every agent on this machine shares one cohort. The v1 behaviour, kept because it is what
  /// someone gets by default today, and because it is the right answer when the work is genuinely
  /// machine-wide.
  | Machine
  /// One cohort per repository root. The default.
  | Repository of rootPath: string
  /// One cohort per directory, however the caller chooses to name it. The escape hatch for
  /// "per solution", "per worktree" or any other unit the caller prefers.
  | Named of key: string

/// The scope module is `Scope` rather than `CohortScope` because F# will not let a type and a module
/// share a name, and `CohortScope.label` / `CohortScope.ofWorkingDirectory` read better qualified
/// than a bare `Scope.label` sitting next to unrelated `Scope` names in this assembly.
module Scope =

  /// How a working directory maps to the scope an agent lands in.
  type Strategy =
    /// One cohort per repository: the git root, so every worktree of one repo shares a cohort and
    /// two unrelated repos do not.
    | PerRepository
    /// One cohort per working directory. Two agents in two worktrees of ONE repo do not see each
    /// other, which is occasionally what you want and usually a mistake.
    | PerWorkingDirectory
    /// One cohort for the whole machine: v1's behaviour.
    | WholeMachine

  /// The default: per repository.
  let defaultStrategy : Strategy = PerRepository

  /// A short, stable, LOG-FILTERABLE name for a scope. This is what lands in the ledger and what a
  /// refusal quotes, so it must be readable in a log line and must not contain a newline.
  let label (scope: CohortScope) : string =
    match scope with
    | Machine -> "machine"
    | Repository root -> sprintf "repo:%s" root
    | Named key -> sprintf "named:%s" key

  /// The canonical form of a path as a scope key: no trailing separator, and never trimmed to
  /// nothing (a filesystem root must stay "/"). Two spellings of one directory MUST produce one
  /// key, or the collision this type exists to remove comes back wearing a different path.
  let trimEnd (p: string) : string =
    let t = p.TrimEnd([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |])
    if t = "" then p else t

  /// Turn a working directory into a repository root, tolerating a path that does not exist yet and
  /// a worktree (whose `.git` is a FILE, not a directory).
  ///
  /// Canonicalization matters here and is not optional: two spellings of one path must produce ONE
  /// scope, or an agent editing `src/Foo` and another editing `src/Foo/../Foo` would each believe
  /// they had the repository to themselves. `Path.GetFullPath` collapses `..` and `.` LEXICALLY,
  /// which is right — but a trailing separator survives it, and a directory that exists only after
  /// the walk cannot be probed by path string at all. So the directory is trimmed to its canonical
  /// form BEFORE the walk, and the walk starts from that. Measured, after trimming:
  ///   "/home/will/Work/SageFs"                 -> Repository "/home/will/Work/SageFs"
  ///   "/home/will/Work/SageFs/SageFs.Core"     -> Repository "/home/will/Work/SageFs"
  ///   "/home/will/Work/SageFs/SageFs.Core/../" -> Repository "/home/will/Work/SageFs"
  /// Without the trim, the third of those reported NO repo and fell back to a per-directory scope,
  /// which is precisely the collision this type exists to remove.
  let repositoryRootOf (workingDirectory: string) : string option =
    if String.IsNullOrWhiteSpace workingDirectory then
      None
    else
      let full : string option =
        try
          match trimEnd (Path.GetFullPath workingDirectory) with
          | p -> Some p
        with
        | :? ArgumentException
        | :? NotSupportedException
        | :? PathTooLongException -> None
      match full with
      | None -> None
      | Some dir ->
        // Walk up looking for a checkout root. `Checkout.classify` already knows main-checkout vs
        // worktree (a worktree's `.git` is a FILE); this only needs the ROOT, which for both is the
        // directory that CONTAINS `.git`, so the distinction does not change the answer here. BOTH
        // kinds are checked because a main checkout's `.git` is a DIRECTORY: testing only
        // `File.Exists` silently found no repo at all on a main checkout and fell through to the
        // per-directory fallback, which is how `repo:` scopes would never have been produced.
        let rec walk (d: string) : string option =
          match Path.GetFileName d with
          | null
          | ""
          | ".." -> None // reached the filesystem root: not a checkout
          | _ ->
            match File.Exists(Path.Combine(d, ".git")) || Directory.Exists(Path.Combine(d, ".git")) with
            | true -> Some d
            | false ->
              // `Path.GetDirectoryName` rather than `Directory.GetParent`: the latter takes a
              // DirectoryInfo, and `null` for a filesystem root. String in, string option out keeps
              // the walk a plain `string -> string option`.
              match Path.GetDirectoryName d with
              | null -> None
              | "" -> None
              | p when p = d -> None // a root whose parent is itself: nowhere left to walk
              | p -> walk p
        walk dir

  /// The scope a member working in `workingDirectory` belongs to, under `strategy`.
  ///
  /// A directory that is NOT a git checkout has no repository, so it falls back to the path itself
  /// under the per-repository strategy rather than silently joining the machine-wide cohort — a
  /// scratch directory is still someone's work, and collapsing it into `Machine` would reintroduce
  /// exactly the collision this type exists to remove.
  let ofWorkingDirectory (strategy: Strategy) (workingDirectory: string) : CohortScope =
    match strategy with
    | WholeMachine -> Machine
    | PerWorkingDirectory ->
      if String.IsNullOrWhiteSpace workingDirectory then
        Named "unknown"
      else
        Named (Path.GetFullPath workingDirectory |> trimEnd)
    | PerRepository ->
      match repositoryRootOf workingDirectory with
      | Some root -> Repository root
      | None ->
        if String.IsNullOrWhiteSpace workingDirectory then
          Named "unknown"
        else
          Named (Path.GetFullPath workingDirectory |> trimEnd)

  /// Two scopes are the same cohort only when they carry the same label. Comparing labels rather
  /// than cases means a path spelled two ways still collides, because `label` is built from the
  /// canonical form.
  let equal (a: CohortScope) (b: CohortScope) : bool = label a = label b