/// The decision behind the sagefs-repl-guard Claude Code hook.
///
/// Pure: a Bash command string plus what the hook found out about the world
/// goes in, Allow or Deny comes out. No IO in here, so SageFs.Tests compiles
/// this same file and tests it, and Hook.fs (the compiled sagefs-repl-guard program) runs it.
/// No dependencies past FSharp.Core, because the hook has to start fast.
module SageFs.AgentHooks.ReplGuard

/// Whether the hook's working directory has an .fsproj, .slnx or .sln at or above it.
type ProjectScope =
  | FSharpWorkspace
  | NotFSharpWorkspace

/// Whether a SageFs daemon answered /health on localhost.
type DaemonProbe =
  | DaemonAnswering
  | DaemonNotAnswering

/// What asking the daemon for an expensive-work lease got back, for a
/// FINAL-GATE-declared command. Only asked when the command IS final-gate
/// declared — a plain (undeclared) slow verb is denied on the "use the
/// REPL" grounds alone and never needs a lease check. Kept as plain
/// primitives (no dependency on SageFs.ExpensiveWorkLease's real DU)
/// because this file has to stay dependency-free past FSharp.Core so the
/// hook script loads fast.
type LeaseProbe =
  | LeaseGranted
  | LeaseRefused of reason: string
  | LeaseMustWait of retryAfterSeconds: float * reason: string

/// What the daemon's sessions say about the command's cwd. A session covers cwd
/// when its working directory is cwd or an ancestor of it.
///   - `ReadySessionCoversCwd`: a Ready session covers it, so there is a REPL to go back to.
///   - `FaultedSessionCoversCwd`: none is Ready and one is Faulted. `reason` is that
///     session's own `faultReason` from `/health`, the daemon's account of what broke and
///     (for a framework with no build) the exact command that fixes it.
///   - `NoReadySessionCoversCwd`: no session at all for this directory, or the one there
///     isn't Ready yet (still starting). Either way there is no REPL here right now.
type SessionProbe =
  | ReadySessionCoversCwd
  | FaultedSessionCoversCwd of reason: string
  | NoReadySessionCoversCwd

/// `Lease` is `None` whenever it was never asked for (the command isn't a
/// declared final gate, or the daemon didn't answer) — `Context` cannot
/// represent "asked and got nothing back," only "didn't ask." `Session`
/// follows the same shape: `None` means "never probed" (not an F# workspace,
/// or the daemon didn't answer), not "probed and found nothing."
type Context = { Project: ProjectScope; Daemon: DaemonProbe; Session: SessionProbe option; Lease: LeaseProbe option }

/// The minimum a session summary the script fetches from `GET /health`'s
/// `sessionStates` needs to answer "is there a Ready REPL for this cwd" —
/// kept as plain primitives (not SageFs.Core's own session/status types) for
/// the same dependency-free reason as `LeaseProbe` above. `FaultReason` is the
/// session's own `faultReason` as the wire carries it, "" when it carries none.
type SessionSummary = { WorkingDirectory: string; Status: string; FaultReason: string }

[<Literal>]
let private readyStatusLabel = "Ready"

/// Normalizes a path for comparison: forward slashes, no trailing
/// separator — so `/repo/`, `/repo` and `C:\repo\` all compare the same way.
let private normalizePath (path: string) =
  let p = (path: string).Replace('\\', '/').TrimEnd('/')
  if p = "" then "/" else p

/// Whether `ancestorOrSelf` is `target` itself, or a real path ancestor of
/// it — never just a shared string prefix (`/repo` must not "contain"
/// `/repo-other`). This is what turns "a session exists somewhere" into "a
/// session exists for a directory I could actually be standing in."
let directoryContains (ancestorOrSelf: string) (target: string) : bool =
  let a = normalizePath ancestorOrSelf
  let t = normalizePath target
  a = t || t.StartsWith(a + "/")

[<Literal>]
let faultedStatusLabel = "Faulted"

/// What the fault note says when the daemon's session carries no reason of its own, so a Faulted session is
/// never reported with an empty string.
[<Literal>]
let NoFaultReasonRecorded = "SageFs recorded no reason for this fault. The daemon log has the details."

let hasStatus (label: string) (s: SessionSummary) =
  System.String.Equals(s.Status, label, System.StringComparison.OrdinalIgnoreCase)

