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
    SkipGate: bool }

/// What the hook says about one pushed ref.
type PushVerdict =
  | NotMaster
  | Deleting
  | RepeatsVersion of sha: string * pushed: string * published: string
  | AlreadyGated
  | SkippedGate of sha: string
  | NotGated of sha: string

/// Only a push to master is checked. A master push must raise the version (checked first, and a refusal ends
/// the ref there), and must be a commit the gate has passed, unless the override is set.
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
      match facts.HasPass push.LocalSha, facts.SkipGate with
      | true, _ -> AlreadyGated
      | false, true -> SkippedGate push.LocalSha
      | false, false -> NotGated push.LocalSha

/// A refusal fails the push; everything else lets it through.
let refuses (verdict: PushVerdict) : bool =
  match verdict with
  | RepeatsVersion _
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
  | SkippedGate sha ->
    [ sprintf "pre-push: WARNING pushing ungated %s (SAGEFS_SKIP_GATE=1); the runner will gate it" (shortSha sha) ]
  | NotGated sha ->
    [ sprintf "pre-push: %s has not passed the local gate." (shortSha sha)
      "          run: dotnet fsi scripts/ship.fsx        (gate + push)"
      sprintf "          or:  dotnet fsi scripts/local-gate.fsx -- %s && git push" (shortSha sha) ]

// ── the local gate ───────────────────────────────────────────────────────────

/// What `local-gate.fsx` was asked to do.
type GateRequest =
  | Gate of commit: string * force: bool
  | Promote of commit: string * into: string
  | Usage of string

/// The arguments the script was started with, with fsi's own `--` already dropped. A commit defaults to HEAD.
let parseGateArgs (argv: string list) : GateRequest =
  match argv with
  | "--promote" :: commit :: into :: _ -> Promote(commit, into)
  | "--promote" :: _ -> Usage "--promote needs a commit and a directory"
  | "--force" :: rest -> Gate((match rest with c :: _ -> c | [] -> "HEAD"), true)
  | rest -> Gate((match rest with c :: _ -> c | [] -> "HEAD"), false)

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

/// The three-line record that says who a checkout is for (repo, pid, when), which the reaper reads.
let ownerRecord (repo: string) (pid: int) (isoNow: string) : string =
  sprintf "%s\n%d\n%s\n" repo pid isoNow

let private ansi = Regex(@"\x1b\[[0-9;]*[A-Za-z]", RegexOptions.Compiled)

/// The text with terminal colour and cursor sequences removed.
let stripAnsi (text: string) : string = ansi.Replace(text, "")

let private progress =
  Regex(@"^── tier|STAGE #|^Tiers:|^Expected wall|trust report:|^\| |error", RegexOptions.Compiled)

/// The pipeline lines worth showing on the console while it runs: stage boundaries, one line per finished tier,
/// the trust table, failures. Everything else goes to the log only.
let isProgressLine (rawLine: string) : bool = progress.IsMatch rawLine

let private failureSummary =
  Regex(@"^\s*(──|::error)|STAGE .* (failed|finished)|error ", RegexOptions.Compiled)

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
