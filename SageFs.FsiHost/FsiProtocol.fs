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

/// How a clicked getter was kept from doing harm, said on the row so "no I/O containment here" always comes with its reason.
type Containment =
  /// It ran on a thread under this syscall filter (and a deadline).
  | ContainedBy of policy: SandboxPolicy
  /// It ran on a thread under a deadline only: no syscall filter could be installed here, for this reason.
  | NotContained of why: SandboxUnavailable

/// Why a click on a "not evaluated" row was not run: the walk mode the session is in already settled it.
type MemberRefusal =
  /// Everything mode ran every getter already, so there is nothing left to click.
  | EveryGetterAlreadyRan
  /// Off mode does not open class instances, so there is no row to click.
  | ClassesAreCollapsed

/// Why a click could not be asked of a session at all.
type MemberUnavailableReason =
  /// The session's FSI lives in the worker's own process, where there is no host to contain a getter in.
  | NoIsolatedHost
  /// The session's host is gone, for this reason.
  | HostNotRunning of reason: string

/// What a click on a "not evaluated" row came to.
type MemberOutcome =
  /// The binding walked again with the clicked member run (or the reason it was not).
  | MemberShown of binding: LiveValueTree.LiveBindingValue * containment: Containment * guards: GuardOutcome
  /// The session has no binding of that name any more (a reset or a rebind between the pane and the click).
  | BindingNotFound of name: string
  /// The session's walk mode makes the click meaningless, and says so.
  | MemberRefused of refusal: MemberRefusal
  /// The click could not be put to the session, and says why.
  | MemberUnavailable of reason: MemberUnavailableReason

type Request =
  | Eval of id: int64 * code: string
  /// Evaluate a config.fsx expression (in the host, never in the daemon) and answer with the DirectoryConfig it builds.
  | EvalConfig of id: int64 * content: string
  | ReadFlag of id: int64 * name: string
  | ReadValue of id: int64 * name: string
  /// The session's bound values as an expanded, bounded tree (the dashboard's watch window). The client picks the generation.
  | ReadLiveValues of id: int64 * generation: int64
  /// Run ONE getter the live-values walk listed as "not evaluated", under containment, and answer with that binding walked
  /// again. `path` is the labels from the binding down to the member. Answered beside the session thread: a getter that
  /// never returns must never hold up an eval.
  | EvaluateMember of id: int64 * binding: string * path: string list
  /// Choose how much of a class the live-values walk may run, from now on. The host keeps it for the life of the session.
  | SetWalkMode of id: int64 * mode: LiveValueTree.WalkMode
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
  /// Hold one test for a debugger. Answered at once with the process to attach to and the ticket that releases the test.
  | AgentDebugBegin of id: int64 * test: LiveTesting.TestCase
  /// Release a held test (the editor's debugger is attached), then wait up to `park` for it to finish. Runs beside the
  /// session thread, like AgentRunTest: a test stopped on a breakpoint must not freeze evals.
  | AgentDebugContinue of id: int64 * ticket: HostAgent.TestDebug.DebugTicket * park: TimeSpan
  /// Where each named module value's reads went, and which readers ran (hot reload rule 2).
  | AgentValueReads of id: int64 * values: string list
  /// Where rule 2's reflection reads stand. Answered beside the session thread.
  | AgentReflectionReads of id: int64
  /// Switch the reflection read mode of the running app. Answered beside the session thread.
  | AgentSetReflectionMode of id: int64 * mode: SageFs.Middleware.ValueReads.ReflectionReadMode
  /// Wait until every probe has been sighted (the patched function's new body ran, or a newer save replaced it) or the
  /// bound passes, then say what is known. Answered beside the session thread: a wait must never hold up a save.
  | AgentAwaitEntries of id: int64 * probes: int64 list * bound: TimeSpan
  | Interrupt
  | Shutdown

