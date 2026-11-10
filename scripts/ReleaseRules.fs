/// The pure decisions behind the release scripts: bump-version.fsx, pre-push.fsx and local-gate.fsx.
///
/// Pure: strings and values in, values out. No IO and no clock in here, so SageFs.Tests compiles this same
/// file and tests it (ReleaseRulesTests.fs), and the scripts `#load` it and do the IO around it. It must stay
/// standalone: no `Timeouts`, no `SageFs.Json`, nothing past FSharp.Core and the BCL, because a script that
/// loads it has no other SageFs file to resolve them from (see StandaloneSourceTests).
module ReleaseRules

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

// ── versions ─────────────────────────────────────────────────────────────────

let private versionTag = Regex(@"<Version>([^<]+)</Version>", RegexOptions.Compiled)

/// The `<Version>` a Directory.Build.props text carries, if it carries one.
let versionIn (propsText: string) : string option =
  match versionTag.Match propsText with
  | m when m.Success -> Some m.Groups[1].Value
  | _ -> None

let private runs = Regex(@"\d+|\D+", RegexOptions.Compiled)

/// Digit runs compared as numbers (by length once leading zeros are gone, then by digits, so no run is ever
/// too big to compare), anything else ordinally.
let private compareRun (a: string) (b: string) : int =
  match Char.IsDigit a[0] && Char.IsDigit b[0] with
  | false -> String.CompareOrdinal(a, b)
  | true ->
    let a, b = a.TrimStart '0', b.TrimStart '0'
    match compare a.Length b.Length with
    | 0 -> String.CompareOrdinal(a, b)
    | byLength -> byLength

/// Version order the way `sort -V` gave it, which is what the pre-push hook used: run by run, numbers as
/// numbers, so 0.6.9 < 0.6.10, and a version that is a prefix of another is the lower one.
let compareVersions (a: string) (b: string) : int =
  let left = [ for m in runs.Matches a -> m.Value ]
  let right = [ for m in runs.Matches b -> m.Value ]
  let rec go (l: string list) (r: string list) =
    match l, r with
    | [], [] -> 0
    | [], _ -> -1
    | _, [] -> 1
    | x :: xs, y :: ys ->
      match compareRun x y with
      | 0 -> go xs ys
      | other -> sign other
  go left right

/// Whether `pushed` is a higher version than `published`. Equal is not higher: NuGet's --skip-duplicate turns a
/// repeated version into a release that publishes nothing and still goes green.
let raisesVersion (pushed: string) (published: string) : bool =
  pushed <> published && compareVersions pushed published > 0

/// Why a version could not be bumped.
type BumpError =
  | NotMajorMinorPatch of version: string

/// `major.minor.patch` with the patch one higher. Anything that is not three dot-separated parts with a numeric
/// last one is refused rather than guessed at.
let bumpPatch (version: string) : Result<string, BumpError> =
  match version.Split '.' with
  | [| major; minor; patch |] ->
    match Int32.TryParse patch with
    | true, n when n >= 0 -> Ok(sprintf "%s.%s.%d" major minor (n + 1))
    | _ -> Error(NotMajorMinorPatch version)
  | _ -> Error(NotMajorMinorPatch version)

/// Directory.Build.props with its `<Version>` moved from `current` to `next`, first occurrence only. Nothing
/// else in the file is touched.
let withPropsVersion (current: string) (next: string) (propsText: string) : string =
  let old = sprintf "<Version>%s</Version>" current
  match propsText.IndexOf(old, StringComparison.Ordinal) with
  | -1 -> propsText
  | at -> propsText.Substring(0, at) + sprintf "<Version>%s</Version>" next + propsText.Substring(at + old.Length)

let private packageVersion = Regex("\"version\": \"[^\"]*\"", RegexOptions.Compiled)

/// The vscode extension's package.json with its first `"version": "..."` set to `next`, the rest of the text
/// byte for byte as it was.
let withPackageVersion (next: string) (packageJson: string) : string =
  packageVersion.Replace(packageJson, sprintf "\"version\": \"%s\"" next, 1)

// ── the pre-push hook ────────────────────────────────────────────────────────

