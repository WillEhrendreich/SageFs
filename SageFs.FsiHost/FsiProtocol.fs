/// Wire protocol between SageFs and an isolated FSI host process.
///
/// BCL + FSharp.Core ONLY. This single file is compiled into BOTH SageFs.Core and the per-SDK
/// generated FSI host (see FsiHostBuild in SageFs.Core), and the host may reference nothing from
/// SageFs: that is what makes it impossible for a SageFs package or runtime to conflict with the
/// user's code.
///
/// It cannot drift, by construction:
///   * the message types below are the ONLY definition of the protocol, shared by source between
///     both sides, so a sender and a receiver can never disagree about a shape;
///   * the codec is generic over those types (F# reflection): case names and field names on the wire
///     ARE the type's own names, there is not one hand-written wire string, and adding a case or a
///     field changes encoding and decoding together;
///   * consumers pattern-match on the DUs, so the compiler enforces exhaustiveness at every use;
///   * `checkSupported` proves every type the protocol mentions is representable, and runs at host
///     start (fail closed) and in the tests.
///
/// Framing: one JSON object per line (JSON escapes newlines, so a message never contains a raw one).
module SageFs.FsiHost.FsiProtocol

open System
open System.IO
open System.Text
open System.Text.Json
open Microsoft.FSharp.Reflection
open SageFs
open SageFs.Features

type DiagnosticSeverity =
  | DiagHidden
  | DiagInfo
  | DiagWarning
  | DiagError

type FsiDiagnostic =
  { Severity: DiagnosticSeverity
    ErrorNumber: int
    Subcategory: string
    Message: string
    StartLine: int
    StartColumn: int
    EndLine: int
    EndColumn: int }

/// How a submission ended. Compile errors arrive as `EvalFailed` plus diagnostics.
type EvalOutcome =
  | EvalSucceeded
  | EvalFailed of message: string
  | EvalInterrupted

type OutputStream =
  | StdOut
  | StdErr

/// What a boolean feature gate (`_SageFsHotReload`, `_SageFsCompExpr`) holds in the session.
type FlagReading =
  | FlagWasUnbound
  | FlagWasBool of value: bool
  | FlagWasNotBool of typeName: string

/// What a bound name holds, as display text (a value itself cannot cross the process boundary).
type ValueReading =
  | ValueUnbound
  | ValueText of typeName: string * text: string

/// How a symbol occurs in checked code (a DU, not a bool: a use is not merely "not a definition").
type WireSymbolUse =
  | WireDefinition
  | WireUsage

/// A resolved symbol occurrence, for the live-testing dependency graph.
type WireSymbolRef =
  { SymbolFullName: string
    Use: WireSymbolUse
    FilePath: string
    Line: int }

/// One completion candidate. The glyph is FCS's own case name for it, mapped back by SageFs's exhaustive
/// `ofGlyph`; the (possibly expensive) description is fetched on demand with `Describe`.
type WireCompletion =
  { DisplayText: string
    ReplacementText: string
    Glyph: string }

/// Why a config.fsx expression did not produce a DirectoryConfig.
type ConfigFailure =
  | ConfigDoesNotCompile of diagnostics: FsiDiagnostic list
  | ConfigWrongType of typeName: string
  | ConfigNoValue
  | ConfigThrew of message: string

type ConfigOutcome =
  | ConfigEvaluated of config: DirectoryConfig
  | ConfigRejected of failure: ConfigFailure

type Request =
  | Eval of id: int64 * code: string
  /// Evaluate a config.fsx expression (in the host, never in the daemon) and answer with the DirectoryConfig it builds.
  | EvalConfig of id: int64 * content: string
  | ReadFlag of id: int64 * name: string
  | ReadValue of id: int64 * name: string
  /// The session's bound values as an expanded, bounded tree (the dashboard's watch window). The client picks the generation.
  | ReadLiveValues of id: int64 * generation: int64
  /// Parse + type-check a snippet against the session's current state and report diagnostics.
  | Check of id: int64 * text: string
  /// Like Check, plus the symbol references of error-free code (the live-testing cycle's type-check effect).
  | CheckWithSymbols of id: int64 * filePath: string * text: string
  /// Completion candidates for the caret position; unsorted (SageFs ranks them).
  | Complete of id: int64 * text: string * caret: int
  /// The description of one candidate from the most recent Complete (`completionsId` is that request's id).
  | Describe of id: int64 * completionsId: int64 * index: int
  /// Start the agent (hot reload + live testing) that lives beside the user's code: load the projects, register the
  /// resolver. Answered with what could not be loaded. Every other agent request needs this first.
  | AgentStart of id: int64 * init: HostAgent.AgentInit
  /// The agent's work after an eval: register or detour the redefined methods, then look for tests.
  | AgentAfterEval of id: int64 * request: HostAgent.AfterEval
  /// Scan what the process has loaded for tests.
  | AgentDiscoverLoaded of id: int64
  /// The simple names of the assemblies the process has loaded.
  | AgentLoadedAssemblies of id: int64
  /// The coverage recorded since the last take (resets it).
  | AgentTakeCoverage of id: int64
  /// Run one test. Runs beside the session thread, so a long test never blocks evals or completions.
  | AgentRunTest of id: int64 * test: LiveTesting.TestCase
  /// Where each named module value's reads went, and which readers ran (hot reload rule 2).
  | AgentValueReads of id: int64 * values: string list
  | Interrupt
  | Shutdown