type Response =
  | Ready of runtime: string * fsharpCore: string
  | EvalResult of id: int64 * outcome: EvalOutcome * diagnostics: FsiDiagnostic list
  | FlagResult of id: int64 * reading: FlagReading
  | ValueResult of id: int64 * reading: ValueReading
  | LiveValuesResult of id: int64 * snapshot: LiveValueTree.LiveValueSnapshot
  | MemberResult of id: int64 * outcome: MemberOutcome
  | WalkModeSet of id: int64 * mode: LiveValueTree.WalkMode
  | CheckResult of id: int64 * diagnostics: FsiDiagnostic list
  | SymbolsResult of id: int64 * diagnostics: FsiDiagnostic list * symbols: WireSymbolRef list
  | CompletionsResult of id: int64 * items: WireCompletion list
  | DescriptionResult of id: int64 * text: string
  | ConfigResult of id: int64 * outcome: ConfigOutcome
  | AgentStartResult of id: int64 * started: HostAgent.AgentStarted
  | AgentAfterEvalResult of id: int64 * report: HostAgent.AfterEvalReport
  | AgentDiscoveryResult of id: int64 * discovery: HostAgent.Discovery
  | AgentTestResult of id: int64 * result: LiveTesting.TestResult
  | AgentDebugBeginResult of id: int64 * answer: HostAgent.TestDebug.DebugBegin
  | AgentDebugContinueResult of id: int64 * progress: HostAgent.TestDebug.DebugProgress
  | AgentLoadedAssembliesResult of id: int64 * names: string list
  | AgentCoverageResult of id: int64 * coverage: HostAgent.CoverageReading
  | AgentValueReadsResult of id: int64 * evidence: SageFs.Middleware.ValueReads.ValueEvidence list
  | AgentReflectionReadsResult of id: int64 * report: SageFs.Middleware.ValueReads.ReflectionReadsReport
  | AgentEntriesResult of id: int64 * reading: SageFs.Middleware.EntryProbes.EntryReading
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

/// Builds an F# list or an array of a runtime-known element type from boxed items. Reached through a generic class
/// behind an interface (not a reflected method name), so there is no lookup-by-string to go stale. One instance per
/// element type, made once when the type's plan is made.
type private ICollectionBuilder =
  abstract MakeList: ResizeArray<obj> -> obj
  abstract MakeArray: ResizeArray<obj> -> obj

