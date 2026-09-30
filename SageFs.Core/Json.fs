namespace SageFs

open System.Collections.Concurrent
open System.Text.Json
open System.Text.Json.Serialization

/// What a JSON consumer needs to be told about key spelling.
[<RequireQualifiedAccess>]
type JsonNaming =
  /// Keys exactly as the F# field or property is written.
  | AsWritten
  | CamelCase
  | SnakeCase

[<RequireQualifiedAccess>]
type JsonLayout =
  | Compact
  | Indented

/// Whether a null (a `None`, a null string) is written or left out.
[<RequireQualifiedAccess>]
type JsonNulls =
  | Write
  | Omit

/// How an F# union is written. Both carry the F# converter, so the text is the same on
/// every runtime; they differ only in shape.
[<RequireQualifiedAccess>]
type JsonUnions =
  /// The F# converter's own default encoding.
  | Standard
  /// `{"type": case, "value": fields}`, the worker protocol's encoding. Daemon and worker
  /// must agree byte for byte, so it is its own named choice and not a tweak.
  | AdjacentTagged

/// One complete answer to "how is this value written". A closed set of small choices, so a
/// call site names what it needs and cannot build an options object that disagrees with the
/// others.
type JsonProfile =
  { Naming: JsonNaming
    Layout: JsonLayout
    Nulls: JsonNulls
    Unions: JsonUnions }

/// The one place SageFs serializes and deserializes JSON.
///
/// .NET 11's System.Text.Json writes an F# union and .NET 10's throws "F# discriminated union
/// serialization is not supported", so a payload that worked on the runtime its author used
/// broke on the other, and the net10 tool asset is the one most users install. Sixteen
/// separate options objects (six with the F# converter at differing encodings, ten without)
/// each decided that for themselves. Every profile here carries the F# converter, which
/// handles unions, options and records itself, so the same value produces the same text on
/// every runtime.
///
/// A difference that really is per runtime is handled in `adjustForRuntime` and nowhere else,
/// and the golden-text tests run in both the net11 and the net10 test tiers to show there is
/// none left.
module Json =

  /// Keys as written, compact, nulls written, the F# converter's default union encoding.
  let standard : JsonProfile =
    { Naming = JsonNaming.AsWritten
      Layout = JsonLayout.Compact
      Nulls = JsonNulls.Write
      Unions = JsonUnions.Standard }

  let camelCase : JsonProfile = { standard with Naming = JsonNaming.CamelCase }

  /// The Jupyter wire.
  let snakeCase : JsonProfile = { standard with Naming = JsonNaming.SnakeCase }

  /// The daemon-to-worker wire: camelCase keys, adjacent-tag unions.
  let workerWire : JsonProfile =
    { standard with
        Naming = JsonNaming.CamelCase
        Unions = JsonUnions.AdjacentTagged }

  let indented (profile: JsonProfile) : JsonProfile = { profile with Layout = JsonLayout.Indented }

  let omitNulls (profile: JsonProfile) : JsonProfile = { profile with Nulls = JsonNulls.Omit }

  /// The one place a runtime-specific difference is handled. Empty on purpose: with the F#
  /// converter on every profile there is no known difference left between .NET 10 and 11, and
  /// the golden tests are what would show a new one.
  let private adjustForRuntime (options: JsonSerializerOptions) : JsonSerializerOptions = options

  let private build (profile: JsonProfile) : JsonSerializerOptions =
    let options = JsonSerializerOptions()
    options.PropertyNamingPolicy <-
      match profile.Naming with
      | JsonNaming.AsWritten -> null
      | JsonNaming.CamelCase -> JsonNamingPolicy.CamelCase
      | JsonNaming.SnakeCase -> JsonNamingPolicy.SnakeCaseLower
    options.WriteIndented <-
      match profile.Layout with
      | JsonLayout.Compact -> false
      | JsonLayout.Indented -> true
    options.DefaultIgnoreCondition <-
      match profile.Nulls with
      | JsonNulls.Write -> JsonIgnoreCondition.Never
      | JsonNulls.Omit -> JsonIgnoreCondition.WhenWritingNull
    let converter =
      match profile.Unions with
      | JsonUnions.Standard -> JsonFSharpConverter()
      | JsonUnions.AdjacentTagged ->
        JsonFSharpConverter(JsonUnionEncoding.AdjacentTag, unionTagName = "type", unionFieldsName = "value")
    options.Converters.Add converter
    adjustForRuntime options

  let private cache = ConcurrentDictionary<JsonProfile, JsonSerializerOptions>()

  /// The options for a profile, for the few callers (ASP.NET result helpers) that need an
  /// options object. The same instance every time, so two call sites never disagree.
  let optionsOf (profile: JsonProfile) : JsonSerializerOptions =
    cache.GetOrAdd(profile, build)

  let serialize<'T> (profile: JsonProfile) (value: 'T) : string =
    JsonSerializer.Serialize(value, optionsOf profile)

  /// A bad payload is an expected input, so the failure is a value that says why.
  let deserialize<'T> (profile: JsonProfile) (text: string) : Result<'T, string> =
    try
      let value = JsonSerializer.Deserialize<'T>(text, optionsOf profile)
      match isNull (box value) with
      | true -> Error "the JSON was null"
      | false -> Ok value
    with
    | :? JsonException as ex -> Error ex.Message
    | :? System.NotSupportedException as ex -> Error ex.Message
    | :? System.ArgumentException as ex -> Error ex.Message
