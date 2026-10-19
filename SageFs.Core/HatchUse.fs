namespace SageFs

/// A `dotnet build`, `test`, `run` or `fsi` that ran while the daemon was up: the escape hatch, as the
/// sagefs-repl-guard hook sees it. This file has no dependency past FSharp.Core on purpose. The hook
/// (tools/agent-hooks) compiles this same file, because it has to start fast and cannot reference SageFs.Core,
/// and the daemon counts what the hook reports. One closed type with one set of wire tokens, so neither side
/// spells a token by hand.

/// What the command said it was.
[<RequireQualifiedAccess>]
type GateDeclaration =
  /// `SAGEFS_FINAL_GATE=1` in front of it, or a Release build, which the guard treats as the same thing.
  | DeclaredGate
  /// A bare `dotnet build`, `test`, `run` or `fsi`.
  | UndeclaredGate

/// What the REPL for the command's directory was doing when the command ran.
[<RequireQualifiedAccess>]
type ReplAtTheTime =
  /// A Ready session covers the directory. A fallback here is the final gate, or advise mode letting it run.
  | Ready
  /// A session covers the directory and it is Faulted: the REPL the agent would go back to is broken.
  | Faulted
  /// No session covers the directory, or the one that does is not Ready yet (still starting). This includes the
  /// one build `create_session` needs before there is a session, which the guard exists to let through.
  | NotReady

/// One use of the hatch: the two facts that say what kind it was.
type HatchUse = { Declaration: GateDeclaration; Repl: ReplAtTheTime }

/// A report the daemon could not read as a `HatchUse`.
[<RequireQualifiedAccess>]
type HatchRefusal =
  | UnknownDeclaration of token: string
  | UnknownRepl of token: string

module GateDeclaration =
  let all = [ GateDeclaration.DeclaredGate; GateDeclaration.UndeclaredGate ]

  /// The wire token for each declaration, the one place it is spelled.
  let toToken (declaration: GateDeclaration) : string =
    match declaration with
    | GateDeclaration.DeclaredGate -> "declared"
    | GateDeclaration.UndeclaredGate -> "undeclared"

  let tryParse (token: string) : GateDeclaration option =
    all |> List.tryFind (fun d -> toToken d = token)

module ReplAtTheTime =
  let all = [ ReplAtTheTime.Ready; ReplAtTheTime.Faulted; ReplAtTheTime.NotReady ]

  /// The wire token for each state, the one place it is spelled.
  let toToken (repl: ReplAtTheTime) : string =
    match repl with
    | ReplAtTheTime.Ready -> "ready"
    | ReplAtTheTime.Faulted -> "faulted"
    | ReplAtTheTime.NotReady -> "not_ready"

  let tryParse (token: string) : ReplAtTheTime option =
    all |> List.tryFind (fun r -> toToken r = token)

module HatchRefusal =
  /// What was wrong with the report and what would have been accepted.
  let describe (refusal: HatchRefusal) : string =
    let known (tokens: string list) = String.concat ", " tokens
    match refusal with
    | HatchRefusal.UnknownDeclaration token ->
      sprintf "unknown 'declaration' \"%s\": it must be one of %s" token (GateDeclaration.all |> List.map GateDeclaration.toToken |> known)
    | HatchRefusal.UnknownRepl token ->
      sprintf "unknown 'repl' \"%s\": it must be one of %s" token (ReplAtTheTime.all |> List.map ReplAtTheTime.toToken |> known)

module HatchUse =
  /// Every use there can be, in the order the daemon reports them.
  let all : HatchUse list =
    [ for declaration in GateDeclaration.all do
        for repl in ReplAtTheTime.all do
          yield { Declaration = declaration; Repl = repl } ]

  /// The wire form of one use: the two tokens.
  let toTokens (hatch: HatchUse) : string * string =
    GateDeclaration.toToken hatch.Declaration, ReplAtTheTime.toToken hatch.Repl

  /// Reads the two tokens a report carries. Anything else is refused with what it got and what is accepted.
  let tryParse (declaration: string) (repl: string) : Result<HatchUse, HatchRefusal> =
    match GateDeclaration.tryParse declaration, ReplAtTheTime.tryParse repl with
    | Some d, Some r -> Ok { Declaration = d; Repl = r }
    | None, _ -> Error(HatchRefusal.UnknownDeclaration declaration)
    | Some _, None -> Error(HatchRefusal.UnknownRepl repl)