type private CollectionBuilderOf<'T>() =
  interface ICollectionBuilder with
    member _.MakeList(items: ResizeArray<obj>) : obj =
      let mutable list: 'T list = []
      for index = items.Count - 1 downto 0 do
        list <- (items.[index] :?> 'T) :: list
      box list

    member _.MakeArray(items: ResizeArray<obj>) : obj =
      let array: 'T[] = Array.zeroCreate items.Count
      for index = 0 to items.Count - 1 do
        array.[index] <- items.[index] :?> 'T
      box array

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

// ---- the plan for a type: worked out ONCE, then read and written through ---------------------
//
// Reflection answers (is this a record? which fields? which union case has this name? how do I build one?) are the
// same for every value of a type, and a live-testing reply carries hundreds of thousands of values. So each type is
// examined exactly once into a `Plan`, cached by type, and every later value is read or written through the plan:
// no `FSharpType.*` lookup, no `UnionCaseInfo.Name`, no `GetFields()`, no `MakeGenericType` per value.

/// What the codec does for one type. The case order is the order the old per-value checks ran in, so a type is
/// the same kind of thing as before.
type private Plan =
  | Text
  | Int
  | Long
  | Flag
  /// A DateTimeOffset, on the wire as its round-trip ("O") text.
  | Moment
  /// A TimeSpan, on the wire as its tick count.
  | Duration
  | ListOf of element: PlanSlot * builder: ICollectionBuilder
  | ArrayOf of element: PlanSlot * builder: ICollectionBuilder
  | RecordOf of RecordPlan
  | UnionOf of UnionPlan
  /// A type the codec cannot carry (`checkSupported` names it). Reached only if a value of it is met.
  | Unrepresentable of Type

/// A plan that may not be finished yet: a recursive type (a node with child nodes) points back at its own slot, which
/// is filled in when the type's plan is complete. Every slot is complete before any plan is published.
and private PlanSlot() =
  member val Plan: Plan = Text with get, set

/// One field of a record or union case: its name in every form the codec needs it, and the plan of its type.
and private FieldPlan =
  { FieldName: string
    /// The name as UTF-8, for looking the property up in a parsed document without re-encoding it each time.
    FieldUtf8: byte[]
    /// The name already escaped for the writer.
    FieldEncoded: JsonEncodedText
    FieldSlot: PlanSlot }

and private RecordPlan =
  { RecordFields: FieldPlan[]
    ReadFields: obj -> obj[]
    MakeRecord: obj[] -> obj }

and private CasePlan =
  { CaseName: string
    CaseEncoded: JsonEncodedText
    CaseFields: FieldPlan[]
    ReadCaseFields: obj -> obj[]
    MakeCase: obj[] -> obj }

and private UnionPlan =
  { UnionTypeName: string
    /// The case a value is, by its tag.
    TagOf: obj -> int
    /// Indexed by tag.
    CasesByTag: CasePlan[]
    CasesByName: System.Collections.Generic.Dictionary<string, CasePlan> }

let private planCache = System.Collections.Concurrent.ConcurrentDictionary<Type, PlanSlot>()
let private planBuildLock = obj ()

let private caseProperty = JsonEncodedText.Encode "case"
let private fieldsProperty = JsonEncodedText.Encode "fields"
let private caseUtf8 = Encoding.UTF8.GetBytes "case"
let private fieldsUtf8 = Encoding.UTF8.GetBytes "fields"
let private noValues: obj[] = [||]

let private collectionBuilderFor (elementType: Type) : ICollectionBuilder =
  Activator.CreateInstance(typedefof<CollectionBuilderOf<_>>.MakeGenericType elementType) :?> ICollectionBuilder

let rec private slotWithin (building: System.Collections.Generic.Dictionary<Type, PlanSlot>) (t: Type) : PlanSlot =
  match planCache.TryGetValue t with
  | true, finished -> finished
  | false, _ ->
    match building.TryGetValue t with
    | true, unfinished -> unfinished
    | false, _ ->
      let slot = PlanSlot()
      building.[t] <- slot
      slot.Plan <- planOf building t
      slot

and private fieldPlan (building: System.Collections.Generic.Dictionary<Type, PlanSlot>) (field: System.Reflection.PropertyInfo) : FieldPlan =
  { FieldName = field.Name
    FieldUtf8 = Encoding.UTF8.GetBytes field.Name
    FieldEncoded = JsonEncodedText.Encode field.Name
    FieldSlot = slotWithin building field.PropertyType }

and private planOf (building: System.Collections.Generic.Dictionary<Type, PlanSlot>) (t: Type) : Plan =
  if t = typeof<string> then Text
  elif t = typeof<int> then Int
  elif t = typeof<int64> then Long
  elif t = typeof<bool> then Flag
  elif t = typeof<DateTimeOffset> then Moment
  elif t = typeof<TimeSpan> then Duration
  elif isFSharpList t then
    let elementType = t.GetGenericArguments().[0]
    ListOf(slotWithin building elementType, collectionBuilderFor elementType)
  elif t.IsArray && t.GetArrayRank() = 1 then
    let elementType = t.GetElementType()
    ArrayOf(slotWithin building elementType, collectionBuilderFor elementType)
  elif FSharpType.IsRecord t then
    RecordOf
      { RecordFields = FSharpType.GetRecordFields t |> Array.map (fieldPlan building)
        ReadFields = FSharpValue.PreComputeRecordReader t
        MakeRecord = FSharpValue.PreComputeRecordConstructor t }
  elif FSharpType.IsUnion t then
    let cases =
      FSharpType.GetUnionCases t
      |> Array.sortBy (fun case -> case.Tag)
      |> Array.map (fun case ->
        let fields = case.GetFields()
        { CaseName = case.Name
          CaseEncoded = JsonEncodedText.Encode case.Name
          CaseFields = fields |> Array.map (fieldPlan building)
          ReadCaseFields =
            (match fields.Length with
             | 0 -> fun _ -> noValues
             | _ -> FSharpValue.PreComputeUnionReader case)
          MakeCase = FSharpValue.PreComputeUnionConstructor case })
    let byName = System.Collections.Generic.Dictionary<string, CasePlan>(StringComparer.Ordinal)
    // The first case of a name wins, as the old linear search did (case names are unique, so this only states it).
    for case in cases do
      byName.TryAdd(case.CaseName, case) |> ignore
    UnionOf
      { UnionTypeName = t.Name
        TagOf = FSharpValue.PreComputeUnionTagReader t
        CasesByTag = cases
        CasesByName = byName }
  else
    Unrepresentable t

/// The finished plan of a type. A plan, once published, is never changed, so readers need no lock; building is
/// serialized so two threads never build (or half-publish) the same recursive type.
let private slotFor (t: Type) : PlanSlot =
  match planCache.TryGetValue t with
  | true, slot -> slot
  | false, _ ->
    lock planBuildLock (fun () ->
      let building = System.Collections.Generic.Dictionary<Type, PlanSlot>()
      let slot = slotWithin building t
      for entry in building do
        planCache.[entry.Key] <- entry.Value
      slot)

let rec private writePlan (writer: Utf8JsonWriter) (plan: Plan) (value: obj) : unit =
  match plan with
  | Text -> writer.WriteStringValue(value :?> string)
  | Int -> writer.WriteNumberValue(value :?> int)
  | Long -> writer.WriteNumberValue(value :?> int64)
  | Flag -> writer.WriteBooleanValue(value :?> bool)
  // The round-trip ("O") format keeps every tick and the offset.
  | Moment ->
    writer.WriteStringValue((value :?> DateTimeOffset).ToString("O", System.Globalization.CultureInfo.InvariantCulture))
  // A TimeSpan travels as its tick count: exact, and independent of culture.
  | Duration -> writer.WriteNumberValue((value :?> TimeSpan).Ticks)
  | ListOf(element, _)
  | ArrayOf(element, _) ->
    let elementPlan = element.Plan
    writer.WriteStartArray()
    for item in (value :?> System.Collections.IEnumerable) do
      writePlan writer elementPlan item
    writer.WriteEndArray()
  | RecordOf record ->
    let values = record.ReadFields value
    writer.WriteStartObject()
    for index in 0 .. record.RecordFields.Length - 1 do
      let field = record.RecordFields.[index]
      writer.WritePropertyName field.FieldEncoded
      writePlan writer field.FieldSlot.Plan values.[index]
    writer.WriteEndObject()
  | UnionOf union ->
    let case = union.CasesByTag.[union.TagOf value]
    writer.WriteStartObject()
    writer.WriteString(caseProperty, case.CaseEncoded)
    if case.CaseFields.Length > 0 then
      let values = case.ReadCaseFields value
      writer.WritePropertyName fieldsProperty
      writer.WriteStartObject()
      for index in 0 .. case.CaseFields.Length - 1 do
        let field = case.CaseFields.[index]
        writer.WritePropertyName field.FieldEncoded
        writePlan writer field.FieldSlot.Plan values.[index]
      writer.WriteEndObject()
    writer.WriteEndObject()
  | Unrepresentable t ->
    invalidOp (sprintf "protocol type %s is not representable on the wire" t.FullName)

let private encode<'T> (value: 'T) : string =
  let buffer = System.Buffers.ArrayBufferWriter<byte>()
  use writer = new Utf8JsonWriter(buffer)
  writePlan writer (slotFor typeof<'T>).Plan (box value)
  writer.Flush()
  Encoding.UTF8.GetString buffer.WrittenSpan

/// Where a read records why it stopped. The first error is the only one: once it is set the readers return at once, so
/// the error reported is the first one in document order, as it was when every step returned a Result. A value read
/// after the error is set is never used. One per decoded message.
type private ReadOutcome() =
  member val Verdict: Result<unit, ProtocolError> = Result.Ok() with get, set

/// What a stopped read returns in place of a value. Nothing reads it: the caller sees the stopped outcome first.
let private nothing: obj = Unchecked.defaultof<obj>

let private stop (outcome: ReadOutcome) (error: ProtocolError) : obj =
  outcome.Verdict <- Result.Error error
  nothing

let private wrongShape (outcome: ReadOutcome) (element: JsonElement) (expected: string) : obj =
  stop outcome (WrongShape(expected, string element.ValueKind))

/// Whether a read has stopped. The readers ask it between steps.
let inline private hasStopped (outcome: ReadOutcome) =
  match outcome.Verdict with
  | Result.Error _ -> true
  | Result.Ok _ -> false

/// The named property of an object, or a stopped read (and a default element) saying why.
let private propertyOf (outcome: ReadOutcome) (element: JsonElement) (utf8Name: byte[]) (name: string) : JsonElement =
  match element.ValueKind with
  | JsonValueKind.Object ->
    let mutable value = Unchecked.defaultof<JsonElement>
    match element.TryGetProperty(ReadOnlySpan<byte>(utf8Name), &value) with
    | true -> value
    | false ->
      stop outcome (MissingField name) |> ignore
      value
  | kind ->
    stop outcome (WrongShape("an object", string kind)) |> ignore
    Unchecked.defaultof<JsonElement>

let rec private readPlan (outcome: ReadOutcome) (plan: Plan) (element: JsonElement) : obj =
  match plan with
  | Text ->
    match element.ValueKind with
    | JsonValueKind.String ->
      match element.GetString() with
      | null -> wrongShape outcome element "a string"
      | text -> box text
    | _ -> wrongShape outcome element "a string"
  | Int ->
    match element.ValueKind with
    | JsonValueKind.Number ->
      match element.TryGetInt32() with
      | true, n -> box n
      | false, _ -> wrongShape outcome element "an int"
    | _ -> wrongShape outcome element "an int"
  | Long ->
    match element.ValueKind with
    | JsonValueKind.Number ->
      match element.TryGetInt64() with
      | true, n -> box n
      | false, _ -> wrongShape outcome element "an int64"
    | _ -> wrongShape outcome element "an int64"
  | Flag ->
    match element.ValueKind with
    | JsonValueKind.True -> box true
    | JsonValueKind.False -> box false
    | _ -> wrongShape outcome element "a bool"
  | Moment ->
    match element.ValueKind with
    | JsonValueKind.String ->
      match element.TryGetDateTimeOffset() with
      | true, moment -> box moment
      | false, _ -> wrongShape outcome element "an ISO 8601 date-time"
    | _ -> wrongShape outcome element "an ISO 8601 date-time"
  | Duration ->
    match element.ValueKind with
    | JsonValueKind.Number ->
      match element.TryGetInt64() with
      | true, ticks -> box (TimeSpan.FromTicks ticks)
      | false, _ -> wrongShape outcome element "a tick count"
    | _ -> wrongShape outcome element "a tick count"
  | ListOf(elementSlot, builder) ->
    match element.ValueKind with
    | JsonValueKind.Array ->
      let items = readItems outcome elementSlot element
      match hasStopped outcome with
      | true -> nothing
      | false -> builder.MakeList items
    | _ -> wrongShape outcome element "an array"
  | ArrayOf(elementSlot, builder) ->
    match element.ValueKind with
    | JsonValueKind.Array ->
      let items = readItems outcome elementSlot element
      match hasStopped outcome with
      | true -> nothing
      | false -> builder.MakeArray items
    | _ -> wrongShape outcome element "an array"
  | RecordOf record ->
    let values = readFields outcome record.RecordFields element
    match hasStopped outcome with
    | true -> nothing
    | false -> record.MakeRecord values
  | UnionOf union ->
    let caseElement = propertyOf outcome element caseUtf8 "case"
    match hasStopped outcome with
    | true -> nothing
    | false ->
      match caseElement.ValueKind with
      | JsonValueKind.String ->
        match caseElement.GetString() with
        | null -> wrongShape outcome caseElement "a string"
        | caseName ->
          match union.CasesByName.TryGetValue caseName with
          | false, _ -> stop outcome (UnknownCase(union.UnionTypeName, caseName))
          | true, case ->
            match case.CaseFields.Length with
            | 0 -> case.MakeCase noValues
            | _ ->
              let fieldsElement = propertyOf outcome element fieldsUtf8 "fields"
              match hasStopped outcome with
              | true -> nothing
              | false ->
                let values = readFields outcome case.CaseFields fieldsElement
                match hasStopped outcome with
                | true -> nothing
                | false -> case.MakeCase values
      | _ -> wrongShape outcome caseElement "a string"
  | Unrepresentable t -> stop outcome (NotRepresentable(string t))

/// The elements of a JSON array, read in order into one list of boxed items. If one cannot be read the outcome says
/// which, and the items so far are not to be used. The element plan is looked up once per array.
and private readItems (outcome: ReadOutcome) (elementSlot: PlanSlot) (array: JsonElement) : ResizeArray<obj> =
  let elementPlan = elementSlot.Plan
  let items = ResizeArray<obj>(array.GetArrayLength())
  let mutable enumerator = array.EnumerateArray().GetEnumerator()
  while not (hasStopped outcome) && enumerator.MoveNext() do
    let item = readPlan outcome elementPlan enumerator.Current
    if not (hasStopped outcome) then
      items.Add item
  items

/// Each field of `fields`, read from `parent` in order, into one array; stops at the first field that cannot be read.
and private readFields (outcome: ReadOutcome) (fields: FieldPlan[]) (parent: JsonElement) : obj[] =
  let values: obj[] = Array.zeroCreate fields.Length
  let mutable index = 0
  while index < fields.Length && not (hasStopped outcome) do
    let field = fields.[index]
    let child = propertyOf outcome parent field.FieldUtf8 field.FieldName
    if not (hasStopped outcome) then
      values.[index] <- readPlan outcome field.FieldSlot.Plan child
    index <- index + 1
  values

let private decode<'T> (line: string) : Result<'T, ProtocolError> =
  try
    use document = JsonDocument.Parse line
    let outcome = ReadOutcome()
    let value = readPlan outcome (slotFor typeof<'T>).Plan document.RootElement
    match outcome.Verdict with
    | Result.Ok _ -> Result.Ok(value :?> 'T)
    | Result.Error error -> Result.Error error
  with :? JsonException as ex ->
    Result.Error(NotJson ex.Message)

let encodeRequest (request: Request) : string = encode request

let decodeRequest (line: string) : Result<Request, ProtocolError> = decode line

let encodeResponse (response: Response) : string = encode response

let decodeResponse (line: string) : Result<Response, ProtocolError> = decode line

/// What a click on a "not evaluated" row means in each walk mode. A click only has something to do in Safe mode: in Everything
/// the getters already ran, and in Off the class is not open. Pure, so the host's decision is testable without a host.
module MemberClick =
  let judge (mode: LiveValueTree.WalkMode) : Result<unit, MemberRefusal> =
    match mode with
    | LiveValueTree.WalkMode.Safe -> Result.Ok()
    | LiveValueTree.WalkMode.Everything -> Result.Error EveryGetterAlreadyRan
    | LiveValueTree.WalkMode.Off -> Result.Error ClassesAreCollapsed

  let describeRefusal (refusal: MemberRefusal) : string =
    match refusal with
    | EveryGetterAlreadyRan -> "every getter already ran, because the walk is in Everything mode"
    | ClassesAreCollapsed -> "class instances are not opened, because the walk is in Off mode"

  let describeUnavailable (reason: MemberUnavailableReason) : string =
    match reason with
    | NoIsolatedHost -> "this session runs F# Interactive inside the worker, so a getter cannot be run under containment"
    | HostNotRunning why -> sprintf "the session's host is gone: %s" why

/// What the host process refuses, and how it says so. Pure decisions, plus the one file read at the edge
/// (`readArgsFile`). Lives here, not in Program.fs, because Program.fs needs the F# compiler service and
/// so cannot be linked into the test project; this file is already compiled into SageFs.Core.
module HostLimits =

  /// How many requests may wait for the session thread. The parent runs one request at a time per
  /// caller, so a queue anywhere near this deep means the session thread is stuck behind a long
  /// evaluation and the caller is piling up work. Past it, requests are refused with a reason
  /// instead of growing memory without bound or silently blocking the reader thread.
  [<Literal>]
  let EvalQueueCapacity = 256

  /// The numbers behind a refused request, so the message can say how full the queue was.
  type QueueRefusal = { Capacity: int; Pending: int }

  type Admission =
    | Admitted
    | Refused of QueueRefusal

  /// Whether one more request fits. The reader thread is the only producer, so the pending count can
  /// only fall between this decision and the add: an admitted request never blocks.
  let admit (capacity: int) (pending: int) : Admission =
    match pending < capacity with
    | true -> Admitted
    | false -> Refused { Capacity = capacity; Pending = pending }

  /// Which kind of reply the refused caller is waiting for.
  type Asked =
    | AskedEval
    | AskedOther

  let describeRefusal (refusal: QueueRefusal) : string =
    sprintf
      "the FSI host's request queue is full (%d pending, capacity %d): the session thread is busy with earlier work. Retry when it finishes, or interrupt the running evaluation."
      refusal.Pending
      refusal.Capacity

  /// The reply for a refused request. An eval gets a failed EvalResult; every other request gets a
  /// refusal carrying its id (the client turns it into a "host gone" reason). Either way the caller
  /// is answered, never left waiting.
  let refusalResponse (asked: Asked) (id: int64) (refusal: QueueRefusal) : Response =
    match asked with
    | AskedEval -> EvalResult(id, EvalFailed(describeRefusal refusal), [])
    | AskedOther -> AgentRefused(id, describeRefusal refusal)

  /// Why one line of the args file is not a valid argument.
  type BadLineReason =
    | ControlCharacter
    | BlankButNotEmpty
    | OptionInProgramNameSlot
    | NotAnOption

  /// Why the args file could not be turned into FSI arguments. Every case names the file.
  type ArgsFileError =
    | ArgsFileMissing of path: string
    | ArgsFileUnreadable of path: string * reason: string
    | ArgsFileEmpty of path: string
    | ArgsFileBadLine of path: string * line: int * text: string * reason: BadLineReason

  let private describeBadLine (reason: BadLineReason) : string =
    match reason with
    | ControlCharacter -> "it contains a control character"
    | BlankButNotEmpty -> "it is blank but not empty"
    | OptionInProgramNameSlot -> "the first argument is the program name, but this is an option"
    | NotAnOption -> "an argument after the program name must be an option starting with '-'"

  let describeArgsFileError (error: ArgsFileError) : string =
    match error with
    | ArgsFileMissing path -> sprintf "args file '%s' does not exist" path
    | ArgsFileUnreadable(path, reason) -> sprintf "args file '%s' could not be read: %s" path reason
    | ArgsFileEmpty path -> sprintf "args file '%s' has no arguments" path
    | ArgsFileBadLine(path, line, text, reason) ->
      sprintf "args file '%s', line %d: '%s' is not a valid argument: %s" path line text (describeBadLine reason)

  let private hasControlCharacter (text: string) = text |> Seq.exists Char.IsControl

  /// Check every line of an args file and return the arguments: blank lines are dropped, the first
  /// argument is the program name, the rest are options. Line numbers are one-based and count blank
  /// lines, so they match what an editor shows.
  let validateArgs (path: string) (lines: string[]) : Result<string list, ArgsFileError> =
    let judge (line: int, text: string) (accepted: Result<string list, ArgsFileError>) =
      match accepted with
      | Result.Error _ -> accepted
      | Result.Ok soFar ->
        let bad reason = Result.Error(ArgsFileBadLine(path, line, text, reason))
        match text.Length = 0 with
        | true -> accepted
        | false ->
          match hasControlCharacter text, String.IsNullOrWhiteSpace text, soFar.IsEmpty, text.StartsWith '-' with
          | true, _, _, _ -> bad ControlCharacter
          | false, true, _, _ -> bad BlankButNotEmpty
          | false, false, true, true -> bad OptionInProgramNameSlot
          | false, false, false, false -> bad NotAnOption
          | false, false, _, _ -> Result.Ok(soFar @ [ text ])
    let judged =
      lines
      |> Array.mapi (fun index text -> (index + 1, text))
      |> Array.fold (fun accepted numbered -> judge numbered accepted) (Result.Ok [])
    match judged with
    | Result.Ok [] -> Result.Error(ArgsFileEmpty path)
    | other -> other

  /// Read and validate the args file. The one IO of this module: every way the read can fail comes
  /// back as an error naming the file.
  let readArgsFile (path: string) : Result<string list, ArgsFileError> =
    match File.Exists path, Directory.Exists path with
    | false, true -> Result.Error(ArgsFileUnreadable(path, "it is a directory"))
    | false, false -> Result.Error(ArgsFileMissing path)
    | true, _ ->
      match (try Result.Ok(File.ReadAllLines path) with ex -> Result.Error(ArgsFileUnreadable(path, ex.Message))) with
      | Result.Error error -> Result.Error error
      | Result.Ok lines -> validateArgs path lines