/// The whole "is there a REPL for this cwd" question. A Ready session whose working directory covers `cwd` is a
/// REPL, whatever else is there. Failing that, a Faulted session that covers it is reported with its own
/// reason (the one with the deepest working directory, the one the agent is standing in). Anything else is
/// `NoReadySessionCoversCwd`. Status comparison is case-insensitive — the wire label's exact casing isn't
/// this file's contract to police.
let sessionProbe (cwd: string) (sessions: SessionSummary list) : SessionProbe =
  let covering = sessions |> List.filter (fun s -> directoryContains s.WorkingDirectory cwd)
  match covering |> List.exists (hasStatus readyStatusLabel) with
  | true -> ReadySessionCoversCwd
  | false ->
    match covering |> List.filter (hasStatus faultedStatusLabel) |> List.sortByDescending (fun s -> (normalizePath s.WorkingDirectory).Length) with
    | nearest :: _ ->
      match System.String.IsNullOrWhiteSpace nearest.FaultReason with
      | true -> FaultedSessionCoversCwd NoFaultReasonRecorded
      | false -> FaultedSessionCoversCwd nearest.FaultReason
    | [] -> NoReadySessionCoversCwd

/// The dotnet verbs that belong in the final gate, not the inner loop.
type SlowVerb =
  | Build
  | Test
  | Run
  | Fsi

module SlowVerb =
  let toToken =
    function
    | Build -> "build"
    | Test -> "test"
    | Run -> "run"
    | Fsi -> "fsi"

  let all = [ Build; Test; Run; Fsi ]

  let tryParse (token: string) =
    all |> List.tryFind (fun v -> toToken v = token)

/// What a command asks dotnet to do, as far as the guard cares.
type CommandClass =
  /// No slow-loop dotnet verb anywhere in it: pack, tool, --version, git, ls...
  | NoSlowLoop
  /// A slow-loop verb that said SAGEFS_FINAL_GATE=1 up front.
  | DeclaredFinalGate of SlowVerb
  /// A slow-loop verb with no final-gate declaration.
  | SlowLoop of SlowVerb

type Decision =
  | Allow
  /// Let it run, and put the REPL reminder in front of the agent as `additionalContext`.
  /// Only `SAGEFS_REPL_GUARD=advise` produces this.
  | AllowWithAdvice of reminder: string
  | Deny of reason: string

[<Literal>]
let FinalGateVariable = "SAGEFS_FINAL_GATE"

let private finalGateAssignment = FinalGateVariable + "=1"

/// How the guard treats a command it would deny on "use the REPL" grounds.
type GuardMode =
  /// Deny it. The default.
  | Enforcing
  /// Let it run and remind the agent. For a repository where the REPL is a recommendation.
  | Advising

module GuardMode =
  /// The environment variable that picks the mode.
  [<Literal>]
  let Variable = "SAGEFS_REPL_GUARD"

  let toToken =
    function
    | Enforcing -> "enforce"
    | Advising -> "advise"

  let all = [ Enforcing; Advising ]

  let tryParse (token: string) =
    all |> List.tryFind (fun m -> System.String.Equals(toToken m, token.Trim(), System.StringComparison.OrdinalIgnoreCase))

/// Command patterns a repository says always pass. A pattern is the words of a
/// dotnet command; it matches a command whose words START with them, whole words only.
/// Each pattern is stored as its words.
type Allowlist = { Patterns: string list list }

module Allowlist =
  let none = { Patterns = [] }

/// What the guard found at `<git root>/.SageFs/repl-guard.json`.
type RepoConfig =
  | NoRepoConfig
  | RepoConfigLoaded of Allowlist
  /// The file is there and the guard could not use it. `Problem` says what is wrong with it.
  | RepoConfigMalformed of path: string * problem: string

/// Where the repository's config lives, relative to its git root.
[<Literal>]
let ConfigDirectory = ".SageFs"

[<Literal>]
let ConfigFileName = "repl-guard.json"

/// Everything the guard was told about how strict to be, in one place.
/// `Problems` are things wrong with that configuration; the guard keeps going on the
/// stricter reading and puts these in the message it prints.
type Policy = { Mode: GuardMode; Allow: Allowlist; Problems: string list }

