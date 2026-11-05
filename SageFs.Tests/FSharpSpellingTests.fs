/// A type and a float as an F# author spells them (`SageFs.Core/FSharpSpelling.fs`). Each fact was proved in a SageFs REPL session first.
/// A round trip is not possible (`int list` does not say which runtime type it came from), so the checks are an exhaustive table of
/// the primitive aliases, and properties: it is never empty, never carries a backtick, gives the same answer twice, and nests.
module SageFs.Tests.FSharpSpellingTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Tests.SharedGenerators

/// Every runtime type a value in the pane is likely to have, with the spelling an F# author expects.
let spellings : (Type * string) list =
  [ typeof<float>, "float"
    typeof<int>, "int"
    typeof<bool>, "bool"
    typeof<string>, "string"
    typeof<int list>, "int list"
    typeof<int option>, "int option"
    typeof<int voption>, "int voption"
    typeof<int[]>, "int[]"
    typeof<int[,]>, "int[,]"
    typeof<int list option>, "int list option"
    typeof<(int * string) list>, "(int * string) list"
    typeof<int -> int>, "int -> int"
    typeof<(int -> int) -> int>, "(int -> int) -> int"
    typeof<int * string>, "int * string"
    typeof<Map<string, int>>, "Map<string, int>"
    typeof<Set<int>>, "Set<int>"
    typeof<Result<int, string>>, "Result<int, string>"
    typeof<ResizeArray<int>>, "ResizeArray<int>"
    typeof<seq<int>>, "seq<int>"
    typeof<int list[]>, "int list[]"
    typeof<(int * int)[]>, "(int * int)[]"
    typeof<int ref>, "int ref"
    typeof<Async<int>>, "Async<int>"
    typeof<exn>, "exn"
    typeof<unit>, "unit"
    typeof<DateTime>, "DateTime" ]

/// Each primitive's runtime short name and the F# keyword for it. One row per alias, so a missing one is a missing row.
let primitives : (Type * string * string) list =
  [ typeof<bool>, "Boolean", "bool"
    typeof<byte>, "Byte", "byte"
    typeof<sbyte>, "SByte", "sbyte"
    typeof<int16>, "Int16", "int16"
    typeof<uint16>, "UInt16", "uint16"
    typeof<int>, "Int32", "int"
    typeof<uint32>, "UInt32", "uint32"
    typeof<int64>, "Int64", "int64"
    typeof<uint64>, "UInt64", "uint64"
    typeof<float32>, "Single", "float32"
    typeof<float>, "Double", "float"
    typeof<decimal>, "Decimal", "decimal"
    typeof<char>, "Char", "char"
    typeof<string>, "String", "string"
    typeof<obj>, "Object", "obj"
    typeof<unit>, "Unit", "unit"
    typeof<nativeint>, "IntPtr", "nativeint"
    typeof<unativeint>, "UIntPtr", "unativeint"
    typeof<bigint>, "BigInteger", "bigint"
    typeof<exn>, "Exception", "exn" ]

let hasBacktick (text: string) : bool = text.Contains '`'

/// Runtime names built up from a generator: primitives, wrapped in a few generic shapes, a few levels deep.
let genRuntimeName : Gen<string> =
  let leaf = Gen.elements (primitives |> List.map (fun (_, runtime, _) -> runtime))
  let rec build (depth: int) : Gen<string> =
    match depth with
    | 0 -> leaf
    | _ ->
      let inner = build (depth - 1)
      Gen.oneof
        [ leaf
          inner |> Gen.map (sprintf "FSharpList`1[%s]")
          inner |> Gen.map (sprintf "FSharpOption`1[%s]")
          inner |> Gen.map (sprintf "%s[]")
          Gen.map2 (sprintf "Tuple`2[%s,%s]") inner inner
          Gen.map2 (sprintf "FSharpMap`2[%s,%s]") inner inner
          Gen.map2 (sprintf "FSharpFunc`2[%s,%s]") inner inner ]
  Gen.sized (fun size -> build (min 4 (size / 20)))

