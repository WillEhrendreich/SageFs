/// Rule 2's lowest layer: what one read of a module value did with it, read
/// out of real IL, and what a set of reads adds up to.
///
/// The IL here is emitted with System.Reflection.Emit, one tiny method per
/// shape, so each test pins exactly the instructions it's about: `pop`, a
/// local nothing reads, `stsfld`/`stfld`, a new object built around it, an
/// argument, a return. `Value()` stands in for a module value's getter.
module SageFs.Tests.ValueReadsTests

open System
open System.Reflection
open System.Reflection.Emit
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Middleware.ValueReads

/// One emitted assembly per test: a `Source.Value()` getter stand-in, a
/// `Source.Sink` static field, a `Holder` with a `Text` field, a `Box(string)`
/// closure stand-in, a `Source.Consume(string)` sink, and the method under test.
type private Emitted =
  { Method: MethodInfo
    Value: MethodInfo }

let private emit (returns: Type) (locals: Type list) (body: ILGenerator -> MethodInfo -> FieldInfo -> ConstructorInfo -> ConstructorInfo * FieldInfo -> MethodInfo -> unit) : Emitted =
  let asm = AssemblyBuilder.DefineDynamicAssembly(AssemblyName(sprintf "ValueReads%s" (Guid.NewGuid().ToString "N")), AssemblyBuilderAccess.RunAndCollect)
  let modb = asm.DefineDynamicModule "m"
  // Box(string): what F# builds when a closure captures a value.
  let box = modb.DefineType("Box", TypeAttributes.Public)
  let boxField = box.DefineField("held", typeof<string>, FieldAttributes.Public)
  let boxCtor = box.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [| typeof<string> |])
  let il = boxCtor.GetILGenerator()
  il.Emit(OpCodes.Ldarg_0)
  il.Emit(OpCodes.Call, typeof<obj>.GetConstructor [||])
  il.Emit(OpCodes.Ldarg_0)
  il.Emit(OpCodes.Ldarg_1)
  il.Emit(OpCodes.Stfld, boxField)
  il.Emit(OpCodes.Ret)
  // Holder: an object with a field the value can be stored into.
  let holder = modb.DefineType("Holder", TypeAttributes.Public)
  let holderText = holder.DefineField("Text", typeof<string>, FieldAttributes.Public)
  let holderCtor = holder.DefineDefaultConstructor MethodAttributes.Public
  let source = modb.DefineType("Source", TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed)
  let sink = source.DefineField("Sink", typeof<string>, FieldAttributes.Public ||| FieldAttributes.Static)
  let value = source.DefineMethod("Value", MethodAttributes.Public ||| MethodAttributes.Static, typeof<string>, [||])
  let vil = value.GetILGenerator()
  vil.Emit(OpCodes.Ldstr, "hello")
  vil.Emit(OpCodes.Ret)
  let consume = source.DefineMethod("Consume", MethodAttributes.Public ||| MethodAttributes.Static, typeof<Void>, [| typeof<string> |])
  consume.GetILGenerator().Emit(OpCodes.Ret)
  let reader = source.DefineMethod("Reader", MethodAttributes.Public ||| MethodAttributes.Static, returns, [||])
  let ril = reader.GetILGenerator()
  for l in locals do
    ril.DeclareLocal l |> ignore
  body ril value sink boxCtor (holderCtor, holderText) consume
  box.CreateType() |> ignore
  holder.CreateType() |> ignore
  let t = source.CreateType()
  { Method = t.GetMethod "Reader"; Value = t.GetMethod "Value" }

let private instrsOf (m: MethodInfo) =
  match decode (m.GetMethodBody().GetILAsByteArray()) with
  | Ok instrs -> List.toArray instrs
  | Error e -> failtestf "the emitted IL should decode: %A" e

let private readsOfValue (value: MethodInfo) (m: MethodInfo) =
  let tokens = tokensOf m
  let classify (i: Instr) =
    match i.Op = OpCodes.Call, i.Operand with
    | true, Operand.Token t ->
      match (try Some(m.Module.ResolveMethod t) with _ -> None) with
      | Some resolved when resolved = (value :> MethodBase) -> ReadMatch.Reads ReadKind.GetterCall
      | _ -> ReadMatch.NotARead
    | _ -> ReadMatch.NotARead
  sitesIn tokens classify (instrsOf m)

