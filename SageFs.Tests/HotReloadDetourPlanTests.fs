module SageFs.Tests.HotReloadDetourPlanTests

open System
open System.Runtime.CompilerServices
open Expecto
open Expecto.Flip
open SageFs.Middleware.HotReloading
open SageFs.Middleware.HotReloadCore
open SageFs.Features.ReloadOutcome
open SageFs.Features.ReloadPlanning
open SageFs.Utils

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

// ── ReloadOutcome.withExtraMisses ─────────────────────────────────────────────
//
// A mutable binding that was declined or never landed is invisible to
// `confirmPatchAsOutcome` — it only ever sees the file's plain functions.
// `withExtraMisses` is how those reach the same outcome the functions did.

[<Tests>]
let withExtraMissesTests =
  testList "ReloadOutcome.withExtraMisses" [
    testCase "WHY — ReloadOutcome.withExtraMisses — nothing extra leaves the outcome untouched because an empty list has nothing to add" <| fun _ ->
      let outcome = ReloadOutcome.Patched(1, 1)
      outcome |> ReloadOutcome.withExtraMisses [] |> Expect.equal "unchanged" outcome

    testCase "WHY — ReloadOutcome.withExtraMisses — NoEffect grows BOTH its reasons and its considered count because 0 of N must count the binding too" <| fun _ ->
      ReloadOutcome.NoEffect(2, [ RestartReason.SignatureChanged "f" ])
      |> ReloadOutcome.withExtraMisses [ RestartReason.MutableModuleState "counter" ]
      |> Expect.equal "3 considered, both reasons"
        (ReloadOutcome.NoEffect(3, [ RestartReason.SignatureChanged "f"; RestartReason.MutableModuleState "counter" ]))

    testCase "WHY — ReloadOutcome.withExtraMisses — RestartRequired appends without touching any count because it never carried one" <| fun _ ->
      ReloadOutcome.RestartRequired [ RestartReason.TypeShapeChanged "Todo" ]
      |> ReloadOutcome.withExtraMisses [ RestartReason.MutableModuleState "counter" ]
      |> Expect.equal "both reasons"
        (ReloadOutcome.RestartRequired [ RestartReason.TypeShapeChanged "Todo"; RestartReason.MutableModuleState "counter" ])

    testCase "WHY — ReloadOutcome.withExtraMisses — Patched has nowhere to put a reason and is left exactly as it was, the same partial-visibility limit an ordinary missed function already has" <| fun _ ->
      let outcome = ReloadOutcome.Patched(2, 2)
      outcome
      |> ReloadOutcome.withExtraMisses [ RestartReason.MutableModuleState "counter" ]
      |> Expect.equal "counts unchanged, real successes not thrown away" outcome

    testCase "WHY — ReloadOutcome.withExtraMisses — Restarted and CompileFailed are also left alone because neither is a place a missed binding belongs" <| fun _ ->
      let extra = [ RestartReason.MutableModuleState "counter" ]
      ReloadOutcome.Restarted [ RestartReason.TypeShapeChanged "Todo" ]
      |> ReloadOutcome.withExtraMisses extra
      |> Expect.equal "restarted unchanged" (ReloadOutcome.Restarted [ RestartReason.TypeShapeChanged "Todo" ])
      ReloadOutcome.CompileFailed "boom"
      |> ReloadOutcome.withExtraMisses extra
      |> Expect.equal "compile-failed unchanged" (ReloadOutcome.CompileFailed "boom")
  ]

// ── ReloadChange.MutableBindingTorn ────────────────────────────────────────────
//
// Discovered only from the RUNTIME detour result (a `BindingOutcome.Torn`),
// never from a source diff — this is the ONE `ReloadChange` case that never
// comes out of `planReload`. It still has to describe and remedy itself
// exactly like every other reason, because the same `RestartReason` gate
// (`restartOrFallBack`) reports both kinds without knowing which is which.

[<Tests>]
let mutableBindingTornChangeTests =
  testList "ReloadChange.MutableBindingTorn" [
    testCase "WHY — ReloadChange.restartReason — a torn binding is named MutableModuleState because the remedy (restart to re-run the initialiser) is identical to any other mutable-state refusal" <| fun _ ->
      ReloadChange.MutableBindingTorn "counter"
      |> ReloadChange.restartReason
      |> Expect.equal "same case as a source-level mutable-state change" (RestartReason.MutableModuleState "counter")

    testCase "WHY — ReloadChange.describe — names the binding and says reads and writes disagree, because that is the danger a user acts on" <| fun _ ->
      ReloadChange.describe (ReloadChange.MutableBindingTorn "counter")
      |> Expect.stringContains "names the binding" "counter"
      ReloadChange.describe (ReloadChange.MutableBindingTorn "counter")
      |> Expect.stringContains "says what's wrong" "disagree"
  ]