type Response =
  | Ready of runtime: string * fsharpCore: string
  | EvalResult of id: int64 * outcome: EvalOutcome * diagnostics: FsiDiagnostic list
  | FlagResult of id: int64 * reading: FlagReading
  | ValueResult of id: int64 * reading: ValueReading
  | LiveValuesResult of id: int64 * snapshot: LiveValueTree.LiveValueSnapshot
  | CheckResult of id: int64 * diagnostics: FsiDiagnostic list
  | SymbolsResult of id: int64 * diagnostics: FsiDiagnostic list * symbols: WireSymbolRef list
  | CompletionsResult of id: int64 * items: WireCompletion list
  | DescriptionResult of id: int64 * text: string
  | ConfigResult of id: int64 * outcome: ConfigOutcome
  | AgentStartResult of id: int64 * started: HostAgent.AgentStarted
  | AgentAfterEvalResult of id: int64 * report: HostAgent.AfterEvalReport
  | AgentDiscoveryResult of id: int64 * discovery: HostAgent.Discovery
  | AgentTestResult of id: int64 * result: LiveTesting.TestResult
  | AgentLoadedAssembliesResult of id: int64 * names: string list
  | AgentCoverageResult of id: int64 * coverage: HostAgent.CoverageReading
  | AgentValueReadsResult of id: int64 * evidence: SageFs.Middleware.ValueReads.ValueEvidence list
  /// An agent request the host cannot serve (it was not started, or the request failed): the reason, never a guess.
  | AgentRefused of id: int64 * reason: string
  | Output of stream: OutputStream * text: string

/// Why a message could not be encoded/decoded. A typed union (never a bare string) so callers can match on the
/// reason; `describeError` is the one place that turns it into text.
type ProtocolError =
  | NotJson of message: string
  | MissingField of field: string
  | WrongShape of expected: string * found: string
  | UnknownCase of typeName: string * caseName: string
  | NotRepresentable of typeName: string

let describeError (error: ProtocolError) : string =
  match error with
  | NotJson message -> sprintf "not valid JSON: %s" message
  | MissingField field -> sprintf "missing field '%s'" field
  | WrongShape(expected, found) -> sprintf "expected %s but found %s" expected found
  | UnknownCase(typeName, caseName) -> sprintf "unknown case '%s' of %s" caseName typeName
  | NotRepresentable typeName -> sprintf "protocol type %s is not representable on the wire" typeName

// ---- generic codec -------------------------------------------------------------------------

/// Builds an F# list of a runtime-known element type from boxed items. Reached through a generic class behind
/// an interface (not a reflected method name), so there is no lookup-by-string to go stale.
type private IListBuilder =
  abstract Make: obj list -> obj