/// The all-zero sha git uses for "no such ref" in a push line.
let zeroSha = String('0', 40)

/// One line git feeds a pre-push hook: `<local ref> <local sha> <remote ref> <remote sha>`.
type PushedRef = { LocalRef: string; LocalSha: string; RemoteRef: string; RemoteSha: string }

let parsePushLine (line: string) : PushedRef option =
  match line.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries) with
  | [| localRef; localSha; remoteRef; remoteSha |] ->
    Some { LocalRef = localRef; LocalSha = localSha; RemoteRef = remoteRef; RemoteSha = remoteSha }
  | _ -> None

/// What the hook knows about the world, handed in so the decision stays pure.
type PushFacts =
  { /// The `<Version>` a commit carries; None when it cannot be read.
    VersionAt: string -> string option
    /// Whether the local gate recorded a pass for this commit.
    HasPass: string -> bool
    /// SAGEFS_SKIP_GATE=1.
    SkipGate: bool
    /// How many release commits the pushed range adds. One push to master is one release: the pipeline
    /// publishes the version at the tip, so a range carrying two releases loses the first one silently.
    ReleaseCountInRange: (string -> string -> int) }

/// What the hook says about one pushed ref.
type PushVerdict =
  | NotMaster
  | Deleting
  | RepeatsVersion of sha: string * pushed: string * published: string
  | StackedReleases of sha: string * count: int
  | AlreadyGated
  | SkippedGate of sha: string
  | NotGated of sha: string

/// Only a push to master is checked. A master push must raise the version (checked first, and a refusal ends
/// the ref there), must carry ONE release (the pipeline publishes the version at the tip, so a second release in
/// the same push never reaches NuGet), and must be a commit the gate has passed, unless the override is set.
let judgePush (facts: PushFacts) (push: PushedRef) : PushVerdict =
  match push.RemoteRef = "refs/heads/master", push.LocalSha = zeroSha with
  | false, _ -> NotMaster
  | _, true -> Deleting
  | true, false ->
    let repeated =
      match push.RemoteSha <> zeroSha, facts.VersionAt push.LocalSha, facts.VersionAt push.RemoteSha with
      | true, Some pushed, Some published when not (raisesVersion pushed published) -> Some(pushed, published)
      | _ -> None
    match repeated with
    | Some(pushed, published) -> RepeatsVersion(push.LocalSha, pushed, published)
    | None ->
      // The first push to master has no range to count: every commit in it is new, and only one release can
      // ever be in a tip that was just created.
      let stacked =
        match push.RemoteSha <> zeroSha with
        | true ->
          let count = facts.ReleaseCountInRange push.RemoteSha push.LocalSha
          match count > 1 with
          | true -> Some count
          | false -> None
        | false -> None
      match stacked with
      | Some count -> StackedReleases(push.LocalSha, count)
      | None ->
        match facts.HasPass push.LocalSha, facts.SkipGate with
        | true, _ -> AlreadyGated
        | false, true -> SkippedGate push.LocalSha
        | false, false -> NotGated push.LocalSha

/// A refusal fails the push; everything else lets it through.
let refuses (verdict: PushVerdict) : bool =
  match verdict with
  | RepeatsVersion _
  | StackedReleases _
  | NotGated _ -> true
  | NotMaster
  | Deleting
  | AlreadyGated
  | SkippedGate _ -> false

let shortSha (sha: string) : string = sha.Substring(0, min 8 sha.Length)

