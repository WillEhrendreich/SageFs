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

/// Why the reflection report isn't there.
[<RequireQualifiedAccess>]
type ReflectionReadsError =
  /// No agent to ask (no session, or the host went away), and why.
  | NoAgent of reason: string
  /// The worker's report didn't parse, and what was wrong with it.
  | UnreadableReport of why: string
  /// Not a mode, and the ones that are.
  | UnknownMode of unknown: SageFs.Middleware.ValueReads.ReflectionReadMode.Unknown

module ReflectionReadsError =
  let describe (error: ReflectionReadsError) : string =
    match error with
    | ReflectionReadsError.NoAgent reason -> reason
    | ReflectionReadsError.UnreadableReport why -> sprintf "the reflection report didn't parse: %s" why
    | ReflectionReadsError.UnknownMode unknown -> SageFs.Middleware.ValueReads.ReflectionReadMode.describeUnknown unknown

/// What the worker's HTTP endpoints can see and do about hot reload's live
/// state. A record of functions so the endpoints don't need to know how any of
/// it reaches the app.
type Access = {
  Pending: unit -> KeptValue list
  Reset: string -> Async<ResetOutcome>
  /// Where rule 2's reflection reads stand in the app's process.
  ReflectionReads: unit -> Result<SageFs.Middleware.ValueReads.ReflectionReadsReport, ReflectionReadsError>
  /// Switch the running app's reflection read mode.
  SetReflectionMode: SageFs.Middleware.ValueReads.ReflectionReadMode -> Result<SageFs.Middleware.ValueReads.ReflectionReadsReport, ReflectionReadsError>
}

module Access =
  /// For a worker with nothing kept, no way to reset and no agent (and for
  /// tests of the other endpoints).
  let none : Access =
    { Pending = fun () -> []
      Reset = fun binding -> async { return ResetOutcome.NothingPending binding }
      ReflectionReads = fun () -> Result.Error(ReflectionReadsError.NoAgent "this worker has no agent to ask")
      SetReflectionMode = fun _ -> Result.Error(ReflectionReadsError.NoAgent "this worker has no agent to ask") }


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

/// What the dashboard knows about a session's reflection reads: the worker
/// reported them, or it didn't (an older worker, or no agent), and why.
[<RequireQualifiedAccess>]
type ReflectionReadsView =
  | Reported of report: SageFs.Middleware.ValueReads.ReflectionReadsReport
  | NotReported of why: string

