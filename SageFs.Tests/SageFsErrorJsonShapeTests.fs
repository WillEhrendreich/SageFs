module SageFs.Tests.SageFsErrorJsonShapeTests

open System
open System.Collections
open System.Collections.Generic
open System.IO
open System.Text.Json
open Expecto
open Expecto.Flip
open Microsoft.FSharp.Reflection
open SageFs

/// One value of every `SageFsError` case, built by reflection so a new case is
/// covered the day it is added. Shared with `SageFsErrorJsonTests`.
let allErrorCases : SageFsError list =
  FSharpType.GetUnionCases(typeof<SageFsError>)
  |> Array.map (fun case ->
    let fields =
      case.GetFields()
      |> Array.map (fun f ->
        match f.PropertyType with
        | t when t = typeof<string> -> box "test"
        | t when t = typeof<int> -> box 42
        | t when t = typeof<float> -> box 1.0
        | t when t = typeof<exn> -> box (Exception "test")
        | t when t = typeof<string list> -> box ([ "a"; "b" ] : string list)
        | t when t = typeof<SessionState> -> box SessionState.Ready
        | t when t = typeof<BuildDiagnostic list> -> box ([ BuildDiagnostic.ofLine "test error" ] : BuildDiagnostic list)
        | t when t = typeof<ProjectCompatibility.UnsupportedTfmReason> -> box ProjectCompatibility.UnsupportedTfmReason.NetFramework
        | _ -> box "unknown")
    FSharpValue.MakeUnion(case, fields) :?> SageFsError)
  |> Array.toList

/// A `BuildFailed` whose diagnostics exercise every shape the record can take:
/// a fully located error, a located warning, and a line with no location or code.
let richBuildFailed : SageFsError =
  SageFsError.BuildFailed(
    1,
    [ "src/A.fs(10,5): error FS0039: The value is not defined [/p/x.fsproj]"
      "src/B.fs(3,1): warning FS3370: This construct is deprecated"
      "MSBUILD : error MSB1009: Project file does not exist." ]
    |> List.map BuildDiagnostic.ofLine)

/// Every case above, plus the rich `BuildFailed`. This is the golden file's population.
let goldenCases : SageFsError list = allErrorCases @ [ richBuildFailed ]

/// A value reachable from a serialized error that the plain `System.Text.Json`
/// serializer cannot be trusted with on every runtime: an F# union (an option
/// included), an F# record, or anything else that is not a JSON primitive, a
/// sequence of allowed things, or a string-keyed dictionary of allowed things.
type Offender = { Path: string; TypeName: string }

let private isAllowedLeaf (t: Type) =
  t = typeof<string>
  || t = typeof<bool>
  || t = typeof<char>
  || t = typeof<byte> || t = typeof<sbyte>
  || t = typeof<int16> || t = typeof<uint16>
  || t = typeof<int> || t = typeof<uint32>
  || t = typeof<int64> || t = typeof<uint64>
  || t = typeof<float32> || t = typeof<float>
  || t = typeof<decimal>
  || t = typeof<Guid>
  || t = typeof<DateTime>
  || t = typeof<DateTimeOffset>

let private isFSharpList (t: Type) =
  t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<int list>

/// Walk everything reachable from `value` and collect each thing that is not
/// allowed. Null is allowed (it is what an unwrapped `None` becomes).
let rec offenders (path: string) (value: obj) : Offender list =
  match value with
  | null -> []
  | v ->
    let t = v.GetType()
    match v with
    | _ when isAllowedLeaf t -> []
    | :? IDictionary<string, obj> as d ->
      d
      |> Seq.collect (fun kv -> offenders (sprintf "%s.%s" path kv.Key) kv.Value)
      |> Seq.toList
    | _ when FSharpType.IsUnion(t, true) && not (isFSharpList t) ->
      [ { Path = path; TypeName = t.Name } ]
    | _ when FSharpType.IsRecord(t, true) ->
      [ { Path = path; TypeName = t.Name } ]
    | :? IEnumerable as seq ->
      seq
      |> Seq.cast<obj>
      |> Seq.mapi (fun i item -> offenders (sprintf "%s[%d]" path i) item)
      |> Seq.concat
      |> Seq.toList
    | _ -> [ { Path = path; TypeName = t.Name } ]

type private SampleRecord = { Name: string; Inner: SampleUnion }
and private SampleUnion = | Leaf | Branch of int

let private goldenPath =
  Path.Combine(__SOURCE_DIRECTORY__, "SageFsError.toJson.golden.json")

let private serializeIndented (errs: SageFsError list) : string =
  let options = JsonSerializerOptions(WriteIndented = true)
  errs
  |> List.map (fun e -> box (SageFsError.toJson e))
  |> fun items -> JsonSerializer.Serialize(items, options)
  |> fun text -> text.Replace("\r\n", "\n")

[<Tests>]
let sageFsErrorJsonShapeTests =
  testList "SageFsError.toJson shape" [

    testList "the walker itself has teeth" [
      test "flags an F# record" {
        offenders "x" (box { Name = "n"; Inner = SampleUnion.Leaf })
        |> List.map (fun o -> o.Path)
        |> Expect.equal "record is refused at its own path" [ "x" ]
      }

      test "flags a bare union" {
        offenders "x" (box (SampleUnion.Branch 1))
        |> List.length
        |> Expect.equal "union is refused" 1
      }

      test "flags Some but accepts the null None boxes to" {
        offenders "x" (box (Some 1))
        |> List.length
        |> Expect.equal "Some is refused" 1
        offenders "x" (box (None: int option))
        |> Expect.isEmpty "None is null, which is fine"
      }

      test "finds a record buried in a list inside a dictionary" {
        let nested =
          dict [ "diagnostics", box [ { Name = "n"; Inner = SampleUnion.Leaf } ] ]
        offenders "fields" (box nested)
        |> List.map (fun o -> o.Path)
        |> Expect.equal "path names the exact element" [ "fields.diagnostics[0]" ]
      }

      test "accepts primitives, lists of primitives and dictionaries of them" {
        let ok =
          dict [ "s", box "a"
                 "n", box 3
                 "f", box 1.5
                 "b", box true
                 "g", box Guid.Empty
                 "l", box [ "a"; "b" ]
                 "d", box (dict [ "k", box 1 ]) ]
        offenders "fields" (box ok)
        |> Expect.isEmpty "nothing to refuse"
      }
    ]

    test "nothing reachable from toJson is an F# union or record, for every case" {
      goldenCases
      |> List.collect (fun err ->
        let json = SageFsError.toJson err
        offenders (sprintf "%s.fields" json.case) (box json.fields))
      |> List.map (fun o -> sprintf "%s is a %s" o.Path o.TypeName)
      |> Expect.equal "every field must be a JSON primitive, a sequence or a string-keyed dictionary of them" []
    }

    test "toJson output is unchanged from the golden file" {
      let golden = File.ReadAllText(goldenPath).Replace("\r\n", "\n").TrimEnd()
      serializeIndented goldenCases
      |> fun actual -> actual.TrimEnd()
      |> Expect.equal "field names and values match the snapshot taken before toFields" golden
    }
  ]
