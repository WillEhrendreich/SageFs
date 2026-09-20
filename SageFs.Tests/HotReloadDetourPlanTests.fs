module SageFs.Tests.HotReloadDetourPlanTests

open System
open Expecto
open Expecto.Flip
open SageFs.Middleware.HotReloading
open SageFs.Middleware.HotReloadCore
open SageFs.Features.ReloadOutcome
open SageFs.Features.ReloadPlanning

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

// ── Accessor-pair atomicity ───────────────────────────────────────────────────
//
// MEASURED (hrprobe, net10.0 linux-x64, Debug, 400k warming iterations, the
// SageFs.Harmony fork's PatchTools.DetourMethod):
//
//   pair         AFTER /f=NEW-FIELD   bump wrote "+X" and the read saw it
//   getter-only  AFTER /f=NEW-FIELD   bump wrote "+X" and the read NEVER saw it
//   setter-only  AFTER /f=OLD-FIELD   bump wrote "+X" and the read NEVER saw it
//
// Redirecting one leg of a module-level `let mutable`'s accessor pair does not
// merely fail to reload — it makes every subsequent WRITE disappear, silently,
// because the write lands in one module's backing field and the read comes from
// the other's. So a binding either moves whole or does not move at all.

/// Real F#-emitted accessors: a module-level `let mutable` compiles to a
/// `get_`/`set_` pair of static methods on the module type, which is exactly the
/// shape the planner has to keep together.
let mutable detourProbeAlpha = 0

let mutable detourProbeBeta = 0

let private probeMethod (name: string) : Method =
  let m =
    System.Reflection.Assembly.GetExecutingAssembly().GetTypes()
    |> Array.collect (fun t ->
      t.GetMethods(System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.NonPublic ||| System.Reflection.BindingFlags.Static))
    |> Array.find (fun m -> m.Name = name)
  { MethodInfo = m; FullName = "App.Config." + name }

/// The same accessor seen as an OLDER copy: a different FullName prefix, the way
/// a compiled module's method and its FSI re-eval differ.
let private olderProbe (name: string) : Method =
  { probeMethod name with FullName = "Demo.App.Config." + name }

let private alphaSettable = Set.ofList [ "Demo.App.Config.detourProbeAlpha" ]

let private pairNames (plan: DetourPlan) =
  plan.Functions |> List.map (fun (o, _) -> o.FullName)

