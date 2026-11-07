namespace SageFs

/// A watch tab's list of expressions as a setting (docs/plans/live-bindings-watch-view.md, step 8). It lives in the same layers as the
/// view: a list kept for this app, or for everywhere. The value is `VExpressions`, whose payload can only hold expressions the parser
/// accepted, and the stored form is one expression to a line.
[<RequireQualifiedAccess>]
module WatchSettings =

  [<Literal>]
  let Key = "bindings.watch.expressions"

  let valueOf (expressions: WatchExpression list) : SettingValue = VExpressions expressions

  /// The expressions a value holds; any other kind of value holds none.
  let expressionsOf (value: SettingValue) : WatchExpression list =
    match value with
    | VExpressions expressions -> expressions
    | VToggle _
    | VPort _
    | VTimeout _
    | VBindHost _
    | VEnum _
    | VDir _ -> []

  let descriptor : SettingDescriptor = {
    Key = Key
    Name = "Watch expressions"
    Description = "The expressions the Watch tab of the live bindings pane lists, one to a line."
    Category = LiveBindingsView
    Scope = RepoOverridable
    Applicability = Live
    Default = VExpressions []
    Parse = fun raw ->
      match WatchExpression.parseLines raw with
      | good, [] -> Ok (VExpressions good)
      | _, (line, why) :: _ -> Error (Malformed (sprintf "'%s': %s" line (ExpressionError.describe why)))
    Render = fun value -> WatchExpression.render (expressionsOf value)
    Apply = ignore
  }

  /// The list with the expression added at the end, unless it is already there.
  let add (expression: WatchExpression) (expressions: WatchExpression list) : WatchExpression list =
    match List.contains expression expressions with
    | true -> expressions
    | false -> expressions @ [ expression ]

  let remove (expression: WatchExpression) (expressions: WatchExpression list) : WatchExpression list =
    expressions |> List.filter (fun e -> e <> expression)