/// What the hook prints to stderr for a verdict, one string per line.
let messagesFor (verdict: PushVerdict) : string list =
  match verdict with
  | NotMaster
  | Deleting
  | AlreadyGated -> []
  | RepeatsVersion(sha, pushed, published) ->
    [ sprintf "pre-push: %s is version %s, and master already has %s." (shortSha sha) pushed published
      "          a repeated version publishes nothing (NuGet --skip-duplicate) and still goes green."
      "          run: dotnet fsi scripts/ship.fsx       (bump + gate + push)"
      "          or:  dotnet fsi scripts/bump-version.fsx && git commit -- Directory.Build.props sagefs-vscode/package.json" ]
  | StackedReleases(sha, count) ->
    [ sprintf "pre-push: this push carries %d releases (%s), and only the last one can ship." count (shortSha sha)
      "          the pipeline publishes the version at the tip of master, so the earlier ones never reach NuGet."
      "          a bump a failed ship left behind is one of them. A plain ship now reuses a single bump, and names"
      "          the commands that drop the extra ones when there are several:"
      "          run: dotnet fsi scripts/ship.fsx" ]
  | SkippedGate sha ->
    [ sprintf "pre-push: WARNING pushing ungated %s (SAGEFS_SKIP_GATE=1); the runner will gate it" (shortSha sha) ]
  | NotGated sha ->
    [ sprintf "pre-push: %s has not passed the local gate." (shortSha sha)
      "          run: dotnet fsi scripts/ship.fsx        (gate + push)"
      sprintf "          or:  dotnet fsi scripts/local-gate.fsx -- %s && git push" (shortSha sha) ]

// ── the release bump ─────────────────────────────────────────────────────────

/// The start of a release commit's subject. ship.fsx writes it, the pre-push hook counts it, and the bump plan
/// finds it: this is the one place the text lives.
let releaseSubjectPrefix = "chore: release v"

/// The subject of the commit that bumps the version to `version`.
let releaseSubject (version: string) : string = releaseSubjectPrefix + version

/// Whether a commit subject is a release commit's.
let isReleaseSubject (subject: string) : bool =
  subject.StartsWith(releaseSubjectPrefix, StringComparison.Ordinal)

/// The release commits in the output of `git log --format="%H %s"`, as full shas in the order git printed them.
/// A line with no subject, or a subject that only mentions the marker, is not one.
let releaseShasIn (log: string) : string list =
  log.Replace("\r\n", "\n").Split('\n')
  |> Array.choose (fun line ->
    match line.IndexOf ' ' with
    | -1 -> None
    | at when isReleaseSubject (line.Substring(at + 1)) -> Some(line.Substring(0, at))
    | _ -> None)
  |> List.ofArray

/// A version read from a commit's Directory.Build.props, or the fact that it could not be.
type VersionRead =
  | Read of version: string
  | Unreadable

/// What a plain `ship.fsx` does about the version, decided from the release commits still waiting to be pushed.
type BumpPlan =
  /// None waiting: bump, as ever.
  | BumpAnew
  /// One waiting, and it raises the published version: gate HEAD with it, add no second one.
  | ReuseBump of sha: string * version: string
  /// Several waiting: one push is one release, so refuse before any gate.
  | RefuseStacked of shas: string list
  /// One waiting, but it does not raise what is published, so it would publish nothing.
  | RefuseNotRaised of sha: string * version: string * published: string
  /// One waiting, and its Directory.Build.props carries no readable version.
  | RefuseUnreadable of sha: string

/// The plan for the release commits in `origin/master..HEAD`, oldest first. `published` is the version on
/// origin/master; when it cannot be read the waiting commit is reused, which is what the pre-push hook does too
/// (it refuses a repeat only when both versions are known).
let planBump (releasesOldestFirst: string list) (versionAt: string -> VersionRead) (published: VersionRead) : BumpPlan =
  match releasesOldestFirst with
  | [] -> BumpAnew
  | [ only ] ->
    match versionAt only, published with
    | Unreadable, _ -> RefuseUnreadable only
    | Read version, Read onMaster when not (raisesVersion version onMaster) -> RefuseNotRaised(only, version, onMaster)
    | Read version, _ -> ReuseBump(only, version)
  | several -> RefuseStacked several

/// The command that removes one commit from the branch and replays what came after it.
let dropCommitCommand (sha: string) : string = sprintf "git rebase --onto %s^ %s" sha sha