/// The reflection report on the worker's JSON (`GET /hotreload`'s
/// `reflectionReads`). Written and read here and nowhere else, so the two
/// sides can't drift. Modes travel as their config names.
module ReflectionReadsJson =
  open System.Text.Json
  open SageFs.Middleware.ValueReads

  [<Literal>]
  let private Watching = "watching"
  [<Literal>]
  let private NotWatching = "notWatching"
  [<Literal>]
  let private Lapsed = "lapsed"
  [<Literal>]
  let private Asked = "asked"
  [<Literal>]
  let private Chosen = "chosen"

  let private writeNotice (w: Utf8JsonWriter) (n: ReflectionNotice) =
    w.WriteStartObject()
    w.WriteString("value", n.Value)
    w.WriteString("caller", n.Caller)
    w.WriteNumber("readsPerSecond", n.ReadsPerSecond)
    w.WriteString("mode", ReflectionReadMode.name n.Mode)
    w.WriteString("message", ReflectionNotices.describe n)
    w.WriteEndObject()

  /// The report as a JSON object.
  let render (report: ReflectionReadsReport) : string =
    use stream = new System.IO.MemoryStream()
    (use w = new Utf8JsonWriter(stream)
     w.WriteStartObject()
     w.WriteString("mode", ReflectionReadMode.name report.Mode)
     w.WriteStartObject "watch"
     match report.Watch with
     | ReflectionWatchStatus.Watching ->
       w.WriteString("state", Watching)
       w.WriteString("why", "")
     | ReflectionWatchStatus.NotWatching why ->
       w.WriteString("state", NotWatching)
       w.WriteString("why", why)
     | ReflectionWatchStatus.Lapsed why ->
       w.WriteString("state", Lapsed)
       w.WriteString("why", why)
     w.WriteEndObject()
     w.WriteNumber("walks", report.Walks)
     w.WriteNumber("siteHits", report.SiteHits)
     w.WriteStartArray "notices"
     for n in report.Notices do
       w.WriteStartObject()
       w.WriteString("value", n.Value)
       match n.State with
       | NoticeState.Unasked -> w.WriteString("state", "unasked")
       | NoticeState.Asked notice ->
         w.WriteString("state", Asked)
         w.WritePropertyName "notice"
         writeNotice w notice
       | NoticeState.Chosen(notice, mode) ->
         w.WriteString("state", Chosen)
         w.WritePropertyName "notice"
         writeNotice w notice
         w.WriteString("chosen", ReflectionReadMode.name mode)
       w.WriteEndObject()
     w.WriteEndArray()
     w.WriteEndObject())
    System.Text.Encoding.UTF8.GetString(stream.ToArray())

  let private str (el: JsonElement) (name: string) : Result<string, ReflectionReadsError> =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Result.Ok(v.GetString() |> Option.ofObj |> Option.defaultValue "")
    | _ -> Result.Error(ReflectionReadsError.UnreadableReport(sprintf "no '%s'" name))

  let private int64Of (el: JsonElement) (name: string) : Result<int64, ReflectionReadsError> =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Number -> Result.Ok(v.GetInt64())
    | _ -> Result.Error(ReflectionReadsError.UnreadableReport(sprintf "no '%s'" name))

  let private modeOf (el: JsonElement) (name: string) =
    str el name |> Result.bind (fun text -> ReflectionReadMode.parse text |> Result.mapError ReflectionReadsError.UnknownMode)

  let private noticeOf (el: JsonElement) : Result<ReflectionNotice, ReflectionReadsError> =
    match str el "value", str el "caller", int64Of el "readsPerSecond", modeOf el "mode" with
    | Result.Ok value, Result.Ok caller, Result.Ok rate, Result.Ok mode ->
      Result.Ok { Value = value; Caller = caller; ReadsPerSecond = int rate; Mode = mode }
    | Result.Error e, _, _, _
    | _, Result.Error e, _, _
    | _, _, Result.Error e, _
    | _, _, _, Result.Error e -> Result.Error e

  let private valueNoticeOf (el: JsonElement) : Result<ValueNotice, ReflectionReadsError> =
    let noticeIn () =
      match el.TryGetProperty "notice" with
      | true, n -> noticeOf n
      | false, _ -> Result.Error(ReflectionReadsError.UnreadableReport "no 'notice'")
    match str el "value", str el "state" with
    | Result.Ok value, Result.Ok Asked -> noticeIn () |> Result.map (fun n -> { Value = value; State = NoticeState.Asked n })
    | Result.Ok value, Result.Ok Chosen ->
      match noticeIn (), modeOf el "chosen" with
      | Result.Ok n, Result.Ok mode -> Result.Ok { Value = value; State = NoticeState.Chosen(n, mode) }
      | Result.Error e, _
      | _, Result.Error e -> Result.Error e
    | Result.Ok value, Result.Ok _ -> Result.Ok { Value = value; State = NoticeState.Unasked }
    | Result.Error e, _
    | _, Result.Error e -> Result.Error e

  /// The report back from the worker's JSON, or what's wrong with it.
  let parse (el: JsonElement) : Result<ReflectionReadsReport, ReflectionReadsError> =
    let watch =
      match el.TryGetProperty "watch" with
      | true, w ->
        match str w "state", str w "why" with
        | Result.Ok Watching, _ -> Result.Ok ReflectionWatchStatus.Watching
        | Result.Ok NotWatching, Result.Ok why -> Result.Ok(ReflectionWatchStatus.NotWatching why)
        | Result.Ok Lapsed, Result.Ok why -> Result.Ok(ReflectionWatchStatus.Lapsed why)
        | Result.Ok other, _ -> Result.Error(ReflectionReadsError.UnreadableReport(sprintf "unknown watch state '%s'" other))
        | Result.Error e, _ -> Result.Error e
      | false, _ -> Result.Error(ReflectionReadsError.UnreadableReport "no 'watch'")
    let notices =
      match el.TryGetProperty "notices" with
      | true, arr when arr.ValueKind = JsonValueKind.Array ->
        arr.EnumerateArray()
        |> Seq.fold
             (fun acc n ->
               match acc, valueNoticeOf n with
               | Result.Ok xs, Result.Ok x -> Result.Ok(xs @ [ x ])
               | Result.Error e, _
               | _, Result.Error e -> Result.Error e)
             (Result.Ok [])
      | _ -> Result.Error(ReflectionReadsError.UnreadableReport "no 'notices'")
    match modeOf el "mode", watch, notices, int64Of el "walks", int64Of el "siteHits" with
    | Result.Ok mode, Result.Ok watch, Result.Ok notices, Result.Ok walks, Result.Ok hits ->
      Result.Ok { Mode = mode; Watch = watch; Notices = notices; Walks = walks; SiteHits = hits }
    | Result.Error e, _, _, _, _
    | _, Result.Error e, _, _, _
    | _, _, Result.Error e, _, _
    | _, _, _, Result.Error e, _
    | _, _, _, _, Result.Error e -> Result.Error e

/// The reflection report as text, for MCP.
module ReflectionReadsText =
  open SageFs.Middleware.ValueReads

  let describe (report: ReflectionReadsReport) : string =
    let watch =
      match report.Watch with
      | ReflectionWatchStatus.Watching -> "The reflection watch is on."
      | ReflectionWatchStatus.NotWatching why -> sprintf "The reflection watch isn't on (%s), so each value's getter carries it instead." why
      | ReflectionWatchStatus.Lapsed why -> sprintf "The reflection watch lapsed: %s. Every value edit restarts until the app restarts." why
    let questions =
      report.Notices
      |> List.choose (fun n ->
        match n.State with
        | NoticeState.Asked notice -> Some(ReflectionNotices.describe notice)
        | NoticeState.Chosen(notice, mode) -> Some(sprintf "Answered: %s (you picked %s)." notice.Value (ReflectionReadMode.name mode))
        | NoticeState.Unasked -> None)
    let modes =
      ReflectionReadMode.all
      |> List.map (fun mode -> sprintf "- %s: %s" (ReflectionReadMode.name mode) (ReflectionReadMode.consequence mode))
    [ yield sprintf "Reflection reads: %s (%s)." (ReflectionReadMode.name report.Mode) (ReflectionReadMode.cost report.Mode)
      yield watch
      yield sprintf "Stack walks so far: %d. Reads named by a rewired caller: %d." report.Walks report.SiteHits
      match questions with
      | [] -> yield "No hot reflective loops so far."
      | qs -> yield! qs
      yield "Modes:"
      yield! modes ]
    |> String.concat "\n"