/// The fate of the ONE read of `Value()` in the emitted method.
let private fateOf (e: Emitted) =
  match readsOfValue e.Value e.Method with
  | [ _, fate ] -> fate
  | other -> failtestf "expected exactly one read, found %A" other

let private escapes (fate: ReadFate) =
  match fate with
  | ReadFate.Escaped escape -> escape
  | ReadFate.Discarded -> failtest "expected the value to escape, but the classifier says it was thrown away"

[<Tests>]
let classifierTests =
  testList "value reads: where one read went, from real IL" [
    testCase "WHY — pop throws the value away, because nothing can hold what was popped" <| fun _ ->
      emit typeof<Void> [] (fun il value _ _ _ _ ->
        il.Emit(OpCodes.Call, value)
        il.Emit(OpCodes.Pop)
        il.Emit(OpCodes.Ret))
      |> fateOf
      |> Expect.equal "popped" ReadFate.Discarded

    testCase "WHY — a local nothing ever reads throws the value away, which is how F#'s startup code computes a literal value it never uses" <| fun _ ->
      emit typeof<Void> [ typeof<string> ] (fun il value _ _ _ _ ->
        il.Emit(OpCodes.Call, value)
        il.Emit(OpCodes.Stloc_0)
        il.Emit(OpCodes.Ret))
      |> fateOf
      |> Expect.equal "a dead local" ReadFate.Discarded

    testCase "WHY — stsfld keeps the value in a static field, which outlives the call" <| fun _ ->
      emit typeof<Void> [] (fun il value sink _ _ _ ->
        il.Emit(OpCodes.Call, value)
        il.Emit(OpCodes.Stsfld, sink)
        il.Emit(OpCodes.Ret))
      |> fateOf
      |> escapes
      |> Expect.equal "stored in Source.Sink" (Escape.StoredInField "Source.Sink")

    testCase "WHY — stfld keeps the value in an object's field" <| fun _ ->
      emit typeof<Void> [] (fun il value _ _ (holderCtor, text) _ ->
        il.Emit(OpCodes.Newobj, holderCtor)
        il.Emit(OpCodes.Call, value)
        il.Emit(OpCodes.Stfld, text)
        il.Emit(OpCodes.Ret))
      |> fateOf
      |> escapes
      |> Expect.equal "stored in Holder.Text" (Escape.StoredInField "Holder.Text")

    testCase "WHY — newobj builds something around the value, the way a closure captures it" <| fun _ ->
      emit typeof<Void> [] (fun il value _ boxCtor _ _ ->
        il.Emit(OpCodes.Call, value)
        il.Emit(OpCodes.Newobj, boxCtor)
        il.Emit(OpCodes.Pop)
        il.Emit(OpCodes.Ret))
      |> fateOf
      |> escapes
      |> Expect.equal "captured by a new Box" (Escape.CapturedBy "Box")

    testCase "WHY — a local that IS read is followed to where it goes, which is how the fixture's `banner` route captures it (stloc; ldloc; newobj)" <| fun _ ->
      emit typeof<Void> [ typeof<string> ] (fun il value _ boxCtor _ _ ->
        il.Emit(OpCodes.Call, value)
        il.Emit(OpCodes.Stloc_0)
        il.Emit(OpCodes.Ldloc_0)
        il.Emit(OpCodes.Newobj, boxCtor)
        il.Emit(OpCodes.Pop)
        il.Emit(OpCodes.Ret))
      |> fateOf
      |> escapes
      |> Expect.equal "the local carries it into a new Box" (Escape.CapturedBy "Box")

    testCase "WHY — passing the value as an argument lets it escape into code SageFs doesn't follow" <| fun _ ->
      emit typeof<Void> [] (fun il value _ _ _ consume ->
        il.Emit(OpCodes.Call, value)
        il.Emit(OpCodes.Call, consume)
        il.Emit(OpCodes.Ret))
      |> fateOf
      |> escapes
      |> Expect.equal "passed to Consume" (Escape.PassedTo "Source.Consume")

    testCase "WHY — returning the value hands it to the caller, who might keep it (a lazy does)" <| fun _ ->
      emit typeof<string> [] (fun il value _ _ _ _ ->
        il.Emit(OpCodes.Call, value)
        il.Emit(OpCodes.Ret))
      |> fateOf
      |> escapes
      |> Expect.equal "returned" Escape.Returned

    testCase "WHY — the value's consumer is found by the stack, not by the next instruction, so `greeting + \"!\"` lands on String.Concat" <| fun _ ->
      emit typeof<string> [] (fun il value _ _ _ _ ->
        il.Emit(OpCodes.Call, value)
        il.Emit(OpCodes.Ldstr, "!")
        il.Emit(OpCodes.Call, typeof<string>.GetMethod("Concat", [| typeof<string>; typeof<string> |]))
        il.Emit(OpCodes.Ret))
      |> fateOf
      |> escapes
      |> Expect.equal "passed to Concat" (Escape.PassedTo "System.String.Concat")

    testCase "WHY — taking a local's address lets anything write through it, so it counts as an escape" <| fun _ ->
      emit typeof<Void> [ typeof<string> ] (fun il value _ _ _ _ ->
        il.Emit(OpCodes.Call, value)
        il.Emit(OpCodes.Stloc_0)
        il.Emit(OpCodes.Ldloca_S, 0uy)
        il.Emit(OpCodes.Pop)
        il.Emit(OpCodes.Ret))
      |> fateOf
      |> escapes
      |> Expect.equal "address taken" Escape.AddressTaken

    testCase "WHY — a branch before the value is used is not followed, and counts as an escape, because a wrong 'thrown away' is a fake Patched" <| fun _ ->
      emit typeof<Void> [] (fun il value _ _ _ _ ->
        let target = il.DefineLabel()
        il.Emit(OpCodes.Call, value)
        il.Emit(OpCodes.Ldc_I4_1)
        il.Emit(OpCodes.Brtrue_S, target)
        il.MarkLabel target
        il.Emit(OpCodes.Pop)
        il.Emit(OpCodes.Ret))
      |> fateOf
      |> function
        | ReadFate.Escaped(Escape.Untraced _) -> ()
        | other -> failtestf "expected an untraced escape, got %A" other

    testCase "WHY — every read in a method gets its own fate, so a startup method that throws one copy away and captures another is seen as capturing (the fixture's static initializer does exactly this with `banner`)" <| fun _ ->
      let e =
        emit typeof<Void> [ typeof<string>; typeof<string> ] (fun il value _ boxCtor _ _ ->
          il.Emit(OpCodes.Call, value)
          il.Emit(OpCodes.Stloc_0)
          il.Emit(OpCodes.Call, value)
          il.Emit(OpCodes.Stloc_1)
          il.Emit(OpCodes.Ldloc_1)
          il.Emit(OpCodes.Newobj, boxCtor)
          il.Emit(OpCodes.Pop)
          il.Emit(OpCodes.Ret))
      readsOfValue e.Value e.Method
      |> List.map snd
      |> Expect.equal "the first read is thrown away, the second is captured" [ ReadFate.Discarded; ReadFate.Escaped(Escape.CapturedBy "Box") ]

    testCase "WHY — decode reads every method body in SageFs.Core, because a decoder that chokes on real IL can't classify anything" <| fun _ ->
      let flags = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly
      let bodies =
        typeof<Instr>.Assembly.GetTypes()
        |> Array.collect (fun t -> Array.append (t.GetMethods flags |> Array.map (fun m -> m :> MethodBase)) (t.GetConstructors flags |> Array.map (fun c -> c :> MethodBase)))
        |> Array.choose (fun m -> match m.GetMethodBody() with null -> None | body -> Some(m, body.GetILAsByteArray()))
      (bodies.Length, 1000) |> Expect.isGreaterThan "SageFs.Core has method bodies to read"
      let failures =
        bodies
        |> Array.choose (fun (m, il) ->
          match decode il with
          | Ok instrs ->
            // Offsets strictly increase, so every byte belongs to exactly one instruction.
            let offsets = instrs |> List.map _.Offset
            match offsets = List.sort offsets && List.distinct offsets = offsets with
            | true -> None
            | false -> Some(sprintf "%s: offsets out of order" m.Name)
          | Error e -> Some(sprintf "%s.%s: %A" m.DeclaringType.Name m.Name e))
      failures |> Expect.isEmpty "every body decodes"
  ]

