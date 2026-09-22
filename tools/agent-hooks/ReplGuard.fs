/// The decision behind the sagefs-repl-guard Claude Code hook.
///
/// Pure: a Bash command string plus what the hook found out about the world
/// goes in, Allow or Deny comes out. No IO in here, so SageFs.Tests compiles
/// this same file and tests it, and sagefs-repl-guard.fsx #loads it and runs it.
/// No dependencies past FSharp.Core, because the script has to load fast.
module SageFs.AgentHooks.ReplGuard

/// Whether the hook's working directory has an .fsproj, .slnx or .sln at or above it.
type ProjectScope =
  | FSharpWorkspace
  | NotFSharpWorkspace

/// Whether a SageFs daemon answered /health on localhost.
type DaemonProbe =
  | DaemonAnswering
  | DaemonNotAnswering

type Context = { Project: ProjectScope; Daemon: DaemonProbe }

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
  | Deny of reason: string

[<Literal>]
let FinalGateVariable = "SAGEFS_FINAL_GATE"

let private finalGateAssignment = FinalGateVariable + "=1"

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

/// The slow verb a single segment runs, and whether that segment declared
/// the final gate in its prefix.
let private classifySegment (words: string list) : (SlowVerb * bool) option =
  let prefix = words |> List.takeWhile (fun w -> not (isDotnet w) && isPrefixWord w)
  match List.skip prefix.Length words with
  | dotnet :: verb :: _ when isDotnet dotnet ->
    SlowVerb.tryParse verb
    |> Option.map (fun v -> v, prefix |> List.contains finalGateAssignment)
  | _ -> None

let private isFinalGateExport (words: string list) =
  match words with
  | "export" :: rest -> rest |> List.contains finalGateAssignment
  | _ -> false

/// The first slow-loop dotnet call in the command, if any. An earlier
/// `export SAGEFS_FINAL_GATE=1` segment counts as a declaration for every
/// segment after it.
let classify (command: string) : CommandClass =
  let rec go exported segs =
    match segs with
    | [] -> NoSlowLoop
    | seg :: rest ->
      match classifySegment seg with
      | Some (verb, declared) when declared || exported ->
        match go exported rest with
        | SlowLoop v -> SlowLoop v
        | _ -> DeclaredFinalGate verb
      | Some (verb, _) -> SlowLoop verb
      | None -> go (exported || isFinalGateExport seg) rest
  go false (segments command)

/// The deny reason: the loop in two lines, then the way out.
let denyReason (verb: SlowVerb) =
  let v = SlowVerb.toToken verb
  String.concat "\n" [
    sprintf "SageFs is running, so `dotnet %s` is off the inner loop. Loop: send_fsharp_code for RED then GREEN, persist to the .fs file, hard_reset_fsi_session rebuild=true, re-verify in the session, commit." v
    "dotnet build/test/run/fsi is for the final gate, run once when you're done, and for the one build before create_session (a worktree gets its own session)."
    sprintf "If this is one of those, rerun it as `%s dotnet %s ...`. If the REPL is fighting you, report the exact error first, then use that escape hatch for that one step only." finalGateAssignment v
  ]

/// The guard's whole decision. Only a slow-loop verb, in an F# workspace,
/// with SageFs answering, is denied. With no daemon there's no REPL to go
/// back to, so everything passes.
let decide (command: string) (ctx: Context) : Decision =
  match classify command with
  | NoSlowLoop
  | DeclaredFinalGate _ -> Allow
  | SlowLoop verb ->
    match ctx.Project, ctx.Daemon with
    | FSharpWorkspace, DaemonAnswering -> Deny(denyReason verb)
    | NotFSharpWorkspace, _
    | _, DaemonNotAnswering -> Allow

/// Whether the hook needs to look at the disk and the network at all. The
/// script checks this first so non-dotnet commands never pay for a probe.
let needsContext (command: string) =
  match classify command with
  | SlowLoop _ -> true
  | NoSlowLoop
  | DeclaredFinalGate _ -> false