module Policy =
  let strict = { Mode = Enforcing; Allow = Allowlist.none; Problems = [] }

  /// The policy for the mode the environment asked for (`rawMode`, "" when unset) and what the repository's
  /// config file turned out to be. Anything it cannot use falls back to the stricter reading and is
  /// recorded in `Problems`, so the message can say what is wrong.
  let build (rawMode: string) (config: RepoConfig) : Policy =
    let mode, modeProblems =
      match System.String.IsNullOrWhiteSpace rawMode with
      | true -> Enforcing, []
      | false ->
        match GuardMode.tryParse rawMode with
        | Some m -> m, []
        | None ->
          let known = GuardMode.all |> List.map GuardMode.toToken |> String.concat ", "
          Enforcing, [ sprintf "%s=%s is not a mode (%s), so the guard is enforcing." GuardMode.Variable rawMode known ]
    let allow, configProblems =
      match config with
      | NoRepoConfig -> Allowlist.none, []
      | RepoConfigLoaded allow -> allow, []
      | RepoConfigMalformed(path, problem) ->
        Allowlist.none, [ sprintf "%s could not be used, so its allowlist is ignored and the strict rules apply: %s" path problem ]
    { Mode = mode; Allow = allow; Problems = modeProblems @ configProblems }

/// Split a command into pipeline segments of words. Quotes group words and are
/// stripped, and separators inside quotes don't split. This is a small shell
/// lexer, good enough to find a command word, not a full POSIX parser.
let segments (command: string) : string list list =
  let segs = ResizeArray<string list>()
  let words = ResizeArray<string>()
  let word = System.Text.StringBuilder()
  let mutable inWord = false
  let endWord () =
    if inWord then
      words.Add(word.ToString())
      word.Clear() |> ignore
      inWord <- false
  let endSegment () =
    endWord ()
    if words.Count > 0 then
      segs.Add(List.ofSeq words)
      words.Clear()
  let mutable quote : char option = None
  let n = command.Length
  let mutable i = 0
  while i < n do
    let c = command[i]
    match quote with
    | Some q when c = q ->
      quote <- None
      i <- i + 1
    | Some '"' when c = '\\' && i + 1 < n ->
      word.Append(command[i + 1]) |> ignore
      i <- i + 2
    | Some _ ->
      word.Append c |> ignore
      i <- i + 1
    | None ->
      match c with
      | '\'' | '"' ->
        quote <- Some c
        inWord <- true
        i <- i + 1
      | '\\' when i + 1 < n ->
        word.Append(command[i + 1]) |> ignore
        inWord <- true
        i <- i + 2
      | ';' | '|' | '&' | '\n' | '(' | ')' ->
        endSegment ()
        i <- i + 1
      | c when System.Char.IsWhiteSpace c ->
        endWord ()
        i <- i + 1
      | c ->
        word.Append c |> ignore
        inWord <- true
        i <- i + 1
  endSegment ()
  List.ofSeq segs

let private isAssignment (word: string) =
  let eq = word.IndexOf '='
  eq > 0
  && (System.Char.IsLetter word[0] || word[0] = '_')
  && word.Substring(0, eq) |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_')

/// Commands that run another command after their own options, so a dotnet
/// word after them is still the command being run.
let private wrappers =
  set [ "env"; "time"; "timeout"; "nice"; "nohup"; "command"; "exec"; "sudo"; "systemd-run" ]

let private isDotnet (word: string) = word = "dotnet" || word.EndsWith "/dotnet"

/// A word that can sit in front of the real command word: an env assignment,
/// a wrapper, a wrapper's option, or a bare number (timeout 600).
let private isPrefixWord (word: string) =
  isAssignment word
  || wrappers.Contains word
  || word.StartsWith "-"
  || (word.Length > 0 && System.Char.IsDigit word[0])

/// What one segment of a command is, as far as the guard cares.
type SegmentKind =
  | NotSlow
  /// A slow-loop verb the repository's allowlist says always passes.
  | Listed
  /// A slow-loop verb. `Declared` is true when it said it is the final gate, or is a Release build.
  | Slow of SlowVerb * declared: bool

/// Whether `words` begin with all of `pattern`'s words, word for word. A longer word is a different word.
let startsWith (pattern: string list) (words: string list) =
  words.Length >= pattern.Length && List.forall2 (=) pattern (List.truncate pattern.Length words)

let isRelease (value: string) =
  System.String.Equals(value.Trim(), "Release", System.StringComparison.OrdinalIgnoreCase)

let configurationPrefixes = [ "-c:"; "-c="; "--configuration:"; "--configuration=" ]

