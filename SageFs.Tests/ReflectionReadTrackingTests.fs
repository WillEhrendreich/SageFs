/// Rule 2's reflection reads: the runtime half, on a real (emitted) app.
///
/// Each test emits a tiny app assembly with System.Reflection.Emit: an F#
/// module `App` with one field-backed value `greeting` (a real `ldsfld; ret`
/// getter, built without optimizations so rule 2 tracks it), and callers that
/// read it through reflection and either throw the result away or keep it. A
/// real `Tracker` watches it, the startup window closes, and then the callers
/// run. Nothing here is simulated: the reflection entry watch, the stack walk,
/// the caller rewiring and the slot are the production code.
module SageFs.Tests.ReflectionReadTrackingTests

open System
open System.Diagnostics
open System.Reflection
open System.Reflection.Emit
open Expecto
open Expecto.Flip
open SageFs.Middleware.ValueReads
open SageFs.Middleware.ValueReadTracking

type private App =
  { Assembly: Assembly
    Greeting: PropertyInfo
    GreetingField: FieldInfo
    /// `p.GetValue(null) |> ignore`
    ReadAndDrop: Action<PropertyInfo>
    /// `Sink <- p.GetValue(null)`
    ReadAndKeep: Action<PropertyInfo>
    /// `f.GetValue(null) |> ignore`, on the backing field
    FieldAndDrop: Action<FieldInfo>
    /// `relay.GetValue(null) |> ignore`, where relay's getter reads
    /// `Relay.Target.GetValue(null)` and keeps it.
    Outer: Action<PropertyInfo>
    Relay: PropertyInfo
    RelayTarget: FieldInfo
    /// The value the getter's field is loaded with.
    ValueKey: string }