/// What ship prints about the plan, one string per line. A refusal ends with the commands that fix it. For a
/// stack it keeps the oldest bump and drops the others newest first, so each command names a sha the earlier
/// ones left alone, and no replay touches the version line a later bump had changed.
let bumpPlanMessages (plan: BumpPlan) : string list =
  match plan with
  | BumpAnew -> []
  | ReuseBump(sha, version) ->
    [ sprintf "ship: %s already carries the release bump to v%s and is not pushed yet." (shortSha sha) version
      "      gating HEAD with that bump in place, and not adding a second one." ]
  | RefuseStacked shas ->
    let oldest = List.head shas
    let extra = shas |> List.tail |> List.rev
    [ sprintf "ship: %d release commits are waiting to be pushed, and one push is one release: %s" shas.Length (shas |> List.map shortSha |> String.concat ", ")
      "      nothing was bumped or gated. The earlier ones never reach NuGet."
      sprintf "      keep the oldest, %s, and drop the others, newest first:" (shortSha oldest) ]
    @ (extra |> List.map (fun sha -> "        " + dropCommitCommand sha))
    @ [ "      then run dotnet fsi scripts/ship.fsx again; it gates HEAD with the bump that is left." ]
  | RefuseNotRaised(sha, version, published) ->
    [ sprintf "ship: %s is an unpushed bump to v%s, and origin/master is already at v%s." (shortSha sha) version published
      "      a repeated version publishes nothing (NuGet --skip-duplicate) and still goes green. Nothing was gated."
      sprintf "      drop it: %s" (dropCommitCommand sha)
      "      then run dotnet fsi scripts/ship.fsx again; it bumps from the version on master." ]
  | RefuseUnreadable sha ->
    [ sprintf "ship: %s is a release commit but its Directory.Build.props has no readable <Version>. Nothing was gated." (shortSha sha)
      sprintf "      drop it: %s" (dropCommitCommand sha)
      "      then run dotnet fsi scripts/ship.fsx again." ]

// ── the local gate ───────────────────────────────────────────────────────────

/// What the gate does when a tier goes red. A release stops at the first red and cancels the rest, which is the quick answer
/// to "can this ship". `--keep-going` lets every tier run, so one run lists every red instead of one per run, which is what
/// fixing a pile of failures needs. It gates; it never bumps or pushes (that is `ship`).
type FailureMode =
  | StopAtFirstRed
  | KeepGoing

/// What `local-gate.fsx` was asked to do.
type GateRequest =
  | Gate of commit: string * force: bool * mode: FailureMode
  | Promote of commit: string * into: string
  | Usage of string

/// The arguments the script was started with, with fsi's own `--` already dropped. A commit defaults to HEAD, and
/// `--keep-going` is read from anywhere.
let parseGateArgs (argv: string list) : GateRequest =
  let args = argv |> List.filter (fun a -> a <> "--keep-going")
  let mode =
    match args.Length = argv.Length with
    | true -> StopAtFirstRed
    | false -> KeepGoing
  match args with
  | "--promote" :: commit :: into :: _ -> Promote(commit, into)
  | "--promote" :: _ -> Usage "--promote needs a commit and a directory"
  | "--force" :: rest -> Gate((match rest with c :: _ -> c | [] -> "HEAD"), true, mode)
  | rest -> Gate((match rest with c :: _ -> c | [] -> "HEAD"), false, mode)

/// Whether the pipeline may take a green tier's record from an earlier run of the same commit.
type PassReuse =
  | AllowReuse
  | ForceFresh

/// `--fresh` anywhere in `argv` (ship's own arguments), and the arguments without it, in their order.
let splitFresh (argv: string list) : PassReuse * string list =
  let kept = argv |> List.filter (fun a -> a <> "--fresh")
  match kept.Length = argv.Length with
  | true -> AllowReuse, kept
  | false -> ForceFresh, kept

/// `--force` re-gates a commit that already passed, so it must not be answered with records of that pass.
let freshnessOfForce (force: bool) : PassReuse =
  match force with
  | true -> ForceFresh
  | false -> AllowReuse

/// The arguments `dotnet` gets to run the pipeline: every stage, the release bundle, and `--fresh` when asked.
let pipelineArgs (reuse: PassReuse) : string list =
  [ "fsi"; "ci-pipeline.fsx"; "--"; "ci"; "release" ]
  @ (match reuse with
     | AllowReuse -> []
     | ForceFresh -> [ "--fresh" ])

/// What the pipeline reads for `FailureMode`: `SAGEFS_KEEP_GOING=1` (ci-pipeline.fsx). It is the environment and not an
/// argument because the pipeline's command-line parser declares its options and refuses `--keep-going` after `ci release`.
let failureModeEnvironment (mode: FailureMode) : (string * string) list =
  match mode with
  | StopAtFirstRed -> []
  | KeepGoing -> [ "SAGEFS_KEEP_GOING", "1" ]