// ── evidence and the verdict ──────────────────────────────────────────────────

let private reader (id: string) (reads: (string * int * ReadFate) list) : Reader =
  { Id = id; Name = id; Reads = reads }

let private captured = ReadFate.Escaped(Escape.CapturedBy "Box")

let private ledgerOf (events: LedgerEvent list) = events |> List.fold Ledger.step Ledger.empty

[<Tests>]
let evidenceTests =
  testList "value reads: which reads ran, and what that means for a patch" [
    testCase "WHY — a read that escaped and ran while the app started is a capture, and the verdict names it" <| fun _ ->
      let cctor = reader "cctor" [ "M.banner", 10, captured ]
      let ledger =
        ledgerOf
          [ LedgerEvent.ValueTracked "M.banner"
            LedgerEvent.ReaderFound(cctor, ReaderStatus.Unwatchable "a static initializer")
            LedgerEvent.GetterRead("M.banner", Caller.Known "cctor") ]
      match Ledger.evidence ledger "M.banner" |> verdictOf with
      | ValueVerdict.HeldBy(site, ReadSeen.AtStartup) -> site.Reader |> Expect.equal "the static initializer holds it" "cctor"
      | other -> failtestf "expected HeldBy the cctor at startup, got %A" other

    testCase "WHY — a read that escapes but whose method hasn't run can't hold anything yet, so the value can be patched" <| fun _ ->
      let greet = reader "greet" [ "M.greeting", 0, ReadFate.Escaped Escape.Returned ]
      ledgerOf [ LedgerEvent.ValueTracked "M.greeting"; LedgerEvent.ReaderFound(greet, ReaderStatus.Armed); LedgerEvent.StartupEnded ]
      |> fun l -> Ledger.evidence l "M.greeting" |> verdictOf
      |> Expect.equal "nothing has read it and kept it" ValueVerdict.SafeToPatch

    testCase "WHY — a read that threw the value away never holds it, however often it ran" <| fun _ ->
      let cctor = reader "cctor" [ "M.greeting", 0, ReadFate.Discarded ]
      ledgerOf
        [ LedgerEvent.ValueTracked "M.greeting"
          LedgerEvent.ReaderFound(cctor, ReaderStatus.Unwatchable "a static initializer")
          LedgerEvent.GetterRead("M.greeting", Caller.Known "cctor") ]
      |> fun l -> Ledger.evidence l "M.greeting" |> verdictOf
      |> Expect.equal "a dead local holds nothing" ValueVerdict.SafeToPatch

    testCase "WHY — a lazy thunk forced after startup is a capture, because the Lazy caches what the thunk returned" <| fun _ ->
      let thunk = reader "lazyMotto@12.Invoke" [ "M.motto", 0, ReadFate.Escaped(Escape.PassedTo "System.String.ToUpper") ]
      let ledger =
        ledgerOf
          [ LedgerEvent.ValueTracked "M.motto"
            LedgerEvent.ReaderFound(thunk, ReaderStatus.Armed)
            LedgerEvent.StartupEnded
            LedgerEvent.ReaderRan "lazyMotto@12.Invoke" ]
      match Ledger.evidence ledger "M.motto" |> verdictOf with
      | ValueVerdict.HeldBy(site, ReadSeen.AfterStartup) -> site.Reader |> Expect.equal "the thunk" "lazyMotto@12.Invoke"
      | other -> failtestf "expected HeldBy the thunk after startup, got %A" other

    testCase "WHY — a caller whose code has no read of the value (reflection) is a capture, because SageFs can't see what it did with it" <| fun _ ->
      ledgerOf [ LedgerEvent.ValueTracked "M.x"; LedgerEvent.GetterRead("M.x", Caller.Unknown "Some.Serializer.Write") ]
      |> fun l -> Ledger.evidence l "M.x" |> verdictOf
      |> function
        | ValueVerdict.HeldBy(site, _) ->
          site.Where |> Expect.equal "no read in its code" SiteLocation.NotInItsCode
          site.Reader |> Expect.equal "named" "Some.Serializer.Write"
        | other -> failtestf "expected HeldBy the unknown caller, got %A" other

    testCase "WHY — a reader SageFs can't watch counts as having run, because not knowing isn't proof" <| fun _ ->
      let r = reader "Generic`1.Read" [ "M.x", 4, ReadFate.Escaped Escape.Returned ]
      ledgerOf [ LedgerEvent.ValueTracked "M.x"; LedgerEvent.ReaderFound(r, ReaderStatus.Unwatchable "a generic method") ]
      |> fun l -> Ledger.evidence l "M.x" |> verdictOf
      |> function
        | ValueVerdict.HeldBy(_, ReadSeen.Unobservable why) -> why |> Expect.equal "the reason travels" "a generic method"
        | other -> failtestf "expected an unobservable hold, got %A" other

    testCase "WHY — a value SageFs doesn't track can't be proven safe, so it isn't" <| fun _ ->
      ledgerOf [ LedgerEvent.ValueUntracked("M.x", "the assembly was built with optimizations") ]
      |> fun l -> Ledger.evidence l "M.x" |> verdictOf
      |> Expect.equal "cannot tell" (ValueVerdict.CannotTell "the assembly was built with optimizations")

    testCase "WHY — a value SageFs never heard of can't be proven safe either" <| fun _ ->
      match Ledger.evidence Ledger.empty "M.nope" |> verdictOf with
      | ValueVerdict.CannotTell _ -> ()
      | other -> failtestf "expected CannotTell, got %A" other

    testCase "WHY — a probe that fires after startup says so, and one that fires during it says that" <| fun _ ->
      let r = reader "r" [ "M.x", 0, captured ]
      let atStartup = ledgerOf [ LedgerEvent.ValueTracked "M.x"; LedgerEvent.ReaderFound(r, ReaderStatus.Armed); LedgerEvent.ReaderRan "r" ]
      let after = ledgerOf [ LedgerEvent.ValueTracked "M.x"; LedgerEvent.ReaderFound(r, ReaderStatus.Armed); LedgerEvent.StartupEnded; LedgerEvent.ReaderRan "r" ]
      atStartup.Status |> Map.find "r" |> Expect.equal "ran during startup" (ReaderStatus.Ran ReadSeen.AtStartup)
      after.Status |> Map.find "r" |> Expect.equal "ran after it" (ReaderStatus.Ran ReadSeen.AfterStartup)

    testCase "WHY — the holder is described by where the value went, so the restart reason says who kept it" <| fun _ ->
      let site = { Reader = "<StartupCode$App>.$App.State..cctor"; Where = SiteLocation.ILOffset 217; Fate = ReadFate.Escaped(Escape.CapturedBy "App.State+handlers@78-9") }
      let text = describeHolder site ReadSeen.AtStartup
      text |> Expect.stringContains "names the reader" "<StartupCode$App>.$App.State..cctor"
      text |> Expect.stringContains "names what it built around the value" "App.State+handlers@78-9"
      text |> Expect.stringContains "says when" "started"

    testCase "WHY — a save is refused when any one value is held, and says which" <| fun _ ->
      let held = ValueVerdict.HeldBy({ Reader = "r"; Where = SiteLocation.ILOffset 0; Fate = captured }, ReadSeen.AtStartup)
      checkSave [ "M.a", ValueVerdict.SafeToPatch; "M.b", held ]
      |> Expect.equal "refused for M.b" (SaveCheck.Refused(("M.b", held), []))
      checkSave [ "M.a", ValueVerdict.SafeToPatch ] |> Expect.equal "all safe" SaveCheck.AllSafe

    testProperty "WHY — the verdict is SafeToPatch exactly when no read was kept, because a kept read is the one thing a patch can't reach" <| fun (reads: (bool * bool) list) ->
      let site = { Reader = "r"; Where = SiteLocation.ILOffset 0; Fate = captured }
      let valueReads =
        reads
        |> List.map (fun (escaped, ran) ->
          match escaped, ran with
          | false, _ -> ValueRead.ThrownAway { site with Fate = ReadFate.Discarded }
          | true, false -> ValueRead.NotRunYet site
          | true, true -> ValueRead.Kept(site, ReadSeen.AfterStartup))
      let anyKept = reads |> List.exists (fun (escaped, ran) -> escaped && ran)
      match verdictOf (ValueEvidence.Tracked("M.x", valueReads)), anyKept with
      | ValueVerdict.SafeToPatch, false -> true
      | ValueVerdict.HeldBy _, true -> true
      | _ -> false
  ]
