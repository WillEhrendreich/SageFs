/// Live state the app kept when you edited its initializer (rule 3 of the
/// state spec), as the worker's HTTP surface sees it: what's pending, and the
/// reset that runs the new initializer.
module SageFs.Features.KeptState

open SageFs.Features.ReloadOutcome

/// One kept binding waiting for a reset: what the save said about it, and what
/// the reset needs to run its new initializer in the right module.
type Pending = {
  Value: KeptValue
  Decls: SageFs.Features.ReloadPlanning.FileDecls
  Decl: SageFs.Features.ReloadPlanning.SourceDecl
}

module Pending =
  /// The qualified name a kept binding is reported and reset by.
  let bindingName (decls: SageFs.Features.ReloadPlanning.FileDecls) (decl: SageFs.Features.ReloadPlanning.SourceDecl) =
    String.concat "." (decls.ModulePath @ decl.Container @ [ decl.Name ])

/// What a reset of one kept binding did.
[<RequireQualifiedAccess>]
type ResetOutcome =
  /// The new initializer ran and the app's field now holds its value.
  | Reset of binding: string * value: string
  /// Nothing is waiting for that binding (never kept, or already reset).
  | NothingPending of binding: string
  /// The initializer or the write failed. The live value is untouched.
  | ResetFailed of binding: string * reason: string

/// What the worker's HTTP endpoints can see and do. A record of functions so
/// the endpoints don't need to know how a reset reaches the app.
type Access = {
  Pending: unit -> KeptValue list
  Reset: string -> Async<ResetOutcome>
}

module Access =
  /// For a worker with nothing kept and no way to reset (and for tests of the
  /// other endpoints).
  let none : Access =
    { Pending = fun () -> []
      Reset = fun binding -> async { return ResetOutcome.NothingPending binding } }

module ResetOutcome =
  let private json (value: obj) = System.Text.Json.JsonSerializer.Serialize value

  /// The HTTP status a client branches on.
  let status =
    function
    | ResetOutcome.Reset _ -> 200
    | ResetOutcome.NothingPending _ -> 404
    | ResetOutcome.ResetFailed _ -> 500

  /// One line a person can read, on the dashboard or in an MCP reply.
  let describe =
    function
    | ResetOutcome.Reset(binding, value) -> sprintf "Reset '%s': it's %s now." binding value
    | ResetOutcome.NothingPending binding ->
      sprintf "Nothing to reset for '%s'. It wasn't kept by a save, or it's already been reset." binding
    | ResetOutcome.ResetFailed(binding, reason) ->
      sprintf "Couldn't reset '%s', so its live value is untouched: %s" binding reason

  let toJson (outcome: ResetOutcome) : string =
    let case, binding =
      match outcome with
      | ResetOutcome.Reset(b, _) -> "Reset", b
      | ResetOutcome.NothingPending b -> "NothingPending", b
      | ResetOutcome.ResetFailed(b, _) -> "ResetFailed", b
    sprintf """{"outcome":%s,"binding":%s,"message":%s}""" (json case) (json binding) (json (describe outcome))

/// The `kept` array of `GET /hotreload`: the same fields the save's own report
/// carries, so a client reads both with one parser.
let pendingJson (pending: KeptValue list) : string =
  let json (value: obj) = System.Text.Json.JsonSerializer.Serialize value
  pending
  |> List.map (fun k ->
    sprintf """{"binding":%s,"keptValue":%s,"newInitializer":%s}""" (json k.Binding) (json k.KeptValue) (json k.NewInitializer))
  |> String.concat ","
  |> sprintf "[%s]"