[<Tests>]
let accessorPairTests =
  testList "HotReloading accessor pair atomicity" [
    testCase "WHY — HotReloadCore.accessorRole — a module-qualified get_/set_ is recognised as one binding's two legs because that is what has to move together" <| fun _ ->
      [ accessorRole (olderProbe "get_detourProbeAlpha")
        accessorRole (olderProbe "set_detourProbeAlpha")
        accessorRole (olderProbe "hotReloadNameProbe") ]
      |> Expect.equal "getter, setter, plain — all naming the same qualified binding"
        [ AccessorRole.Getter "Demo.App.Config.detourProbeAlpha"
          AccessorRole.Setter "Demo.App.Config.detourProbeAlpha"
          AccessorRole.Plain ]

    testCase "WHY — HotReloadCore.settableBindingsOf — a binding counts as mutable when the RUNNING code has a setter for it because that is the code whose writes would be lost" <| fun _ ->
      let existing =
        [ "get_detourProbeAlpha", [ olderProbe "get_detourProbeAlpha" ]
          "set_detourProbeAlpha", [ olderProbe "set_detourProbeAlpha" ]
          "get_detourProbeBeta", [ olderProbe "get_detourProbeBeta" ] ]
        |> Map.ofList
      settableBindingsOf existing
      |> Expect.equal "only the binding with a setter" alphaSettable

    testCase "WHY — HotReloadCore.planDetourUnits — both legs of a mutable binding plan as ONE unit because redirecting one alone silently discards every later write" <| fun _ ->
      let plan =
        planDetourUnits alphaSettable
          [ olderProbe "get_detourProbeAlpha", probeMethod "get_detourProbeAlpha"
            olderProbe "set_detourProbeAlpha", probeMethod "set_detourProbeAlpha" ]
      plan.Declined |> Expect.isEmpty "a complete pair is never declined"
      plan.Functions |> Expect.isEmpty "an accessor is never planned as a loose function"
      plan.MutableBindings |> List.map _.Binding
      |> Expect.equal "one unit for the binding" [ "Demo.App.Config.detourProbeAlpha" ]

    testCase "WHY — HotReloadCore.planDetourUnits — a getter with no setter partner is DECLINED, not planned, because a half-redirected binding loses writes with no error" <| fun _ ->
      let plan =
        planDetourUnits alphaSettable
          [ olderProbe "get_detourProbeAlpha", probeMethod "get_detourProbeAlpha" ]
      plan.MutableBindings |> Expect.isEmpty "nothing is redirected for the binding"
      pairNames plan |> Expect.isEmpty "and the orphan does not leak into the function pairs"
      plan.Declined
      |> Expect.equal "declined, naming which leg was orphaned"
        [ { Binding = "Demo.App.Config.detourProbeAlpha"; Orphan = OrphanedLeg.GetterWithoutSetter } ]

    testCase "WHY — HotReloadCore.planDetourUnits — a setter with no getter partner is DECLINED too because the tear is symmetric" <| fun _ ->
      let plan =
        planDetourUnits alphaSettable
          [ olderProbe "set_detourProbeAlpha", probeMethod "set_detourProbeAlpha" ]
      plan.MutableBindings |> Expect.isEmpty "nothing is redirected for the binding"
      pairNames plan |> Expect.isEmpty "and the orphan does not leak into the function pairs"
      plan.Declined
      |> Expect.equal "declined, naming which leg was orphaned"
        [ { Binding = "Demo.App.Config.detourProbeAlpha"; Orphan = OrphanedLeg.SetterWithoutGetter } ]

    testCase "WHY — HotReloadCore.planDetourUnits — the getter of an IMMUTABLE binding is an ordinary detour because with no writer anywhere there is nothing to tear" <| fun _ ->
      let plan =
        planDetourUnits Set.empty
          [ olderProbe "get_detourProbeBeta", probeMethod "get_detourProbeBeta" ]
      plan.Declined |> Expect.isEmpty "nothing to decline"
      plan.MutableBindings |> Expect.isEmpty "not a mutable binding"
      pairNames plan |> Expect.equal "redirected as a plain function" [ "Demo.App.Config.get_detourProbeBeta" ]

    testCase "WHY — HotReloadCore.planDetourUnits — every older copy of BOTH legs travels in the same unit because a running app may hold any prior eval's copy of either" <| fun _ ->
      let olderCopy suffix name = { olderProbe name with FullName = "Demo" + suffix + ".App.Config." + name }
      let settable = Set.ofList [ "Demo.App.Config.detourProbeAlpha"; "Demo2.App.Config.detourProbeAlpha" ]
      let plan =
        planDetourUnits settable
          [ olderProbe "get_detourProbeAlpha", probeMethod "get_detourProbeAlpha"
            olderCopy "2" "get_detourProbeAlpha", probeMethod "get_detourProbeAlpha"
            olderProbe "set_detourProbeAlpha", probeMethod "set_detourProbeAlpha" ]
      // Demo.…alpha has both legs; Demo2.…alpha has only a getter, so it is declined.
      plan.MutableBindings |> List.map (fun u -> u.Binding, u.Getters.Length, u.Setters.Length)
      |> Expect.equal "the complete binding keeps both legs" [ "Demo.App.Config.detourProbeAlpha", 1, 1 ]
      plan.Declined |> List.map _.Binding
      |> Expect.equal "the incomplete one is declined by name" [ "Demo2.App.Config.detourProbeAlpha" ]

    testProperty "WHY — HotReloadCore.planDetourUnits — no settable binding is ever redirected through only one of its accessors, for ANY pair set, because that is the state that silently eats writes" <| fun (getters: int list, setters: int list) ->
      let names = [| "detourProbeAlpha"; "detourProbeBeta" |]
      let bindingOf i = "Demo.App.Config." + names.[abs i % names.Length]
      let settable = names |> Array.map (fun n -> "Demo.App.Config." + n) |> Set.ofArray
      let leg prefix i =
        let n = names.[abs i % names.Length]
        { olderProbe (prefix + n) with FullName = "Demo.App.Config." + prefix + n }, probeMethod (prefix + n)
      let plan =
        planDetourUnits settable
          ((getters |> List.map (leg "get_")) @ (setters |> List.map (leg "set_")))
      let redirected =
        plan.MutableBindings
        |> List.collect (fun u -> [ for _ in u.Getters -> u.Binding, "get" ] @ [ for _ in u.Setters -> u.Binding, "set" ])
      let bindingsTouched = redirected |> List.map fst |> List.distinct
      // Every binding that moved at all moved through BOTH roles, and no accessor
      // of a settable binding ever escaped into the loose function pairs.
      let bothRoles =
        bindingsTouched
        |> List.forall (fun b ->
          (redirected |> List.exists (fun (x, r) -> x = b && r = "get"))
          && (redirected |> List.exists (fun (x, r) -> x = b && r = "set")))
      let noneLoose =
        plan.Functions
        |> List.forall (fun (o, _) ->
          match accessorRole o with
          | AccessorRole.Plain -> true
          | AccessorRole.Getter b
          | AccessorRole.Setter b -> not (Set.contains b settable))
      let allAccountedFor =
        (getters |> List.map bindingOf) @ (setters |> List.map bindingOf)
        |> List.distinct
        |> List.forall (fun b ->
          List.contains b bindingsTouched || plan.Declined |> List.exists (fun d -> d.Binding = b))
      bothRoles && noneLoose && allAccountedFor
  ]