type private ListBuilderOf<'T>() =
  interface IListBuilder with
    member _.Make(items: obj list) : obj = items |> List.map (fun item -> item :?> 'T) |> box

let private makeList (elementType: Type) (items: obj list) : obj =
  let builderType = typedefof<ListBuilderOf<_>>.MakeGenericType elementType
  (Activator.CreateInstance builderType :?> IListBuilder).Make items

let private isFSharpList (t: Type) = t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>>

let rec private isSupported (seen: Set<string>) (t: Type) : Result<unit, ProtocolError> =
  // System.Type is not comparable, so key the visited set by its display name.
  match seen.Contains(string t) with
  | true -> Result.Ok()
  | false ->
    let seen = seen.Add(string t)
    let all (types: Type seq) =
      types |> Seq.fold (fun acc next -> acc |> Result.bind (fun () -> isSupported seen next)) (Result.Ok())
    if t = typeof<string> || t = typeof<int> || t = typeof<int64> || t = typeof<bool> || t = typeof<DateTimeOffset> || t = typeof<TimeSpan> then Result.Ok()
    elif isFSharpList t then isSupported seen (t.GetGenericArguments().[0])
    elif t.IsArray && t.GetArrayRank() = 1 then isSupported seen (t.GetElementType())
    elif FSharpType.IsRecord t then FSharpType.GetRecordFields t |> Seq.map (fun f -> f.PropertyType) |> all
    elif FSharpType.IsUnion t then
      FSharpType.GetUnionCases t
      |> Seq.collect (fun case -> case.GetFields() |> Seq.map (fun f -> f.PropertyType))
      |> all
    else Result.Error(NotRepresentable(string t))

/// Proves every type the protocol mentions is representable by the codec. Run at host start and in tests.
let checkSupported () : Result<unit, ProtocolError> =
  [ typeof<Request>; typeof<Response> ]
  |> List.fold (fun acc t -> acc |> Result.bind (fun () -> isSupported Set.empty t)) (Result.Ok())

let rec private writeValue (writer: Utf8JsonWriter) (t: Type) (value: obj) : unit =
  if t = typeof<string> then writer.WriteStringValue(value :?> string)
  elif t = typeof<int> then writer.WriteNumberValue(value :?> int)
  elif t = typeof<int64> then writer.WriteNumberValue(value :?> int64)
  elif t = typeof<bool> then writer.WriteBooleanValue(value :?> bool)
  // The round-trip ("O") format keeps every tick and the offset.
  elif t = typeof<DateTimeOffset> then
    writer.WriteStringValue((value :?> DateTimeOffset).ToString("O", System.Globalization.CultureInfo.InvariantCulture))
  // A TimeSpan travels as its tick count: exact, and independent of culture.
  elif t = typeof<TimeSpan> then writer.WriteNumberValue((value :?> TimeSpan).Ticks)
  elif isFSharpList t then
    let elementType = t.GetGenericArguments().[0]
    writer.WriteStartArray()
    for item in (value :?> System.Collections.IEnumerable) do
      writeValue writer elementType item
    writer.WriteEndArray()
  elif t.IsArray && t.GetArrayRank() = 1 then
    let elementType = t.GetElementType()
    writer.WriteStartArray()
    for item in (value :?> System.Collections.IEnumerable) do
      writeValue writer elementType item
    writer.WriteEndArray()
  elif FSharpType.IsRecord t then
    writer.WriteStartObject()
    for field in FSharpType.GetRecordFields t do
      writer.WritePropertyName field.Name
      writeValue writer field.PropertyType (field.GetValue value)
    writer.WriteEndObject()
  elif FSharpType.IsUnion t then
    let case, values = FSharpValue.GetUnionFields(value, t)
    writer.WriteStartObject()
    writer.WriteString("case", case.Name)
    let fields = case.GetFields()
    if fields.Length > 0 then
      writer.WritePropertyName "fields"
      writer.WriteStartObject()
      for index in 0 .. fields.Length - 1 do
        writer.WritePropertyName fields.[index].Name
        writeValue writer fields.[index].PropertyType values.[index]
      writer.WriteEndObject()
    writer.WriteEndObject()
  else
    invalidOp (sprintf "protocol type %s is not representable on the wire" t.FullName)

let private encode<'T> (value: 'T) : string =
  use stream = new MemoryStream()
  use writer = new Utf8JsonWriter(stream)
  writeValue writer typeof<'T> (box value)
  writer.Flush()
  Encoding.UTF8.GetString(stream.ToArray())