/// Where the pipeline keeps its tier pass records: beside the gate's passes, outside every checkout.
let tierPassesDirectory (gateHome: string) : string = gateHome.TrimEnd('/') + "/tier-passes"

/// `3m7s`, the way a gate reports how long it took (minutes and seconds, no padding).
let formatElapsed (elapsed: TimeSpan) : string =
  let total = int elapsed.TotalSeconds
  sprintf "%dm%ds" (total / 60) (total % 60)

/// What the `ok` file of a pass records: when, and how long it took.
let passRecord (isoNow: string) (elapsed: TimeSpan) : string =
  sprintf "%s in %s" isoNow (formatElapsed elapsed)

/// The name of a persistent clean checkout for a source repo: one per repo path, so an agent's worktree gets a
/// checkout of its own. It is `checkout-` plus the first eight hex of the SHA-1 of the path, which is what the
/// bash gate named them, so the warm checkouts already on disk keep being reused.
let checkoutName (repoPath: string) : string =
  let hash = SHA1.HashData(Encoding.UTF8.GetBytes repoPath)
  "checkout-" + (Convert.ToHexString hash).ToLowerInvariant().Substring(0, 8)

/// The git commands that bring the gate's persistent checkout to a commit. The checkout is the gate's own and nobody's
/// work lives in it, so the checkout is forced: a plain one refused when the index had lost files its HEAD still had
/// (the plan files showed as staged deletions and untracked at once) and the gate died before it ran anything. The tidy
/// steps follow it and may fail without stopping the run; the checkout may not.
type CheckoutSync = { Checkout: string list; Tidy: string list list }

let syncCheckoutSteps (sha: string) : CheckoutSync =
  { Checkout = [ "checkout"; "--detach"; "-q"; "-f"; sha ]
    Tidy = [ [ "reset"; "--hard"; "-q" ]; [ "clean"; "-fdq" ] ] }