// ── Mutable module state, classified ─────────────────────────────────────────
//
// A `let mutable` and a `let` fail for OPPOSITE reasons. The immutable one was
// computed at startup and the app captured the result; the mutable one is the
// app's live data, and carrying it forward would ignore a deliberate edit to the
// initialiser while resetting it would destroy running state. The planner used
// to call both "built at startup", which is true of neither the diagnosis nor
// the fix — and `RestartReason.MutableModuleState`, which says exactly the right
// thing, was unreachable.

let private mutableSource = """module Demo.Web.Program

let mutable requestCount = 0

let getHome : string = "home"

let render (n: int) = sprintf "%d" n
"""

let private declsOfSource (source: string) =
  match extractDecls source with
  | Ok decls -> decls
  | Error reason -> failtestf "extractDecls failed: %s" reason

let private reasonsOfPlan (plan: ReloadPlan) =
  match plan with
  | ReloadPlan.RestartRequired (first, rest) -> ReloadChange.restartReasons first rest
  | ReloadPlan.PatchFunctions fs -> failtestf "expected a restart, got a patch of %A" (fs |> List.map _.Name)

[<Tests>]
let mutableStateClassificationTests =
  testList "ReloadPlanning mutable module state" [
    testCase "WHY — ReloadPlanning.extractDecls — a module-level `let mutable` is its OWN kind because it is live data, not a value computed at startup" <| fun _ ->
      (declsOfSource mutableSource).Decls
      |> List.map (fun d -> d.Name, d.Kind)
      |> Expect.equal "mutable and immutable values are different kinds"
        [ "requestCount", DeclKind.MutableValueDecl
          "getHome", DeclKind.ValueDecl
          "render", DeclKind.FunctionDecl ]

    testCase "WHY — ReloadPlanning.restartReason — editing a `let mutable` reports MutableModuleState because the remedy is 'restart to re-run the initialiser', not 'make it a function'" <| fun _ ->
      let edited = mutableSource.Replace("let mutable requestCount = 0", "let mutable requestCount = 100")
      planReload (declsOfSource mutableSource) (declsOfSource edited)
      |> reasonsOfPlan
      |> Expect.equal "named as mutable module state" [ RestartReason.MutableModuleState "requestCount" ]

    testCase "WHY — ReloadPlanning.restartReason — editing an immutable value still reports StartupComputedValue because the running app captured the finished value" <| fun _ ->
      let edited = mutableSource.Replace("""let getHome : string = "home" """.TrimEnd(), """let getHome : string = "HOME" """.TrimEnd())
      planReload (declsOfSource mutableSource) (declsOfSource edited)
      |> reasonsOfPlan
      |> Expect.equal "named as a startup-computed value" [ RestartReason.StartupComputedValue "getHome" ]

    testCase "WHY — ReloadPlanning.restartReason — a declaration the running build never had reports NewDeclaration because there is no original to re-point" <| fun _ ->
      let edited = mutableSource + "\ntype Extra = { Value: int }\n"
      planReload (declsOfSource mutableSource) (declsOfSource edited)
      |> reasonsOfPlan
      |> Expect.equal "named as a new declaration" [ RestartReason.NewDeclaration "Extra" ]

    testCase "WHY — ReloadPlanning.restartReason — the user-facing message for mutable state refuses to guess between carrying the value forward and resetting it" <| fun _ ->
      let message = ReloadOutcome.describeForUser (restartOutcome (ReloadChange.MutableStateChanged "requestCount") [])
      message |> Expect.stringContains "says what the binding is" "mutable module state"
      message |> Expect.stringContains "and what to do" "Restart the app"
  ]