/// Whether the last `-c` / `--configuration` among a build's arguments asks for Release. Arguments after a
/// bare `--` belong to the program being run, not to dotnet.
let asksForRelease (args: string list) : bool =
  let rec go release args =
    match args with
    | []
    | "--" :: _ -> release
    | ("-c" | "--configuration") :: value :: rest -> go (isRelease value) rest
    | word :: rest ->
      match configurationPrefixes |> List.tryFind (fun p -> word.StartsWith(p, System.StringComparison.Ordinal)) with
      | Some prefix -> go (isRelease (word.Substring prefix.Length)) rest
      | None -> go release rest
  go false args

/// The dotnet command in a segment, from the dotnet word on (its path normalized to `dotnet`), and whether the
/// prefix in front of it declared the final gate.
let dotnetCommand (words: string list) : (string list * bool) option =
  let prefix = words |> List.takeWhile (fun w -> not (isDotnet w) && isPrefixWord w)
  match List.skip prefix.Length words with
  | dotnet :: rest when isDotnet dotnet -> Some("dotnet" :: rest, prefix |> List.contains finalGateAssignment)
  | _ -> None

let kindOf (allow: Allowlist) (words: string list) : SegmentKind =
  match dotnetCommand words with
  | Some(command, declared) ->
    match command with
    | _ :: verbWord :: args ->
      match SlowVerb.tryParse verbWord with
      | None -> NotSlow
      | Some verb ->
        match allow.Patterns |> List.exists (fun pattern -> startsWith pattern command) with
        | true -> Listed
        | false -> Slow(verb, declared || (verb = Build && asksForRelease args))
    | _ -> NotSlow
  | None -> NotSlow

let private isFinalGateExport (words: string list) =
  match words with
  | "export" :: rest -> rest |> List.contains finalGateAssignment
  | _ -> false

/// The first slow-loop dotnet call in the command, if any, leaving out the ones `allow` lists. An earlier
/// `export SAGEFS_FINAL_GATE=1` segment counts as a declaration for every segment after it. A Release build
/// counts as declared: it checks things the REPL cannot, so sending it back costs a retry and nothing else.
let classifyWith (allow: Allowlist) (command: string) : CommandClass =
  let rec go exported segs =
    match segs with
    | [] -> NoSlowLoop
    | seg :: rest ->
      match kindOf allow seg with
      | Slow(verb, declared) when declared || exported ->
        match go exported rest with
        | SlowLoop v -> SlowLoop v
        | _ -> DeclaredFinalGate verb
      | Slow(verb, _) -> SlowLoop verb
      | Listed
      | NotSlow -> go (exported || isFinalGateExport seg) rest
  go false (segments command)

/// `classifyWith` with no allowlist.
let classify (command: string) : CommandClass = classifyWith Allowlist.none command

/// The deny reason for an UNDECLARED slow verb: BROKEN is never the issue
/// here — this is "use the REPL," the loop in two lines, then the way out.
let denyReason (verb: SlowVerb) =
  let v = SlowVerb.toToken verb
  String.concat "\n" [
    sprintf "SageFs is running, so `dotnet %s` is off the inner loop. Loop: send_fsharp_code for RED then GREEN, persist to the .fs file, hard_reset_fsi_session rebuild=true, re-verify in the session, commit." v
    "dotnet build/test/run/fsi is for the final gate, run once when you're done, and for the one build before create_session (a worktree gets its own session)."
    sprintf "If this is one of those, rerun it as `%s dotnet %s ...`. If the REPL is fighting you, report the exact error first, then use that escape hatch for that one step only." finalGateAssignment v
  ]

/// The deny reason for a DECLARED final gate that a lease request refused
/// or asked to wait for: SageFs is BUSY here, not broken, and the message
/// says so explicitly — the whole point is an agent must never read this as
/// "the REPL is fighting me, fall back to dotnet" (that IS the fallback,
/// and it is what a busy daemon needs the LEAST). Waiting the named time is
/// the only correct move; a build/test/run that shells out anyway spends
/// exactly the memory SageFs is trying to reclaim, outside its own
/// accounting.
let busyDenyReason (verb: SlowVerb) (leaseProbe: LeaseProbe) =
  let v = SlowVerb.toToken verb
  let whatItSaid =
    match leaseProbe with
    | LeaseGranted -> "" // unreachable from decide's Deny path
    | LeaseRefused reason -> sprintf "SageFs refused the lease: %s" reason
    | LeaseMustWait(retryAfterSeconds, reason) -> sprintf "SageFs said wait %.0fs: %s" retryAfterSeconds reason
  String.concat "\n" [
    sprintf "SageFs is BUSY, not broken — `dotnet %s` (a declared final gate) costs real memory SageFs is trying to manage, even though SageFs itself never ran it." v
    whatItSaid
    "This is a WAIT, not a bug to route around: sleep the named time and rerun the identical command. Shelling out anyway spends the exact memory SageFs is trying to reclaim, invisibly to it — that is how five agents starved one daemon in one night."
  ]