[<Tests>]
let typeNameTests =
  testList "a type spelled as F# spells it" [

    testCase "every primitive alias: the runtime's name, and the type itself, give the F# keyword" <| fun _ ->
      for t, runtime, keyword in primitives do
        FSharpTypeName.runtimeName t |> Expect.equal (sprintf "the runtime name of %s" keyword) runtime
        FSharpTypeName.display runtime |> Expect.equal (sprintf "%s is spelled %s" runtime keyword) keyword
        FSharpTypeName.ofType t |> Expect.equal (sprintf "the type %s is spelled %s" runtime keyword) keyword

    testCase "the generic and collection types an author writes every day" <| fun _ ->
      for t, expected in spellings do
        FSharpTypeName.ofType t |> Expect.equal (sprintf "%s" (FSharpTypeName.runtimeName t)) expected

    testCase "WHY — the runtime name keeps the generic arguments, so what a tooltip shows is the whole .NET name and the display can be built from it" <| fun _ ->
      FSharpTypeName.runtimeName typeof<int list option> |> Expect.equal "arguments nest" "FSharpOption`1[FSharpList`1[Int32]]"
      FSharpTypeName.runtimeName typeof<int[,]> |> Expect.equal "an array keeps its rank" "Int32[,]"

    testCase "a generic definition with no arguments is spelled with blanks and never shows its arity tick" <| fun _ ->
      FSharpTypeName.display "FSharpList`1" |> Expect.equal "a list of something" "_ list"
      FSharpTypeName.display "FSharpMap`2" |> Expect.equal "a map of two somethings" "Map<_, _>"

    testCase "a record or a union, by its short name, is left as it is" <| fun _ ->
      FSharpTypeName.display "Rig" |> Expect.equal "a record" "Rig"
      FSharpTypeName.display "Shape" |> Expect.equal "a union" "Shape"

    testCase "WHY — text that is not a runtime type name still comes back non-empty and without a backtick, because the pane draws whatever the walk sent" <| fun _ ->
      for text in [ ""; "   "; "`"; "``"; "Foo`"; "[["; "]"; "A[B"; "A[B,]"; ",,"; "Tuple`2[Int32"; "x`1[y`2[]" ] do
        let shown = FSharpTypeName.display text
        String.IsNullOrWhiteSpace shown |> Expect.isFalse (sprintf "%A is shown as something" text)
        hasBacktick shown |> Expect.isFalse (sprintf "%A has no backtick when shown" text)

    testPropertyWithConfig propConfig "whatever it is given, it is never empty and never carries a backtick"
      (Prop.forAll (Arb.fromGen (Gen.oneof [ genRuntimeName; Gen.elements [ ""; "`"; "A`2"; "[,]"; "X[Y" ]; Gen.map string (Gen.elements [ 'a'; '`'; '['; ']'; ',' ]) ])) (fun text ->
        let shown = FSharpTypeName.display text
        not (String.IsNullOrWhiteSpace shown) && not (hasBacktick shown)))

    testPropertyWithConfig propConfig "nested generics: a name built from primitives, at any depth, shows no runtime wrapper name at all"
      (Prop.forAll (Arb.fromGen genRuntimeName) (fun runtime ->
        let shown = FSharpTypeName.display runtime
        not (hasBacktick shown) && not (shown.Contains "FSharp") && not (shown.Contains "Tuple")))

    testPropertyWithConfig propConfig "stable: spelling a spelling changes nothing"
      (Prop.forAll (Arb.fromGen genRuntimeName) (fun runtime ->
        let once = FSharpTypeName.display runtime
        FSharpTypeName.display once = once))

    testPropertyWithConfig propConfig "a list of anything is that thing's spelling followed by list, parenthesised when it is a tuple or a function"
      (Prop.forAll (Arb.fromGen genRuntimeName) (fun runtime ->
        let inner = FSharpTypeName.display runtime
        let listed = FSharpTypeName.display (sprintf "FSharpList`1[%s]" runtime)
        listed = sprintf "%s list" inner || listed = sprintf "(%s) list" inner))
  ]

[<Tests>]
let floatTests =
  testList "a float spelled as F# source spells it" [

    testCase "a whole number keeps its point, a fraction keeps its digits, and the sign and exponent survive" <| fun _ ->
      FSharpFloat.text 80.0 |> Expect.equal "80.0 and not 80" "80.0"
      FSharpFloat.text 0.25 |> Expect.equal "a fraction" "0.25"
      FSharpFloat.text -3.0 |> Expect.equal "negative" "-3.0"
      FSharpFloat.text 9.8 |> Expect.equal "the walk's own 9.8" "9.8"

    testPropertyWithConfig propConfig "every finite float reads back as the same number, and looks like a float (a point or an exponent)"
      (Prop.forAll (Arb.fromGen (Gen.map2 (fun whole frac -> float whole + float frac / 1000.0) (Gen.choose (-100000, 100000)) (Gen.choose (0, 999)))) (fun value ->
        let text = FSharpFloat.text value
        Double.Parse(text, Globalization.CultureInfo.InvariantCulture) = value && (text.Contains '.' || text.Contains 'e')))
  ]