let private bind (f: 'a -> Result<'b, ProtocolError>) (r: Result<'a, ProtocolError>) = Result.bind f r

/// Read every element of `items` with `read`, stopping at the first Error.
let private readAll (read: 'a -> Result<obj, ProtocolError>) (items: 'a seq) : Result<obj list, ProtocolError> =
  items
  |> Seq.fold
       (fun acc item ->
         acc |> bind (fun parsed -> read item |> Result.map (fun value -> value :: parsed)))
       (Result.Ok [])
  |> Result.map List.rev

let private objectProperty (element: JsonElement) (name: string) : Result<JsonElement, ProtocolError> =
  match element.ValueKind with
  | JsonValueKind.Object ->
    match element.TryGetProperty name with
    | true, value -> Result.Ok value
    | false, _ -> Result.Error(MissingField name)
  | kind -> Result.Error(WrongShape("an object", string kind))

let rec private readValue (t: Type) (element: JsonElement) : Result<obj, ProtocolError> =
  let wrong (expected: string) = Result.Error(WrongShape(expected, string element.ValueKind))
  if t = typeof<string> then
    match element.ValueKind with
    | JsonValueKind.String ->
      match element.GetString() with
      | null -> wrong "a string"
      | text -> Result.Ok(box text)
    | _ -> wrong "a string"
  elif t = typeof<int> then
    match element.ValueKind with
    | JsonValueKind.Number ->
      match element.TryGetInt32() with
      | true, n -> Result.Ok(box n)
      | false, _ -> wrong "an int"
    | _ -> wrong "an int"
  elif t = typeof<int64> then
    match element.ValueKind with
    | JsonValueKind.Number ->
      match element.TryGetInt64() with
      | true, n -> Result.Ok(box n)
      | false, _ -> wrong "an int64"
    | _ -> wrong "an int64"
  elif t = typeof<bool> then
    match element.ValueKind with
    | JsonValueKind.True -> Result.Ok(box true)
    | JsonValueKind.False -> Result.Ok(box false)
    | _ -> wrong "a bool"
  elif t = typeof<DateTimeOffset> then
    match element.ValueKind with
    | JsonValueKind.String ->
      match element.TryGetDateTimeOffset() with
      | true, moment -> Result.Ok(box moment)
      | false, _ -> wrong "an ISO 8601 date-time"
    | _ -> wrong "an ISO 8601 date-time"
  elif t = typeof<TimeSpan> then
    match element.ValueKind with
    | JsonValueKind.Number ->
      match element.TryGetInt64() with
      | true, ticks -> Result.Ok(box (TimeSpan.FromTicks ticks))
      | false, _ -> wrong "a tick count"
    | _ -> wrong "a tick count"
  elif isFSharpList t then
    match element.ValueKind with
    | JsonValueKind.Array ->
      let elementType = t.GetGenericArguments().[0]
      element.EnumerateArray()
      |> readAll (readValue elementType)
      |> Result.map (makeList elementType)
    | _ -> wrong "an array"
  elif t.IsArray && t.GetArrayRank() = 1 then
    match element.ValueKind with
    | JsonValueKind.Array ->
      let elementType = t.GetElementType()
      element.EnumerateArray()
      |> readAll (readValue elementType)
      |> Result.map (fun items ->
        let array = Array.CreateInstance(elementType, List.length items)
        items |> List.iteri (fun index item -> array.SetValue(item, index))
        box array)
    | _ -> wrong "an array"
  elif FSharpType.IsRecord t then
    let fields = FSharpType.GetRecordFields t
    fields
    |> readAll (fun field -> objectProperty element field.Name |> bind (readValue field.PropertyType))
    |> Result.map (fun values -> FSharpValue.MakeRecord(t, List.toArray values))
  elif FSharpType.IsUnion t then
    objectProperty element "case" |> bind (readValue typeof<string>) |> bind (fun caseName ->
      match FSharpType.GetUnionCases t |> Array.tryFind (fun case -> case.Name = string caseName) with
      | None -> Result.Error(UnknownCase(t.Name, string caseName))
      | Some case ->
        let fields = case.GetFields()
        match fields.Length with
        | 0 -> Result.Ok(FSharpValue.MakeUnion(case, [||]))
        | _ ->
          objectProperty element "fields" |> bind (fun fieldsElement ->
            fields
            |> readAll (fun field -> objectProperty fieldsElement field.Name |> bind (readValue field.PropertyType))
            |> Result.map (fun values -> FSharpValue.MakeUnion(case, List.toArray values))))
  else
    Result.Error(NotRepresentable(string t))

let private decode<'T> (line: string) : Result<'T, ProtocolError> =
  try
    use document = JsonDocument.Parse line
    readValue typeof<'T> document.RootElement |> Result.map (fun value -> value :?> 'T)
  with :? JsonException as ex ->
    Result.Error(NotJson ex.Message)

let encodeRequest (request: Request) : string = encode request

let decodeRequest (line: string) : Result<Request, ProtocolError> = decode line

let encodeResponse (response: Response) : string = encode response

let decodeResponse (line: string) : Result<Response, ProtocolError> = decode line