/// The reminder for an UNDECLARED slow verb when the mode is advise: the same loop as `denyReason`, but the
/// command was allowed to run, and the escape hatch is not mentioned because nothing needs escaping.
let adviceReason (verb: SlowVerb) =
  let v = SlowVerb.toToken verb
  String.concat "\n" [
    sprintf "SageFs is running with a Ready session here, so `dotnet %s` is off the inner loop. %s=%s is set, so it was allowed to run." v GuardMode.Variable (GuardMode.toToken Advising)
    "Loop: send_fsharp_code for RED then GREEN, persist to the .fs file, hard_reset_fsi_session rebuild=true, re-verify in the session, commit."
    "dotnet build/test/run/fsi is for the final gate, run once when you're done, and for the one build before create_session (a worktree gets its own session)."
  ]

/// The note for a slow verb that was allowed because the directory's session is Faulted: there is no REPL to go
/// back to. It says what the session itself recorded as the fault, whole, and the way back. For a project with no
/// build for any framework the host can load, that recorded text already names the exact command to run (issue
/// #153), so the agent is told the fix instead of guessing it.
let faultAdvice (verb: SlowVerb) (reason: string) =
  let v = SlowVerb.toToken verb
  String.concat "\n" [
    sprintf "SageFs is running, but its session for this directory is Faulted, so there is no REPL to go back to and `dotnet %s` was allowed to run." v
    "The session's own fault reason:"
    reason
    "To get the REPL back: do what the reason says (when it names a command, run exactly that one), then call hard_reset_fsi_session with rebuild=true. If the reason is a SageFs bug, report it with the exact text above."
  ]

/// Puts what is wrong with the configuration under a message, so the caller can see why the guard is stricter than they set it.
let withProblems (problems: string list) (message: string) =
  match problems with
  | [] -> message
  | _ -> String.concat "\n" (message :: problems)

/// The guard's whole decision.
///   - A slow verb (declared or not) whose directory has a FAULTED session and no Ready one is allowed with
///     that session's own fault reason as advice, in either mode: nothing is denied, so there is no preference
///     for the mode to bend (issue #160).
///   - An UNDECLARED slow verb, in an F# workspace, with SageFs answering
///     AND a Ready session whose working directory covers this cwd, is
///     denied on "use the REPL" grounds — unrelated to memory pressure.
///     With SageFs up but no Ready session here, there is no REPL to go
///     back to — that is exactly the one build `create_session` itself
///     needs (issue #144), so it passes. A `Session` probe that failed or
///     was never asked (`None`) fails OPEN the same way: this hook must
///     never be the reason a build that needed to run couldn't.
///   - A DECLARED final gate is legitimate, but still asks for a lease
///     first: `LeaseGranted` (or no daemon to ask) allows it; `LeaseRefused`/
///     `LeaseMustWait` denies it as BUSY, with the reason and the wait.
/// With no daemon there is no REPL to go back to and no pool to ask, so
/// everything passes either way.
///   - The `policy` bends this three ways: its allowlist leaves commands out before any of the above, advise
///     mode turns the "use the REPL" deny into an allow that carries the reminder (the BUSY deny stays a
///     deny, it is about memory and not about a preference), and its `Problems` ride along under any message.
let decideWith (policy: Policy) (command: string) (ctx: Context) : Decision =
  match classifyWith policy.Allow command with
  | NoSlowLoop -> Allow
  | SlowLoop verb ->
    match ctx.Project, ctx.Daemon, ctx.Session with
    | FSharpWorkspace, DaemonAnswering, Some ReadySessionCoversCwd ->
      match policy.Mode with
      | Enforcing -> Deny(withProblems policy.Problems (denyReason verb))
      | Advising -> AllowWithAdvice(withProblems policy.Problems (adviceReason verb))
    | FSharpWorkspace, DaemonAnswering, Some(FaultedSessionCoversCwd reason) ->
      AllowWithAdvice(withProblems policy.Problems (faultAdvice verb reason))
    | FSharpWorkspace, DaemonAnswering, (Some NoReadySessionCoversCwd | None)
    | NotFSharpWorkspace, _, _
    | _, DaemonNotAnswering, _ -> Allow
  | DeclaredFinalGate verb ->
    match ctx.Project, ctx.Daemon, ctx.Lease with
    | FSharpWorkspace, DaemonAnswering, Some(LeaseRefused _ as probe)
    | FSharpWorkspace, DaemonAnswering, Some(LeaseMustWait _ as probe) ->
      Deny(withProblems policy.Problems (busyDenyReason verb probe))
    | FSharpWorkspace, DaemonAnswering, Some LeaseGranted
    | FSharpWorkspace, DaemonAnswering, None ->
      match ctx.Session with
      | Some(FaultedSessionCoversCwd reason) -> AllowWithAdvice(withProblems policy.Problems (faultAdvice verb reason))
      | Some ReadySessionCoversCwd
      | Some NoReadySessionCoversCwd
      | None -> Allow
    | NotFSharpWorkspace, _, _
    | _, DaemonNotAnswering, _ -> Allow

