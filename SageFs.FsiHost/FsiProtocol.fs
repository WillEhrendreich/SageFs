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

type DiagnosticSeverity =
  | DiagHidden
  | DiagInfo
  | DiagWarning
  | DiagError

type FsiDiagnostic =
  { Severity: DiagnosticSeverity
    ErrorNumber: int
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

type Request =
  | Eval of id: int64 * code: string
  | Interrupt
  | Shutdown

type Response =
  | Ready of runtime: string * fsharpCore: string
  | EvalResult of id: int64 * outcome: EvalOutcome * diagnostics: FsiDiagnostic list
  | Output of stream: OutputStream * text: string

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

let rec private isSupported (seen: Set<string>) (t: Type) : Result<unit, string> =
  // System.Type is not comparable, so key the visited set by its display name.
  match seen.Contains(string t) with
  | true -> Result.Ok()
  | false ->
    let seen = seen.Add(string t)
    let all (types: Type seq) =
      types |> Seq.fold (fun acc next -> acc |> Result.bind (fun () -> isSupported seen next)) (Result.Ok())
    if t = typeof<string> || t = typeof<int> || t = typeof<int64> || t = typeof<bool> then Result.Ok()
    elif isFSharpList t then isSupported seen (t.GetGenericArguments().[0])
    elif FSharpType.IsRecord t then FSharpType.GetRecordFields t |> Seq.map (fun f -> f.PropertyType) |> all
    elif FSharpType.IsUnion t then
      FSharpType.GetUnionCases t
      |> Seq.collect (fun case -> case.GetFields() |> Seq.map (fun f -> f.PropertyType))
      |> all
    else Result.Error(sprintf "protocol type %s is not representable on the wire" t.FullName)

/// Proves every type the protocol mentions is representable by the codec. Run at host start and in tests.
let checkSupported () : Result<unit, string> =
  [ typeof<Request>; typeof<Response> ]
  |> List.fold (fun acc t -> acc |> Result.bind (fun () -> isSupported Set.empty t)) (Result.Ok())

let rec private writeValue (writer: Utf8JsonWriter) (t: Type) (value: obj) : unit =
  if t = typeof<string> then writer.WriteStringValue(value :?> string)
  elif t = typeof<int> then writer.WriteNumberValue(value :?> int)
  elif t = typeof<int64> then writer.WriteNumberValue(value :?> int64)
  elif t = typeof<bool> then writer.WriteBooleanValue(value :?> bool)
  elif isFSharpList t then
    let elementType = t.GetGenericArguments().[0]
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

let private bind (f: 'a -> Result<'b, string>) (r: Result<'a, string>) = Result.bind f r

/// Read every element of `items` with `read`, stopping at the first Error.
let private readAll (read: 'a -> Result<obj, string>) (items: 'a seq) : Result<obj list, string> =
  items
  |> Seq.fold
       (fun acc item ->
         acc |> bind (fun parsed -> read item |> Result.map (fun value -> value :: parsed)))
       (Result.Ok [])
  |> Result.map List.rev

let private objectProperty (element: JsonElement) (name: string) : Result<JsonElement, string> =
  match element.ValueKind with
  | JsonValueKind.Object ->
    match element.TryGetProperty name with
    | true, value -> Result.Ok value
    | false, _ -> Result.Error(sprintf "missing field '%s'" name)
  | kind -> Result.Error(sprintf "expected an object with field '%s' but found %A" name kind)

let rec private readValue (t: Type) (element: JsonElement) : Result<obj, string> =
  let wrong (expected: string) = Result.Error(sprintf "expected %s for %s but found %A" expected t.Name element.ValueKind)
  if t = typeof<string> then
    match element.ValueKind with
    | JsonValueKind.String ->
      match element.GetString() with
      | null -> Result.Error "string value is null"
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
  elif isFSharpList t then
    match element.ValueKind with
    | JsonValueKind.Array ->
      let elementType = t.GetGenericArguments().[0]
      element.EnumerateArray()
      |> readAll (readValue elementType)
      |> Result.map (makeList elementType)
    | _ -> wrong "an array"
  elif FSharpType.IsRecord t then
    let fields = FSharpType.GetRecordFields t
    fields
    |> readAll (fun field -> objectProperty element field.Name |> bind (readValue field.PropertyType))
    |> Result.map (fun values -> FSharpValue.MakeRecord(t, List.toArray values))
  elif FSharpType.IsUnion t then
    objectProperty element "case" |> bind (readValue typeof<string>) |> bind (fun caseName ->
      match FSharpType.GetUnionCases t |> Array.tryFind (fun case -> case.Name = string caseName) with
      | None -> Result.Error(sprintf "unknown case '%s' of %s" (string caseName) t.Name)
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
    Result.Error(sprintf "protocol type %s is not representable on the wire" t.FullName)

let private decode<'T> (line: string) : Result<'T, string> =
  try
    use document = JsonDocument.Parse line
    readValue typeof<'T> document.RootElement |> Result.map (fun value -> value :?> 'T)
  with :? JsonException as ex ->
    Result.Error(sprintf "not valid JSON: %s" ex.Message)

let encodeRequest (request: Request) : string = encode request

let decodeRequest (line: string) : Result<Request, string> = decode line

let encodeResponse (response: Response) : string = encode response

let decodeResponse (line: string) : Result<Response, string> = decode line
