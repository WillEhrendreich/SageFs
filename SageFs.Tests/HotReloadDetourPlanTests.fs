module SageFs.Tests.HotReloadDetourPlanTests

open System
open Expecto
open Expecto.Flip
open SageFs.Middleware.HotReloading

// Distinct overloads of one Math method share a Name but not an identity —
// the same shape as two FSI copies of one re-evaluated definition.
let private names = [| "Abs"; "Max"; "Min" |]

let private overloads (name: string) =
  typeof<Math>.GetMethods() |> Array.filter (fun m -> m.IsStatic && m.Name = name)

let private token (m: Method) = m.MethodInfo.MetadataToken

let private copy (name: string) (index: int) : Method =
  { MethodInfo = (overloads name).[index]; FullName = "M." + name }

let private sameDefinition (old: Method) (candidate: Method) = old.FullName = candidate.FullName

let private byName (methods: Method list) =
  methods |> List.groupBy (fun m -> m.MethodInfo.Name) |> Map.ofList

let private newGiven (known: Method list) (m: Method) =
  not (known |> List.exists (fun k -> token k = token m))

type private Step = {
  KnownBefore: Method list
  Defined: Method list
  Plan: (Method * Method) list
}

/// Replays evals the way handleNewAsmFromRepl sees them: every eval adds its
/// definitions to ONE accumulating FSI assembly (--multiemit-), every method in
/// that assembly is offered as "new", and the known methods are merged by name.
let private replay (history: int list list) : Step list =
  let pools = names |> Array.map overloads
  let step (accumulated: Method list, known: Method list, used: Map<int, int>, steps: Step list) (eval: int list) =
    let defined, used' =
      eval
      |> List.fold (fun (defs, used) i ->
        let n = abs i % names.Length
        let count = used |> Map.tryFind n |> Option.defaultValue 0
        match count < pools.[n].Length with
        | true -> defs @ [ copy names.[n] count ], Map.add n (count + 1) used
        | false -> defs, used) ([], used)
    let accumulated' = accumulated @ defined
    let plan = planDetours (newGiven known) sameDefinition accumulated' (byName known)
    accumulated', accumulated', used', steps @ [ { KnownBefore = known; Defined = defined; Plan = plan } ]
  let _, _, _, steps = history |> List.fold step ([], [], Map.empty, [])
  steps

let private isPair (a: Method) (b: Method) (plan: (Method * Method) list) =
  plan |> List.exists (fun (x, y) -> token x = token a && token y = token b)

[<Tests>]
let detourPlanTests =
  testList "HotReloading planDetours" [
    testCase "WHY — HotReloading.planDetours — a redefinition detours the old copy onto the new one because that is hot reload" <| fun _ ->
      let a1, a2 = copy "Abs" 0, copy "Abs" 1
      planDetours (newGiven [ a1 ]) sameDefinition [ a1; a2 ] (byName [ a1 ])
      |> List.map (fun (o, n) -> token o, token n)
      |> Expect.equal "old onto new" [ token a1, token a2 ]

    testCase "WHY — HotReloading.planDetours — an unrelated eval after two copies exist plans no detours between them because mutual detours spin the worker at 100% CPU forever" <| fun _ ->
      let a1, a2, m1 = copy "Abs" 0, copy "Abs" 1, copy "Max" 0
      planDetours (newGiven [ a1; a2 ]) sameDefinition [ a1; a2; m1 ] (byName [ a1; a2 ])
      |> Expect.isEmpty "nothing new was redefined"

    testProperty "WHY — HotReloading.planDetours — no eval history ever plans a detour and its reverse because the two entry points would jump to each other forever" <| fun (history: int list list) ->
      replay history
      |> List.forall (fun s -> s.Plan |> List.forall (fun (a, b) -> not (isPair b a s.Plan)))

    testProperty "WHY — HotReloading.planDetours — every detour targets a definition from this eval because only the newest code may win" <| fun (history: int list list) ->
      replay history
      |> List.forall (fun s ->
        s.Plan |> List.forall (fun (_, target) -> s.Defined |> List.exists (fun d -> token d = token target)))

    testProperty "WHY — HotReloading.planDetours — every older copy moves onto each new definition because a running app may hold any of them" <| fun (history: int list list) ->
      replay history
      |> List.forall (fun s ->
        s.Defined |> List.forall (fun d ->
          s.KnownBefore
          |> List.filter (fun k -> k.FullName = d.FullName)
          |> List.forall (fun old -> isPair old d s.Plan)))
  ]

/// A compiled method in the two-segment namespace SageFs.Tests.
let hotReloadNameProbe () = 42

[<Tests>]
let methodNameTests =
  testList "HotReloading getAllMethods names" [
    testCase "WHY — HotReloading.getAllMethods — a compiled method in a multi-segment namespace is named in reading order because detours pair it with its FSI copy by name suffix" <| fun _ ->
      getAllMethods (System.Reflection.Assembly.GetExecutingAssembly())
      |> List.filter (fun m -> m.MethodInfo.Name = "hotReloadNameProbe")
      |> List.map _.FullName
      |> Expect.equal "namespace segments in reading order" [ "SageFs.Tests.HotReloadDetourPlanTests.hotReloadNameProbe" ]
  ]