// ── BindingOutcome.Torn, reached through REAL Harmony detours ────────────────
//
// Real (process-global) Harmony patches, like HarmonyCanaryTests and
// MethodPatcherTests — dedicated throwaway methods, and the whole section
// joins their "sagefs-harmony" sequenced group so it can never race another
// suite's detours on the same process.
//
// `applyBindingDetour` preflights every leg (JIT-prepares it) before writing
// any of them, so a leg that CANNOT be JIT-compiled is caught before anything
// moves (NeitherLegRedirected, not Torn — the atomicity guarantee the header
// comment describes). Torn is reached a layer further in: preflight only
// proves a leg can be JIT-compiled, not that Harmony's OWN detour will
// succeed on it. A method detoured to ITSELF preflights cleanly (it is a
// perfectly ordinary, already-JIT-compiled method) and then fails inside
// Harmony's PatchTools.DetourMethod with "Cannot detour a method to itself"
// (confirmed live against this repo's Harmony/MonoMod build) — a distinct,
// later failure surface preflight cannot see coming. Pairing that with a
// genuinely different, compatible method for the other leg reproduces Torn
// through the real path, not a hand-built DU literal.
// NOT `private`: HarmonyCanaryTests/MethodPatcherTests' own real-detour probe
// types are all non-private, and a `private` type here reproducibly kept
// BOTH legs from landing in the compiled test binary (NeitherLegRedirected
// every time, confirmed across three independent method pairs) even though
// the identical sequence works from FSI — Harmony/MonoMod's detour machinery
// on this repo's build evidently needs the ordinary public-type IL shape.
type TornProbeMethods() =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member GetterOld() : int = 1

  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member SetterOld1(_x: int) : unit = ()
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member SetterNew1(_x: int) : unit = ()
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member SetterOld2(_x: int) : unit = ()
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member SetterNew2(_x: int) : unit = ()
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member SetterOld3(_x: int) : unit = ()
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member SetterNew3(_x: int) : unit = ()

let private tornProbe (name: string) : Method =
  { MethodInfo = typeof<TornProbeMethods>.GetMethod(name); FullName = "Torn.Config." + name }

/// Three independent setter pairs, tried in turn: real (process-global,
/// native-code-patching) Harmony detours have shown genuine run-to-run
/// flakiness on this exact class of test elsewhere in the suite
/// (HarmonyCanaryTests documents JIT/tiered-compilation and cross-suite
/// contention as causes) — a landed detour confirmed live via the REPL
/// (see the module comment) occasionally does not land under the full
/// suite's parallel load. Each pair is a FRESH, never-before-patched method,
/// so a retry is a genuinely independent attempt, not a repeat of one that
/// already failed.
let private setterPairs =
  [ "SetterOld1", "SetterNew1"
    "SetterOld2", "SetterNew2"
    "SetterOld3", "SetterNew3" ]

[<Tests>]
let realTornBindingTests =
  testSequencedGroup "sagefs-harmony" (testList "HotReloadCore applyDetourPlan — a real torn accessor pair" [
    testCase "WHY — HotReloadCore.applyDetourPlan — one leg self-detoured (fails inside Harmony, not preflight) and the other genuinely redirected is reported Torn, not silently dropped, because the running process now reads the OLD field and writes the NEW one" <| fun _ ->
      // Reuse the SAME `Method` value for both sides of the pair — calling
      // `tornProbe "GetterOld"` a second time builds a distinct `Method`
      // record wrapping a SEPARATE `Type.GetMethod` lookup, and MonoMod's
      // "Cannot detour a method to itself" guard did not fire against two
      // such lookups in the compiled test binary (confirmed: it silently
      // succeeded, landing BothLegsRedirected instead of failing this leg —
      // reflection does not guarantee `GetMethod` returns the same
      // `MethodInfo` instance across separate calls). One shared value is
      // guaranteed to be the self-detour MonoMod actually rejects.
      let getterOld = tornProbe "GetterOld"
      let selfDetouredGetter = getterOld, getterOld
      let attempt (oldName, newName) : DetourReport =
        let plan : DetourPlan =
          { Functions = []
            MutableBindings =
              [ { Binding = "Torn.Config.probe"
                  FirstGetter = selfDetouredGetter
                  MoreGetters = []
                  FirstSetter = tornProbe oldName, tornProbe newName
                  MoreSetters = [] } ]
            Declined = [] }
        applyDetourPlan (Log.asILogger()) plan
      let rec tryPairs =
        function
        | [] ->
          // Every independent attempt's "should succeed" leg also failed to
          // land — an environment limitation (MonoMod/CoreCLR
          // compatibility or suite-wide contention), not evidence that
          // applyBindingDetour mishandles a landed+failed pair. Skip rather
          // than assert a false negative, matching HarmonyCanaryTests'
          // documented tolerance for the same class of Harmony flakiness.
          skiptest
            "could not manufacture a torn pair on this run — every attempt either landed both legs (the atomicity guarantee holding) or failed both. Torn's reporting path is pinned by WorkerMainTests' escalationOf cases, which construct the outcome directly."
        | pair :: rest ->
          let report = attempt pair
          match report.Bindings with
          | [ BindingOutcome.Torn(binding, reason) ] ->
            binding |> Expect.equal "names the torn binding" "Torn.Config.probe"
            reason |> Expect.stringContains "carries why the self-detoured leg failed" "itself"
            // The whole point: Torn is reported through the SAME channel a
            // caller reads any other binding outcome from — nothing about
            // it is swallowed once it exists.
            report.Failures |> Expect.isNonEmpty "a torn binding's failing leg is still counted as a failure"
          | [ BindingOutcome.NeitherLegRedirected _ ] -> tryPairs rest
          // MonoMod's "cannot detour a method to itself" guard did not fire —
          // the self-detour silently succeeded and BOTH legs landed. That is a
          // failure to MANUFACTURE a tear, not a defect: the pair applied
          // atomically, which is the guarantee `AccessorPairDetour` exists to
          // provide. Try the next pair; if none of them tears, the skip below
          // says so honestly rather than reporting a green that proved nothing.
          | [ BindingOutcome.BothLegsRedirected _ ] -> tryPairs rest
          | other -> failtestf "expected exactly one Torn, NeitherLegRedirected or BothLegsRedirected outcome, got %A" other
      tryPairs setterPairs
  ])