// ── What the patch actually did to the running process ───────────────────────

let private fnDecl (name: string) : SourceDecl =
  { Name = name
    Kind = DeclKind.FunctionDecl
    Access = DeclAccess.Public
    Header = sprintf "let %s x" name
    Text = sprintf "let %s x = x" name
    StartLine = 1
    EndLine = 1 }

let private beforeWith (names: string list) : FileDecls =
  { ModulePath = [ "M" ]; Opens = []; Decls = names |> List.map fnDecl; RawSource = None }

[<Tests>]
let confirmPatchOutcomeTests =
  testList "ReloadPlanning confirmPatchAsOutcome" [
    testCase "WHY — ReloadPlanning.confirmPatchAsOutcome — a patch list of zero is NoEffect, never success, because 'applied' with nothing applied is the exact lie that refreshed browsers into identical code" <| fun _ ->
      confirmPatchAsOutcome (beforeWith [ "f" ]) [] []
      |> Expect.equal "nothing considered, nothing patched" (ReloadOutcome.NoEffect(0, []))

    testCase "WHY — ReloadPlanning.confirmPatchAsOutcome — a partial patch reports BOTH numbers because a partial reload must read as partial" <| fun _ ->
      confirmPatchAsOutcome (beforeWith [ "f"; "g" ]) [ fnDecl "f"; fnDecl "g" ] [ "M.f" ]
      |> Expect.equal "one of two" (ReloadOutcome.Patched(1, 2))

    testCase "WHY — ReloadPlanning.confirmPatchAsOutcome — a function that existed and was not detoured is a SIGNATURE change because that is the only reason the matcher could not pair it" <| fun _ ->
      confirmPatchAsOutcome (beforeWith [ "f" ]) [ fnDecl "f" ] []
      |> Expect.equal "no effect, with the reason"
        (ReloadOutcome.NoEffect(1, [ RestartReason.SignatureChanged "f" ]))

    testCase "WHY — ReloadPlanning.confirmPatchAsOutcome — a function the running build never had is a NEW declaration because there is no captured closure to re-point" <| fun _ ->
      confirmPatchAsOutcome (beforeWith []) [ fnDecl "brandNew" ] []
      |> Expect.equal "no effect, named as new"
        (ReloadOutcome.NoEffect(1, [ RestartReason.NewDeclaration "brandNew" ]))

    testCase "WHY — ReloadPlanning.confirmPatchAsOutcome — an outcome that changed nothing must not tell the browser to refresh because refreshing into identical code is what users read as 'hot reload is broken'" <| fun _ ->
      confirmPatchAsOutcome (beforeWith [ "f" ]) [ fnDecl "f" ] []
      |> ReloadOutcome.shouldRefreshBrowser
      |> Expect.isFalse "nothing reached the running app"
      confirmPatchAsOutcome (beforeWith [ "f" ]) [ fnDecl "f" ] [ "M.f" ]
      |> ReloadOutcome.shouldRefreshBrowser
      |> Expect.isTrue "the running app serves new code"

    testProperty "WHY — ReloadPlanning.confirmPatchAsOutcome — the reported count never exceeds what was considered and is zero exactly when nothing was detoured, because the numbers are the whole claim" <| fun (detouredFlags: bool list) ->
      let names = detouredFlags |> List.mapi (fun i _ -> sprintf "fn%d" i)
      let decls = names |> List.map fnDecl
      let reloaded = List.zip names detouredFlags |> List.choose (fun (n, d) -> match d with | true -> Some ("M." + n) | false -> None)
      match confirmPatchAsOutcome (beforeWith names) decls reloaded with
      | ReloadOutcome.Patched(patched, considered) ->
        patched > 0 && patched <= considered && considered = List.length decls && patched = List.length reloaded
      | ReloadOutcome.NoEffect(considered, reasons) ->
        List.isEmpty reloaded && considered = List.length decls && List.length reasons = List.length decls
      | other -> failtestf "confirmPatchAsOutcome must only ever produce Patched or NoEffect, got %A" other
  ]