/// Extra environment for the gate's pipeline process and what it starts, and for nothing else: `SAGEFS_PIPELINE_ENV` holds
/// `NAME=value` entries separated by semicolons (a value may hold an equals sign). It exists so a diagnostic such as a
/// preloaded descriptor canary can ride one gate run without reaching the daemon that `install-local` restarts from the same
/// shell. A name is an upper-case identifier, and an entry that is not `NAME=value` is refused by name, so a typo cannot run an
/// unmeasured gate that looks like a measured one.
let parsePipelineEnvironment (text: string) : Result<(string * string) list, string> =
  let validName = Regex(@"^[A-Z_][A-Z0-9_]*$")
  let entry (raw: string) : Result<string * string, string> =
    match raw.IndexOf '=' with
    | at when at > 0 && validName.IsMatch(raw.Substring(0, at)) -> Ok(raw.Substring(0, at), raw.Substring(at + 1))
    | _ -> Error(sprintf "SAGEFS_PIPELINE_ENV entry '%s' is not NAME=value with an upper-case NAME" raw)
  text.Split([| ';' |], StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
  |> Array.toList
  |> List.fold
    (fun acc raw ->
      match acc, entry raw with
      | Ok parsed, Ok one -> Ok(parsed @ [ one ])
      | (Error _ as failed), _ -> failed
      | Ok _, Error message -> Error message)
    (Ok [])

/// The three-line record that says who a checkout is for (repo, pid, when), which the reaper reads.
let ownerRecord (repo: string) (pid: int) (isoNow: string) : string =
  sprintf "%s\n%d\n%s\n" repo pid isoNow

let private ansi = Regex(@"\x1b\[[0-9;]*[A-Za-z]", RegexOptions.Compiled)

/// The text with terminal colour and cursor sequences removed.
let stripAnsi (text: string) : string = ansi.Replace(text, "")

let private progress =
  Regex(@"^── (tier|background)|^FAILURE |^cleanup:|^pass records:|STAGE #|^Tiers:|^Expected wall|trust report:|^\| |error", RegexOptions.Compiled)

/// The pipeline lines worth showing on the console while it runs: stage boundaries, one line per finished tier,
/// the trust table, failures. Everything else goes to the log only.
let isProgressLine (rawLine: string) : bool = progress.IsMatch rawLine

let private failureSummary =
  Regex(@"^\s*(──|::error)|^FAILURE |STAGE .* (failed|finished)|error ", RegexOptions.Compiled)

/// The lines a failed gate repeats at the end, so the cause is on screen without opening the log.
let isFailureSummaryLine (rawLine: string) : bool = failureSummary.IsMatch rawLine

/// What asking a daemon for a build lease got back.
type LeaseReply =
  | LeaseGranted of leaseId: string
  | LeaseWait
  | LeaseOther

/// Reads the daemon's reply to `POST /api/lease/request`. Anything that is not a clear `granted` or `wait`
/// (a refusal, an error body, a daemon that is not there) is `LeaseOther`: not this script's problem to solve.
let parseLeaseReply (body: string) : LeaseReply =
  try
    use doc = System.Text.Json.JsonDocument.Parse body
    let root = doc.RootElement
    let text (name: string) =
      match root.TryGetProperty name with
      | true, v when v.ValueKind = System.Text.Json.JsonValueKind.String -> v.GetString()
      | _ -> ""
    match text "decision" with
    | "granted" -> LeaseGranted(text "leaseId")
    | "wait" -> LeaseWait
    | _ -> LeaseOther
  with _ -> LeaseOther

// ── the failure report of a pass ──────────────────────────────────────────────

/// The failure report a pass leaves in the checkout (`test-results/failure-report.txt`), as the gate found it on disk.
type ReportFile =
  | NoReportFile
  | ReportWrittenAt of writtenAt: DateTimeOffset * text: string

/// Which report, if any, belongs to the pass that just ended.
type ReportChoice =
  | ShowReport of text: string
  | StaleReport of writtenAt: DateTimeOffset
  | NoReport

/// The report to print for the pass that started at `passStartedAt`: one written during this pass, never one that was
/// already in the checkout. The gate reuses one checkout per repository and `git clean` keeps ignored files, and the
/// pipeline only writes the report in its last stage, so a pass that was cancelled before that stage (the time cap)
/// left the PREVIOUS pass's report in place, and the ship printed it as this pass's first failure (2026-10-18).
let chooseFailureReport (passStartedAt: DateTimeOffset) (file: ReportFile) : ReportChoice =
  match file with
  | NoReportFile -> NoReport
  | ReportWrittenAt (writtenAt, text) ->
    match writtenAt >= passStartedAt with
    | true -> ShowReport text
    | false -> StaleReport writtenAt

/// Exit codes that mean the pass was stopped, not that a case failed: `timeout(1)` (124), SIGINT (130), SIGTERM (143).
let cancelledExitCodes = [ 124; 130; 143 ]

/// Whether a pass ended because something stopped it. Every tier that finished before then is still green.
let endedByCancel (exitCode: int) : bool = List.contains exitCode cancelledExitCodes

/// What the gate prints about a pass's failure after the pipeline's own output: the report when this pass wrote one,
/// otherwise a plain statement that it wrote none, saying why that is not a mystery.
let failureNotice (exitCode: int) (choice: ReportChoice) : string list =
  match choice with
  | ShowReport text -> [ text.TrimEnd() ]
  | StaleReport writtenAt ->
    let older =
      sprintf "this pass wrote no failure report. An older one from %s was left in the checkout and is NOT shown: it describes another pass." (writtenAt.ToString "yyyy-MM-dd HH:mm:ss zzz")
    match endedByCancel exitCode with
    | true -> [ older; sprintf "exit %d means the pass was cancelled or timed out, not that a case failed. The tiers that finished before then are green; rerun to reuse them." exitCode ]
    | false -> [ older; "read the pass's own `tier ... exit=` lines and its log." ]
  | NoReport ->
    match endedByCancel exitCode with
    | true -> [ sprintf "exit %d means the pass was cancelled or timed out, not that a case failed. No failure report was written; the tiers that finished before then are green." exitCode ]
    | false -> [ "no failure report was written by this pass; the log has the reason." ]
