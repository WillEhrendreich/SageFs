/// How an expression row of a watch tab is answered (docs/plans/live-bindings-watch-view.md, step 8). An expression made of names and
/// fields is read from the values the session's walk already holds, so none of the program's code runs. An expression that needs code
/// (a call, an operator) is held until the user clicks it in Safe mode, which is the rule the held getters follow, and runs by itself
/// only in Everything mode, where the walk already runs getters on its own.
module SageFs.Features.Tweak.WatchRows

open SageFs
open SageFs.Features.LiveValueTree

/// What the pane does with an expression row, before any value exists.
[<RequireQualifiedAccess>]
type Reading =
  /// Read it off the walk along this path: a binding, then fields of it.
  | ReadFromWalk of path: string list
  /// It runs code, so it waits for a click.
  | HeldUntilClicked of why: string
  /// It runs code and the mode allows that on every refresh.
  | RunsOnItsOwn of why: string

/// The rule for a mode. Safe and Off hold what runs code, because a click is the user asking for it and a refresh is not; Everything is
/// the mode the user picked to have the program's code run by the pane.
let decide (mode: WalkMode) (reach: ExpressionReach) : Reading =
  match reach with
  | ExpressionReach.NamesOnly path -> Reading.ReadFromWalk path
  | ExpressionReach.RunsCode why ->
    match mode with
    | WalkMode.Everything -> Reading.RunsOnItsOwn why
    | WalkMode.Safe
    | WalkMode.Off -> Reading.HeldUntilClicked why

/// What the session said to a click on a row that runs code.
[<RequireQualifiedAccess>]
type RunOutcome =
  | RanOk of text: string
  | RanFailed of reason: string

/// What an expression row shows.
[<RequireQualifiedAccess>]
type Answer =
  /// Read from the walk, with the type the walk gave it.
  | Value of preview: string * typeName: string
  /// Waiting for a click, and why.
  | Held of why: string
  /// The session's answer to a click.
  | Ran of text: string
  | RunFailed of reason: string
  /// A name that is not bound, or a member the value has not got, in words.
  | NotFound of words: string

/// What reading a path off the walk found.
[<RequireQualifiedAccess>]
type WalkRead =
  | Found of LiveValueNode
  | NoBinding of name: string
  /// `step` is not a member of `under`.
  | NoMember of step: string * under: string

/// The node a path names: the binding called by the first step, then the child labelled by each further step.
let readPath (walk: LiveValueSnapshot) (path: string list) : WalkRead =
  match path with
  | [] -> WalkRead.NoBinding ""
  | first :: steps ->
    match walk.Bindings |> List.tryFind (fun b -> b.Name = first) with
    | None -> WalkRead.NoBinding first
    | Some binding ->
      let rec descend (under: string) (current: LiveValueNode) (remaining: string list) : WalkRead =
        match remaining with
        | [] -> WalkRead.Found current
        | step :: rest ->
          match current.Children |> List.tryFind (fun c -> c.Label = step) with
          | Some child -> descend step child rest
          | None -> WalkRead.NoMember(step, under)
      descend first binding.Root steps

/// What a row shows. A name or a field is read from the walk whatever was run before; an expression that runs code shows what the last
/// click answered, and until there is a click it is held. A node the walk itself held (a getter it did not run) is held here too: the
/// click evaluates the expression, which runs the getter.
let answer (mode: WalkMode) (walk: LiveValueSnapshot option) (lastRun: RunOutcome option) (expression: WatchExpression) : Answer =
  match decide mode (WatchExpression.reach expression) with
  | Reading.ReadFromWalk path ->
    match walk with
    | None -> Answer.NotFound "this session has no values yet"
    | Some snapshot ->
      match readPath snapshot path with
      | WalkRead.Found node ->
        match node.Kind with
        | NodeKind.NotEvaluated _ -> Answer.Held node.Preview
        | NodeKind.Leaf
        | NodeKind.Record
        | NodeKind.List
        | NodeKind.Map
        | NodeKind.Option
        | NodeKind.Union
        | NodeKind.Tuple
        | NodeKind.Array
        | NodeKind.Class
        | NodeKind.Closure
        | NodeKind.Cycle
        | NodeKind.Truncated -> Answer.Value(node.Preview, node.TypeName)
      | WalkRead.NoBinding name -> Answer.NotFound(sprintf "no value called %s is bound in this session" name)
      | WalkRead.NoMember(step, under) -> Answer.NotFound(sprintf "%s has no member %s" under step)
  | Reading.HeldUntilClicked why
  | Reading.RunsOnItsOwn why ->
    match lastRun with
    | None -> Answer.Held why
    | Some(RunOutcome.RanOk text) -> Answer.Ran text
    | Some(RunOutcome.RanFailed reason) -> Answer.RunFailed reason