/// `decideWith` with the default policy: enforcing, nothing allowlisted.
let decide (command: string) (ctx: Context) : Decision = decideWith Policy.strict command ctx

/// Whether the guard has a use of the escape hatch to report to the daemon for a command.
type HatchReport =
  | Counted of SageFs.HatchUse
  | NotCounted

let hatchReportWith (policy: Policy) (command: string) (ctx: Context) : HatchReport =
  NotCounted

/// Whether the hook needs to look at the disk and the network at all, once `allow` has left its commands out.
/// The program checks this first so non-dotnet commands never pay for a probe.
/// A declared final gate needs context too, it still has to ask for a lease before it can be allowed through.
let needsContextWith (allow: Allowlist) (command: string) =
  match classifyWith allow command with
  | SlowLoop _
  | DeclaredFinalGate _ -> true
  | NoSlowLoop -> false

/// `needsContextWith` with no allowlist.
let needsContext (command: string) = needsContextWith Allowlist.none command

/// One allowlist entry as its words. It has to be a single plain dotnet command: no quotes, pipes, semicolons,
/// `&&` or subshells, because the guard matches it against one command at a time and a pattern that could never
/// match one would just be a silent no-op.
let parsePattern (pattern: string) : Result<string list, string> =
  let wordsOf = pattern.Split([| ' '; '\t' |], System.StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
  match wordsOf with
  | [] -> Error "an entry in \"allow\" is empty"
  | first :: _ when first <> "dotnet" -> Error(sprintf "the pattern \"%s\" must start with dotnet" pattern)
  | _ when segments pattern <> [ wordsOf ] ->
    Error(sprintf "the pattern \"%s\" must be one command: no quotes, pipes, semicolons, && or subshells" pattern)
  | _ -> Ok wordsOf

/// The text of `<git root>/.SageFs/repl-guard.json`. The one key is `allow`, an array of dotnet command
/// patterns. Anything else is an Error that says what, so a typo is never a silently empty allowlist.
let parseConfig (json: string) : Result<Allowlist, string> =
  try
    use doc = System.Text.Json.JsonDocument.Parse json
    let root = doc.RootElement
    match root.ValueKind with
    | System.Text.Json.JsonValueKind.Object ->
      let props = root.EnumerateObject() |> List.ofSeq
      match props |> List.tryFind (fun p -> p.Name <> "allow") with
      | Some stray -> Error(sprintf "the key \"%s\" is not known; the only key is \"allow\"" stray.Name)
      | None ->
        match props |> List.tryFind (fun p -> p.Name = "allow") with
        | None -> Ok Allowlist.none
        | Some allow when allow.Value.ValueKind <> System.Text.Json.JsonValueKind.Array ->
          Error "\"allow\" must be an array of strings"
        | Some allow ->
          allow.Value.EnumerateArray()
          |> List.ofSeq
          |> List.fold
            (fun acc entry ->
              match acc with
              | Error _ -> acc
              | Ok patterns ->
                match entry.ValueKind with
                | System.Text.Json.JsonValueKind.String ->
                  parsePattern (entry.GetString()) |> Result.map (fun words -> patterns @ [ words ])
                | _ -> Error "every entry in \"allow\" must be a string")
            (Ok [])
          |> Result.map (fun patterns -> { Patterns = patterns })
    | _ -> Error "the top level must be an object like { \"allow\": [ \"dotnet build Foo.fsproj\" ] }"
  with :? System.Text.Json.JsonException as e -> Error(sprintf "it is not valid JSON: %s" e.Message)
