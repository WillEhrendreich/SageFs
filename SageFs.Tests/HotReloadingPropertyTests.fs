module SageFs.Tests.HotReloadingPropertyTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Middleware.HotReloading

// ── Helpers ──────────────────────────────────────────────────────────────

let private emptyState: State = {
  Methods = Map.empty
  LastAssembly = None
  LastOpenModules = []
  ProjectAssemblies = []
  AssemblyLoadErrors = []
  LiveTestInitDone = false
}

let private pick (gen: Gen<'a>) = (Gen.sample 1 gen).[0]

// ── FsCheck Generators ──────────────────────────────────────────────────

let private genIdent =
  Gen.elements [
    "f"; "g"; "handler"; "run"; "processItem"; "getValue"; "compute"
  ]

let private genModifier =
  Gen.elements [
    ""; "private "; "internal "; "inline "; "rec "; "private inline "
  ]

let private genFuncParams =
  Gen.elements [
    "x"; "x y"; "(x: int)"; "()"; "(a, b)"; "(ctx: HttpContext)"; "x (y: string)"
  ]

let private genValueRhs =
  Gen.elements [
    "42"; "\"hello\""; "[]"; "[1; 2; 3]"; "Map.empty"; "None"; "true"; "0.0"
  ]

let private genFuncBody =
  Gen.elements ["x + 1"; "42"; "sprintf \"%d\" x"; "x"; "()"]

// ── Extended Edge Case Tests ────────────────────────────────────────────

let isTopLevelFunctionBindingExtended = testList "isTopLevelFunctionBinding extended" [
  testCase "private inline function" <| fun _ ->
    isTopLevelFunctionBinding "let private inline f x = x"
    |> Expect.isTrue "private inline"
  testCase "value with list rhs" <| fun _ ->
    isTopLevelFunctionBinding "let xs = [1; 2; 3]"
    |> Expect.isFalse "list value"
  testCase "value with record rhs" <| fun _ ->
    isTopLevelFunctionBinding "let r = { Name = \"x\" }"
    |> Expect.isFalse "record value"
  testCase "function with complex body" <| fun _ ->
    isTopLevelFunctionBinding "let handler (ctx: HttpContext) = task { return () }"
    |> Expect.isTrue "handler"
  testCase "value with None" <| fun _ ->
    isTopLevelFunctionBinding "let x = None"
    |> Expect.isFalse "None value"
  testCase "value with Some" <| fun _ ->
    isTopLevelFunctionBinding "let x = Some 42"
    |> Expect.isFalse "Some value"
  testCase "function with pipe body" <| fun _ ->
    isTopLevelFunctionBinding "let f x = x |> List.map id"
    |> Expect.isTrue "pipe body"
]

let isStaticMemberFunctionExtended = testList "isStaticMemberFunction extended" [
  testCase "static member with type annotation return" <| fun _ ->
    isStaticMemberFunction "  static member Parse (s: string) : int option = None"
    |> Expect.isTrue "typed return"
  testCase "empty string" <| fun _ ->
    isStaticMemberFunction "" |> Expect.isFalse "empty"
  testCase "regular let is not static" <| fun _ ->
    isStaticMemberFunction "let f x = x" |> Expect.isFalse "let"
]

let getOpenModulesTests = testList "getOpenModules" [
  testCase "extracts single open" <| fun _ ->
    (getOpenModules "open MyModule" emptyState).LastOpenModules
    |> Expect.equal "one" ["MyModule"]
  testCase "extracts multiple opens" <| fun _ ->
    (getOpenModules "open A\nopen B\nopen C" emptyState).LastOpenModules
    |> Expect.containsAll "all" ["A"; "B"; "C"]
  testCase "ignores non-open lines" <| fun _ ->
    (getOpenModules "let x = 42\nopen Real\ntype Foo = int" emptyState).LastOpenModules
    |> Expect.equal "only Real" ["Real"]
  testCase "appends to existing" <| fun _ ->
    let st = { emptyState with LastOpenModules = ["Old"] }
    (getOpenModules "open New" st).LastOpenModules
    |> Expect.containsAll "both" ["Old"; "New"]
  testCase "deduplicates" <| fun _ ->
    let st = { emptyState with LastOpenModules = ["A"] }
    (getOpenModules "open A\nopen B" st).LastOpenModules
    |> Expect.hasLength "no dupes" 2
  testCase "empty code" <| fun _ ->
    (getOpenModules "" emptyState).LastOpenModules
    |> Expect.isEmpty "empty"
  testCase "spaces only" <| fun _ ->
    (getOpenModules "   " emptyState).LastOpenModules
    |> Expect.isEmpty "spaces"
  testCase "dotted namespace" <| fun _ ->
    (getOpenModules "open System.Collections.Generic" emptyState).LastOpenModules
    |> Expect.equal "dotted" ["System.Collections.Generic"]
]

// ── Property-Based Tests (FsCheck) ─────────────────────────────────────

let propertyTests = testList "HotReloading properties" [
  testProperty "function bindings are always detected" <| fun () ->
    let line =
      sprintf "let %s%s %s = %s"
        (pick genModifier) (pick genIdent) (pick genFuncParams) (pick genFuncBody)
    isTopLevelFunctionBinding line
    |> Expect.isTrue (sprintf "detect: %s" line)

  testProperty "value bindings are never detected as functions" <| fun () ->
    let modifier = Gen.elements [""; "private "; "internal "] |> pick
    let line = sprintf "let %s%s = %s" modifier (pick genIdent) (pick genValueRhs)
    isTopLevelFunctionBinding line
    |> Expect.isFalse (sprintf "reject: %s" line)

  testProperty "injectNoInlining is identity for value-only code" <| fun () ->
    let code = sprintf "let %s = %s" (pick genIdent) (pick genValueRhs)
    injectNoInlining code |> Expect.equal "unchanged" code

  testProperty "injectNoInlining adds exactly one attribute per function" <| fun () ->
    let funcCount = pick (Gen.choose (1, 4))
    let valCount = pick (Gen.choose (0, 3))
    let funcs =
      [ for i in 1..funcCount ->
          sprintf "let fn%d %s = ()" i (pick genFuncParams) ]
    let vals =
      [ for i in 1..valCount -> sprintf "let v%d = %d" i i ]
    let code = (funcs @ vals) |> String.concat "\n"
    let result = injectNoInlining code
    let attrCount = result.Split("[<MethodImpl") |> Array.length |> fun n -> n - 1
    attrCount |> Expect.equal (sprintf "%d attrs for %d funcs" funcCount funcCount) funcCount

  testProperty "injectNoInlining preserves all original lines" <| fun () ->
    let originalLine =
      sprintf "let %s %s = %s" (pick genIdent) (pick genFuncParams) (pick genFuncBody)
    injectNoInlining originalLine
    |> Expect.stringContains "preserved" originalLine

  testProperty "function binding and static member are mutually exclusive" <| fun () ->
    let name = pick genIdent
    let parms = pick genFuncParams
    isStaticMemberFunction (sprintf "let %s %s = ()" name parms)
    |> Expect.isFalse "let is not static"
    isTopLevelFunctionBinding (sprintf "static member %s %s = ()" name parms)
    |> Expect.isFalse "static is not let"

  testProperty "getOpenModules preserves existing modules" <| fun () ->
    let existing =
      Gen.elements ["A"; "B"; "C"; "D"; "E"]
      |> Gen.listOf |> Gen.map List.distinct |> pick
    let newMod = Gen.elements ["X"; "Y"; "Z"] |> pick
    let result =
      (getOpenModules (sprintf "open %s" newMod) { emptyState with LastOpenModules = existing })
        .LastOpenModules
    existing |> List.iter (fun m ->
      result |> List.contains m
      |> Expect.isTrue (sprintf "preserve %s" m))

  testProperty "getOpenModules never creates duplicates" <| fun () ->
    let mods =
      Gen.elements ["A"; "B"; "C"]
      |> Gen.listOf |> Gen.map List.distinct |> pick
    let code = mods |> List.map (sprintf "open %s") |> String.concat "\n"
    let result =
      (getOpenModules code { emptyState with LastOpenModules = mods }).LastOpenModules
    result |> List.distinct |> List.length
    |> Expect.equal "no dupes" result.Length
]

// ── Combined ────────────────────────────────────────────────────────────

[<Tests>]
let hotReloadingPropertyTests = testList "HotReloading property & edge-case" [
  isTopLevelFunctionBindingExtended
  isStaticMemberFunctionExtended
  getOpenModulesTests
  propertyTests
]