let private emitApp () : App =
  let name = sprintf "ReflectApp%s" (Guid.NewGuid().ToString "N")
  let asm = AssemblyBuilder.DefineDynamicAssembly(AssemblyName name, AssemblyBuilderAccess.Run)
  // Built without optimizations, like SageFs builds the user's project.
  asm.SetCustomAttribute(
    CustomAttributeBuilder(
      typeof<DebuggableAttribute>.GetConstructor [| typeof<DebuggableAttribute.DebuggingModes> |],
      [| box (DebuggableAttribute.DebuggingModes.Default ||| DebuggableAttribute.DebuggingModes.DisableOptimizations) |]))
  let modb = asm.DefineDynamicModule name
  // module App = let greeting = "hello"
  let app = modb.DefineType("App", TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed)
  app.SetCustomAttribute(
    CustomAttributeBuilder(
      typeof<CompilationMappingAttribute>.GetConstructor [| typeof<SourceConstructFlags> |],
      [| box SourceConstructFlags.Module |]))
  let field = app.DefineField("greeting@1", typeof<string>, FieldAttributes.Assembly ||| FieldAttributes.Static)
  let cctor = app.DefineTypeInitializer().GetILGenerator()
  cctor.Emit(OpCodes.Ldstr, "hello")
  cctor.Emit(OpCodes.Stsfld, field)
  cctor.Emit(OpCodes.Ret)
  let getter = app.DefineMethod("get_greeting", MethodAttributes.Public ||| MethodAttributes.Static ||| MethodAttributes.SpecialName, typeof<string>, [||])
  let gil = getter.GetILGenerator()
  gil.Emit(OpCodes.Ldsfld, field)
  gil.Emit(OpCodes.Ret)
  let prop = app.DefineProperty("greeting", PropertyAttributes.None, typeof<string>, [||])
  prop.SetGetMethod getter
  // The callers, in a plain type (not a module: nothing here is a value).
  let callers = modb.DefineType("Callers", TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed)
  let sink = callers.DefineField("Sink", typeof<obj>, FieldAttributes.Public ||| FieldAttributes.Static)
  let target = callers.DefineField("Target", typeof<PropertyInfo>, FieldAttributes.Public ||| FieldAttributes.Static)
  let getValue = typeof<PropertyInfo>.GetMethod("GetValue", [| typeof<obj> |])
  let fieldGetValue = typeof<FieldInfo>.GetMethod("GetValue", [| typeof<obj> |])
  let defineStatic (name: string) (arg: Type) (body: ILGenerator -> unit) =
    let m = callers.DefineMethod(name, MethodAttributes.Public ||| MethodAttributes.Static, typeof<Void>, [| arg |])
    body (m.GetILGenerator())
  defineStatic "ReadAndDrop" typeof<PropertyInfo> (fun il ->
    il.Emit(OpCodes.Ldarg_0)
    il.Emit(OpCodes.Ldnull)
    il.Emit(OpCodes.Callvirt, getValue)
    il.Emit(OpCodes.Pop)
    il.Emit(OpCodes.Ret))
  defineStatic "ReadAndKeep" typeof<PropertyInfo> (fun il ->
    il.Emit(OpCodes.Ldarg_0)
    il.Emit(OpCodes.Ldnull)
    il.Emit(OpCodes.Callvirt, getValue)
    il.Emit(OpCodes.Stsfld, sink)
    il.Emit(OpCodes.Ret))
  defineStatic "FieldAndDrop" typeof<FieldInfo> (fun il ->
    il.Emit(OpCodes.Ldarg_0)
    il.Emit(OpCodes.Ldnull)
    il.Emit(OpCodes.Callvirt, fieldGetValue)
    il.Emit(OpCodes.Pop)
    il.Emit(OpCodes.Ret))
  defineStatic "Outer" typeof<PropertyInfo> (fun il ->
    il.Emit(OpCodes.Ldarg_0)
    il.Emit(OpCodes.Ldnull)
    il.Emit(OpCodes.Callvirt, getValue)
    il.Emit(OpCodes.Pop)
    il.Emit(OpCodes.Ret))
  // static member Relay = (Callers.Target.GetValue null |> keep); null
  let relayGetter = callers.DefineMethod("get_Relay", MethodAttributes.Public ||| MethodAttributes.Static ||| MethodAttributes.SpecialName, typeof<obj>, [||])
  let ril = relayGetter.GetILGenerator()
  ril.Emit(OpCodes.Ldsfld, target)
  ril.Emit(OpCodes.Ldnull)
  ril.Emit(OpCodes.Callvirt, getValue)
  ril.Emit(OpCodes.Stsfld, sink)
  ril.Emit(OpCodes.Ldnull)
  ril.Emit(OpCodes.Ret)
  let relay = callers.DefineProperty("Relay", PropertyAttributes.None, typeof<obj>, [||])
  relay.SetGetMethod relayGetter
  let appType = app.CreateType()
  let callersType = callers.CreateType()
  let action (name: string) : Action<'a> =
    Delegate.CreateDelegate(typeof<Action<'a>>, callersType.GetMethod name) :?> Action<'a>
  let greeting = appType.GetProperty "greeting"
  { Assembly = appType.Assembly
    Greeting = greeting
    GreetingField = appType.GetField("greeting@1", BindingFlags.NonPublic ||| BindingFlags.Static)
    ReadAndDrop = action "ReadAndDrop"
    ReadAndKeep = action "ReadAndKeep"
    FieldAndDrop = action "FieldAndDrop"
    Outer = action "Outer"
    Relay = callersType.GetProperty "Relay"
    RelayTarget = callersType.GetField "Target"
    ValueKey = valueKeyOf (greeting.GetGetMethod()) }

/// A clock the test moves by hand.
type private Clock() =
  let mutable now = 0L
  member _.Now() = now
  member _.Advance(by: TimeSpan) = now <- now + by.Ticks

/// An app with a tracker on it, started, and its startup window closed.
let private started (mode: ReflectionReadMode) (threshold: HotLoopThreshold) =
  let app = emitApp ()
  let clock = Clock()
  let tracker = Tracker({ Mode = mode; HotLoop = threshold; Tiering = TieringChoice.TieringOffWhileWatching }, clock.Now)
  tracker.Track [ app.Assembly ]
  tracker.EvalFinished true
  app, tracker, clock

let private quiet = { Count = 1_000_000; Within = TimeSpan.FromSeconds 1.0 }

let private verdictFor (app: App) (tracker: Tracker) =
  match tracker.Evidence [ app.ValueKey ] with
  | [ evidence ] -> verdictOf evidence
  | other -> failtestf "expected evidence for one value, got %A" other

let private sightings (app: App) (tracker: Tracker) =
  tracker.Ledger.ReflectiveReads |> Map.tryFind app.ValueKey |> Option.defaultValue [] |> List.map _.Caller

let private held (verdict: ValueVerdict) =
  match verdict with
  | ValueVerdict.HeldBy(site, _) -> site
  | other -> failtestf "expected the value to be held, got %A" other

/// This test process runs with tiered compilation on. A CoreLib method the
/// watch is patched onto can be recompiled mid-test, and then the watch lapses
/// (the hot-reload host turns tiering off so it can't). A lapse has one
/// promise: the value fails closed. So when the watch lapsed, that's what
/// gets checked; otherwise it's the behaviour under test.
let private orFailClosed (app: App) (tracker: Tracker) (check: unit -> unit) =
  match tracker.ReflectionReads.Watch with
  | ReflectionWatchStatus.Lapsed why ->
    match verdictFor app tracker with
    | ValueVerdict.CannotTell _ -> printfn "reflection watch lapsed in this run (%s); checked it failed closed instead" why
    | other -> failtestf "the watch lapsed (%s) but the value wasn't CannotTell: %A" why other
  | ReflectionWatchStatus.Watching
  | ReflectionWatchStatus.NotWatching _ -> check ()

/// The runtime method the entry watch patches for `MethodBase.Invoke`.
let private invokeEntryPoint () =
  let invoke =
    match <@ fun (m: MethodBase) -> m.Invoke(null, BindingFlags.Default, null, null, null) @> with
    | Quotations.Patterns.Lambda(_, Quotations.Patterns.Call(_, m, _)) -> m
    | other -> failtestf "unexpected quotation %A" other
  invoke.GetType().GetMethods(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly)
  |> Array.find (fun m -> m.GetBaseDefinition() = invoke.GetBaseDefinition())

[<Tests>]
let reflectionReadTrackingTests =
  testSequenced <| testList "reflection reads after startup, on a real app" [
    testCase "WHY — the startup window closing no longer hides a reflective read: mark-on-reflect marks the value, and its edit restarts" <| fun _ ->
      let app, tracker, _ = started ReflectionReadMode.MarkOnReflect quiet
      app.ReadAndDrop.Invoke app.Greeting
      orFailClosed app tracker (fun () ->
        let site = verdictFor app tracker |> held
        site.Where |> Expect.equal "it has no read in anyone's code" SiteLocation.NotInItsCode
        describeHolder site ReadSeen.AfterStartup |> Expect.stringContains "and the reason says which mode marked it" "mark-on-reflect")

    testCase "WHY — probe-callers finds the caller and its reflection call, and a caller that throws the value away holds nothing" <| fun _ ->
      let app, tracker, _ = started ReflectionReadMode.ProbeCallers quiet
      app.ReadAndDrop.Invoke app.Greeting
      orFailClosed app tracker (fun () ->
        match sightings app tracker with
        | [ ReflectiveCaller.AtSite(reader, _, fate) ] ->
          reader |> Expect.stringContains "names the caller" "Callers.ReadAndDrop"
          fate |> Expect.equal "the caller popped it" ReadFate.Discarded
        | other -> failtestf "expected one sighting at the caller's call site, got %A" other
        verdictFor app tracker |> Expect.equal "so the value can still be patched" ValueVerdict.SafeToPatch)

    testCase "WHY — probe-callers rewires the caller, so its later reads are named from the slot without walking the stack" <| fun _ ->
      let app, tracker, _ = started ReflectionReadMode.ProbeCallers quiet
      app.ReadAndDrop.Invoke app.Greeting
      let walksAfterFirst = tracker.ReflectionReads.Walks
      for _ in 1 .. 50 do
        app.ReadAndDrop.Invoke app.Greeting
      orFailClosed app tracker (fun () ->
        let report = tracker.ReflectionReads
        report.Walks |> Expect.equal "no more walks once the caller is rewired" walksAfterFirst
        (report.SiteHits, 50L) |> Expect.isGreaterThanOrEqual "every later read came from the slot")

    testCase "WHY — a caller that keeps what reflection handed it holds the value, and the restart reason names it and what it did" <| fun _ ->
      let app, tracker, _ = started ReflectionReadMode.ProbeCallers quiet
      app.ReadAndKeep.Invoke app.Greeting
      app.ReadAndKeep.Invoke app.Greeting
      orFailClosed app tracker (fun () ->
        let site = verdictFor app tracker |> held
        site.Reader |> Expect.stringContains "names the caller" "Callers.ReadAndKeep"
        site.Fate |> Expect.equal "stored it in Callers.Sink" (ReadFate.Escaped(Escape.StoredInField "Callers.Sink")))

    testCase "WHY — a rewired caller's slot is used up by its own call, so a reflective read INSIDE the target is pinned on the target's code, never on the outer caller" <| fun _ ->
      let app, tracker, _ = started ReflectionReadMode.ProbeCallers quiet
      app.RelayTarget.SetValue(null, app.Greeting)
      // Outer reads greeting itself and throws it away: that's what gets
      // Outer rewired. Then the rewired Outer reads the relay, whose getter
      // reads greeting and keeps it. A slot left for Outer's call would pin
      // that read on Outer (thrown away) and the value would look patchable.
      app.Outer.Invoke app.Greeting
      app.Outer.Invoke app.Relay
      orFailClosed app tracker (fun () ->
        tracker.ReflectionReads.SiteHits |> Expect.equal "the relay's read was never named from Outer's slot" 0L
        let readers =
          sightings app tracker
          |> List.map (function
            | ReflectiveCaller.AtSite(reader, _, _) -> reader
            | ReflectiveCaller.Unattributed why -> why)
        readers |> List.exists (fun r -> r.Contains "get_Relay") |> Expect.isTrue (sprintf "the relay's getter read it: %A" readers)
        (verdictFor app tracker |> held).Reader |> Expect.stringContains "and kept it, so the value is held" "get_Relay")

    testCase "WHY — exact-every-read keeps the getter's watch on past startup, and names a reflective caller at its call site" <| fun _ ->
      let app, tracker, _ = started ReflectionReadMode.ExactEveryRead quiet
      app.ReadAndKeep.Invoke app.Greeting
      orFailClosed app tracker (fun () ->
        let site = verdictFor app tracker |> held
        site.Reader |> Expect.stringContains "names the caller" "Callers.ReadAndKeep"
        site.Where |> Expect.notEqual "at its reflection call" SiteLocation.NotInItsCode
        (tracker.ReflectionReads.Walks, 1L) |> Expect.isGreaterThanOrEqual "and the report counts the getter watch's walks")

    testCase "WHY — a read of the backing field through FieldInfo.GetValue is a reflective read too" <| fun _ ->
      let app, tracker, _ = started ReflectionReadMode.MarkOnReflect quiet
      app.FieldAndDrop.Invoke app.GreetingField
      orFailClosed app tracker (fun () -> verdictFor app tracker |> held |> ignore)

    testCase "WHY — a delegate made from the getter can run anywhere, so making one holds the value in every mode" <| fun _ ->
      for mode in ReflectionReadMode.all do
        let app, tracker, _ = started mode quiet
        app.Greeting.GetGetMethod().CreateDelegate(typeof<Func<string>>) |> ignore
        orFailClosed app tracker (fun () ->
          (verdictFor app tracker |> held).Fate
          |> Expect.equal (sprintf "%A: a delegate" mode) (ReadFate.Escaped(Escape.Untraced "its getter was turned into a delegate through reflection, and a delegate can run anywhere")))

    testCase "WHY — switching modes takes effect on the next read, with no restart" <| fun _ ->
      let app, tracker, _ = started ReflectionReadMode.MarkOnReflect quiet
      tracker.SetMode ReflectionReadMode.ProbeCallers |> _.Mode |> Expect.equal "switched" ReflectionReadMode.ProbeCallers
      app.ReadAndDrop.Invoke app.Greeting
      orFailClosed app tracker (fun () ->
        verdictFor app tracker |> Expect.equal "read the probe-callers way: thrown away, so patchable" ValueVerdict.SafeToPatch)

    testCase "WHY — switching to exact-every-read after startup puts the getter watch back, and switching off takes it off again" <| fun _ ->
      let app, tracker, _ = started ReflectionReadMode.ProbeCallers quiet
      tracker.SetMode ReflectionReadMode.ExactEveryRead |> ignore
      app.ReadAndKeep.Invoke app.Greeting
      orFailClosed app tracker (fun () ->
        (verdictFor app tracker |> held).Reader |> Expect.stringContains "seen through the getter's watch" "Callers.ReadAndKeep"
        tracker.SetMode ReflectionReadMode.MarkOnReflect |> ignore
        tracker.ReflectionReads.Mode |> Expect.equal "and back" ReflectionReadMode.MarkOnReflect)

    testCase "WHY — a hot reflective loop raises one notice naming the value, the caller and the rate, and picking a mode answers it" <| fun _ ->
      let app, tracker, clock = started ReflectionReadMode.MarkOnReflect { Count = 5; Within = TimeSpan.FromSeconds 1.0 }
      for _ in 1 .. 20 do
        clock.Advance(TimeSpan.FromMilliseconds 10.0)
        app.ReadAndDrop.Invoke app.Greeting
      match tracker.ReflectionReads.Notices with
      | [ { Value = value; State = NoticeState.Asked notice } ] ->
        value |> Expect.equal "the value" app.ValueKey
        notice.Caller |> Expect.stringContains "the caller" "Callers.ReadAndDrop"
        (notice.ReadsPerSecond, 50) |> Expect.isGreaterThanOrEqual "a read every 10 ms is a hundred a second"
        notice.Mode |> Expect.equal "and the mode it was costing" ReflectionReadMode.MarkOnReflect
      | other -> failtestf "expected exactly one asked notice, got %A" other
      tracker.SetMode ReflectionReadMode.ProbeCallers |> ignore
      match tracker.ReflectionReads.Notices with
      | [ { State = NoticeState.Chosen(_, ReflectionReadMode.ProbeCallers) } ] -> ()
      | other -> failtestf "choosing a mode should answer it, got %A" other

    testCase "WHY — a slow trickle of reflective reads never asks" <| fun _ ->
      let app, tracker, clock = started ReflectionReadMode.MarkOnReflect { Count = 5; Within = TimeSpan.FromSeconds 1.0 }
      for _ in 1 .. 20 do
        clock.Advance(TimeSpan.FromMilliseconds 400.0)
        app.ReadAndDrop.Invoke app.Greeting
      tracker.ReflectionReads.Notices |> Expect.isEmpty "never hot"

    testCase "WHY — a watch that stops firing (the runtime recompiled what it was patched onto) is caught at the next checkpoint, every value fails closed, and the watch goes back on" <| fun _ ->
      let app, tracker, _ = started ReflectionReadMode.ProbeCallers quiet
      // Knock the prefix off, which is what a recompile does to it.
      let entry = invokeEntryPoint ()
      HarmonyLib.Harmony("sagefs.valuereads").Unpatch(entry :> MethodBase, HarmonyLib.HarmonyPatchType.Prefix, "sagefs.valuereads")
      app.ReadAndKeep.Invoke app.Greeting
      match verdictFor app tracker with
      | ValueVerdict.CannotTell why -> why |> Expect.stringContains "says the watch lapsed" "stopped seeing reads"
      | other -> failtestf "a read the watch may have missed must never look patchable, got %A" other
      match tracker.ReflectionReads.Watch with
      | ReflectionWatchStatus.Lapsed _ -> ()
      | other -> failtestf "the report should say the watch lapsed, got %A" other
      // A tracker started after the lapse is watched again.
      let app2, tracker2, _ = started ReflectionReadMode.ProbeCallers quiet
      app2.ReadAndKeep.Invoke app2.Greeting
      orFailClosed app2 tracker2 (fun () -> (verdictFor app2 tracker2 |> held).Reader |> Expect.stringContains "seen again" "Callers.ReadAndKeep")
  ]
