/// Hot reloading a generic function: which instantiations the program can reach, which of them share one
/// compiled body, and the real detours that move every one of those bodies.
///
/// The runtime compiles a generic method once for each value-type instantiation that runs and ONE body for all
/// reference types. These tests read IL emitted into a dynamic assembly (so what the program calls is exactly
/// what the test says) and detour real generic functions of this assembly, then read what each instantiation
/// returns. The real-app rows are in `HotReloadParityTests.fs`.
module SageFs.Tests.GenericReloadTests

open System
open System.Reflection
open System.Reflection.Emit
open System.Runtime.CompilerServices
open Expecto
open Expecto.Flip
open SageFs.Middleware
open SageFs.Middleware.EntryProbes
open SageFs.Middleware.HotReloadCore

let private logger : SageFs.Utils.ILogger =
  { new SageFs.Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

// ── a program whose IL says exactly what it calls ────────────────────────────

/// What a test program does with the generic method `Echo<T>(T)`.
[<RequireQualifiedAccess>]
type private Call =
  /// A static method that calls `Echo<T>` over this type.
  | Over of Type
  /// `Outer<T>(T)` calls `Echo<T>`, and a static method calls `Outer<T>` over this type.
  | ThroughGeneric of Type
  /// A method that mentions `MethodInfo.MakeGenericMethod`. It never runs.
  | MakesGenericMethod
  /// A method that mentions `Type.MakeGenericType`. It never runs.
  | MakesGenericType

let private program (calls: Call list) : Type * MethodInfo =
  let name = sprintf "GenericReloadFixture_%s" (Guid.NewGuid().ToString "N")
  let assembly = AssemblyBuilder.DefineDynamicAssembly(AssemblyName name, AssemblyBuilderAccess.Run)
  let modul = assembly.DefineDynamicModule name
  let prog = modul.DefineType("Prog", TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed)
  let statics = MethodAttributes.Public ||| MethodAttributes.Static
  let echo = prog.DefineMethod("Echo", statics)
  let echoParameter = echo.DefineGenericParameters [| "T" |]
  echo.SetSignature(echoParameter.[0], null, null, [| echoParameter.[0] :> Type |], null, null)
  let echoIl = echo.GetILGenerator()
  echoIl.Emit OpCodes.Ldarg_0
  echoIl.Emit OpCodes.Ret
  let outer = prog.DefineMethod("Outer", statics)
  let outerParameter = outer.DefineGenericParameters [| "T" |]
  outer.SetSignature(outerParameter.[0], null, null, [| outerParameter.[0] :> Type |], null, null)
  let outerIl = outer.GetILGenerator()
  outerIl.Emit OpCodes.Ldarg_0
  outerIl.Emit(OpCodes.Call, echo.MakeGenericMethod [| outerParameter.[0] :> Type |])
  outerIl.Emit OpCodes.Ret
  calls
  |> List.iteri (fun i call ->
    let m = prog.DefineMethod(sprintf "Caller%d" i, statics, typeof<Void>, [||])
    let il = m.GetILGenerator()
    let callOver (target: MethodInfo) (t: Type) =
      let local = il.DeclareLocal t
      il.Emit(OpCodes.Ldloc, local)
      il.Emit(OpCodes.Call, target)
      il.Emit OpCodes.Pop
    match call with
    | Call.Over t -> callOver (echo.MakeGenericMethod [| t |]) t
    | Call.ThroughGeneric t -> callOver (outer.MakeGenericMethod [| t |]) t
    | Call.MakesGenericMethod ->
      il.Emit OpCodes.Ldnull
      il.Emit OpCodes.Ldnull
      il.Emit(OpCodes.Callvirt, typeof<MethodInfo>.GetMethod("MakeGenericMethod", [| typeof<Type[]> |]))
      il.Emit OpCodes.Pop
    | Call.MakesGenericType ->
      il.Emit OpCodes.Ldnull
      il.Emit OpCodes.Ldnull
      il.Emit(OpCodes.Callvirt, typeof<Type>.GetMethod("MakeGenericType", [| typeof<Type[]> |]))
      il.Emit OpCodes.Pop
    il.Emit OpCodes.Ret)
  let created = prog.CreateType()
  created, created.GetMethod "Echo"

let private namesOf (reach: GenericReload.Reach) : string list list =
  reach.Instantiations
  |> Map.toList
  |> List.collect (fun (_, found) -> found |> List.map (fun args -> args |> Array.map (fun t -> t.Name) |> List.ofArray))
  |> List.sort

type private Box = { Value: int }

[<Struct>]
type private Point = { X: int }

// ── real generic functions to detour ─────────────────────────────────────────

[<MethodImpl(MethodImplOptions.NoInlining)>]
let private shapeOld<'T> (x: 'T) : string = "old:" + typeof<'T>.Name

[<MethodImpl(MethodImplOptions.NoInlining)>]
let private shapeNew<'T> (x: 'T) : string = "new:" + typeof<'T>.Name

[<MethodImpl(MethodImplOptions.NoInlining)>]
let private againOld<'T> (x: 'T) : string = "first:" + typeof<'T>.Name

[<MethodImpl(MethodImplOptions.NoInlining)>]
let private againSecond<'T> (x: 'T) : string = "second:" + typeof<'T>.Name

[<MethodImpl(MethodImplOptions.NoInlining)>]
let private againThird<'T> (x: 'T) : string = "third:" + typeof<'T>.Name

[<MethodImpl(MethodImplOptions.NoInlining)>]
let private pairOld<'A, 'B> (a: 'A) (b: 'B) : string = sprintf "old:%s/%s" typeof<'A>.Name typeof<'B>.Name

[<MethodImpl(MethodImplOptions.NoInlining)>]
let private pairNew<'A, 'B> (a: 'A) (b: 'B) : string = sprintf "new:%s/%s" typeof<'A>.Name typeof<'B>.Name

[<MethodImpl(MethodImplOptions.NoInlining)>]
let private structOld<'T> (x: 'T) : Point = { X = 1 }

[<MethodImpl(MethodImplOptions.NoInlining)>]
let private structNew<'T> (x: 'T) : Point = { X = 2 }

/// The members of a generic type: the type's arguments reach an instance member through the object and a static
/// member through the class, and the old and the new copy are laid out the same.
type private HolderOld<'T>(v: 'T) =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  member _.Show() : string = "old:" + typeof<'T>.Name + ":" + string v

  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Make(x: 'T) : string = "old:" + typeof<'T>.Name + ":" + string x

  [<MethodImpl(MethodImplOptions.NoInlining)>]
  member _.Pair<'U>(u: 'U) : string = "old:" + typeof<'T>.Name + "/" + typeof<'U>.Name

type private HolderNew<'T>(v: 'T) =
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  member _.Show() : string = "new:" + typeof<'T>.Name + ":" + string v

  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Make(x: 'T) : string = "new:" + typeof<'T>.Name + ":" + string x

  [<MethodImpl(MethodImplOptions.NoInlining)>]
  member _.Pair<'U>(u: 'U) : string = "new:" + typeof<'T>.Name + "/" + typeof<'U>.Name

let private holderMember (isOld: bool) (name: string) : Method =
  let t = match isOld with | true -> typedefof<HolderOld<_>> | false -> typedefof<HolderNew<_>>
  { MethodInfo = t.GetMethod(name, BindingFlags.Static ||| BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic)
    FullName = "GenericReloadTests.Holder." + name }

let private testModule = typeof<Box>.DeclaringType

let private definition (name: string) : Method =
  { MethodInfo = testModule.GetMethod(name, BindingFlags.Static ||| BindingFlags.NonPublic ||| BindingFlags.Public)
    FullName = "GenericReloadTests." + name }

let private noPlainDetours : DetourPlan = { Functions = []; MutableBindings = []; Declined = [] }

[<Tests>]
let tests =
  testList "hot reloading a generic function" [

    testList "which instantiations the program can reach" [

      testCase "WHY - an instantiation some method names is listed, whether it is a value type or a reference type" <| fun _ ->
        let created, echo = program [ Call.Over typeof<int>; Call.Over typeof<string>; Call.Over typeof<float> ]
        match GenericReload.reach [ created.Assembly ] [ echo ] with
        | Result.Error why -> failtestf "expected a list, got %s" (GenericReload.Unreachable.describe why)
        | Result.Ok reach ->
          namesOf reach |> Expect.equal "the three it is called with" [ [ "Double" ]; [ "Int32" ]; [ "String" ] ]

      testCase "WHY - a function nothing calls has no instantiation to patch" <| fun _ ->
        let created, _ = program [ Call.Over typeof<int> ]
        let other = created.GetMethod "Outer"
        match GenericReload.reach [ created.Assembly ] [ other ] with
        | Result.Error why -> failtestf "expected a list, got %s" (GenericReload.Unreachable.describe why)
        | Result.Ok reach -> namesOf reach |> Expect.equal "Outer is never called" []

      testCase "WHY - a generic function called from another generic function is reached with the type arguments that one is reached with" <| fun _ ->
        let created, echo = program [ Call.ThroughGeneric typeof<float>; Call.ThroughGeneric typeof<Box> ]
        match GenericReload.reach [ created.Assembly ] [ echo ] with
        | Result.Error why -> failtestf "expected a list, got %s" (GenericReload.Unreachable.describe why)
        | Result.Ok reach ->
          namesOf reach |> Expect.equal "Echo<float> and Echo<Box>, through Outer" [ [ "Box" ]; [ "Double" ] ]
          reach.GenericCarrier |> Expect.isTrue "Outer is a generic method that refers to it"

      testCase "WHY - a program that calls MakeGenericMethod can make an instantiation no code names, so the list is refused" <| fun _ ->
        let created, echo = program [ Call.Over typeof<int>; Call.MakesGenericMethod ]
        match GenericReload.reach [ created.Assembly ] [ echo ] with
        | Result.Error(GenericReload.Unreachable.MakesGenericMethods _) -> ()
        | other -> failtestf "expected MakesGenericMethods, got %A" other

      testCase "WHY - MakeGenericType only matters once a generic method or type refers to the function" <| fun _ ->
        let plain, plainEcho = program [ Call.Over typeof<int>; Call.MakesGenericType ]
        match GenericReload.reach [ plain.Assembly ] [ plainEcho ] with
        | Result.Ok reach -> namesOf reach |> Expect.equal "a plain caller is listed" [ [ "Int32" ] ]
        | Result.Error why -> failtestf "no generic refers to it, got %s" (GenericReload.Unreachable.describe why)
        let carrying, carryingEcho = program [ Call.ThroughGeneric typeof<int>; Call.MakesGenericType ]
        match GenericReload.reach [ carrying.Assembly ] [ carryingEcho ] with
        | Result.Error(GenericReload.Unreachable.MakesGenericTypes _) -> ()
        | other -> failtestf "a generic type could be made with any argument, got %A" other
    ]

    testList "which declaration a re-pointed member belongs to" [

      testCase "WHY - the runtime names a generic type with its number of type parameters, and the source does not, so a member still belongs to its type" <| fun _ ->
        let source = "module Parity\n\ntype GenHolder<'T>(v: 'T) =\n  member _.Show() : string = \"a\" + string v\n"
        match SageFs.Features.ReloadPlanning.extractDecls source with
        | Result.Error why -> failtestf "the source parses: %s" why
        | Result.Ok file ->
          match file.Decls |> List.tryFind (fun d -> d.Name = "GenHolder") with
          | None -> failtest "the type is a declaration"
          | Some decl ->
            SageFs.Features.ReloadPlanning.reachedBy [ "ParityFixture.Parity.GenHolder`1.Show" ] decl
            |> Expect.isTrue "a member of GenHolder`1 reaches GenHolder"
            SageFs.Features.ReloadPlanning.reachedBy [ "ParityFixture.Parity.GenHolderOther`1.Show" ] decl
            |> Expect.isFalse "a member of another type does not"
    ]

    testList "which instantiations share a body" [

      testCase "WHY - value types each have a body, and every reference type shares one" <| fun _ ->
        let bodies =
          GenericReload.bodiesOf [ [| typeof<int> |]; [| typeof<float> |]; [| typeof<string> |]; [| typeof<Box> |]; [| typeof<int list> |] ]
        let own = bodies |> List.choose (function GenericReload.Body.Own a -> Some a.[0].Name | _ -> None) |> List.sort
        let shared = bodies |> List.choose (function GenericReload.Body.Shared a -> Some a | _ -> None)
        own |> Expect.equal "int and float have their own" [ "Double"; "Int32" ]
        shared.Length |> Expect.equal "string, a record and a list are one body" 1

      testCase "WHY - the value types among the type arguments split the shared bodies: (string, int) and (string, float) are two" <| fun _ ->
        let bodies =
          GenericReload.bodiesOf
            [ [| typeof<string>; typeof<int> |]; [| typeof<Box>; typeof<int> |]; [| typeof<string>; typeof<float> |]; [| typeof<string>; typeof<string> |] ]
        bodies.Length |> Expect.equal "(*,int), (*,float), (*,*)" 3

      testCase "WHY - a struct that holds a reference type argument is shared like one, a plain struct is not" <| fun _ ->
        let bodies = GenericReload.bodiesOf [ [| typeof<struct (string * int)> |]; [| typeof<struct (Box * int)> |]; [| typeof<Point> |] ]
        bodies |> List.filter (function GenericReload.Body.Shared _ -> true | _ -> false) |> List.length
        |> Expect.equal "the two tuples are one shared body" 1
        bodies |> List.filter (function GenericReload.Body.Own _ -> true | _ -> false) |> List.length
        |> Expect.equal "the plain struct has its own" 1

      testCase "WHY - the canonical instance replaces every reference type by object and keeps the value types" <| fun _ ->
        GenericReload.canonicalInstance [| typeof<string>; typeof<int>; typeof<Box> |]
        |> Expect.equal "object, int, object" [| typeof<obj>; typeof<int>; typeof<obj> |]
    ]

    testList "the real detours" [

      testCase "WHY - one save moves every body: each instantiation gets the new body WITH ITS OWN type argument, listed or not" <| fun _ ->
        let older = definition "shapeOld"
        let newer = definition "shapeNew"
        // Compile the old bodies the way a running app has: int and string and a record ran already.
        shapeOld 1 |> Expect.equal "old int" "old:Int32"
        shapeOld "s" |> Expect.equal "old string" "old:String"
        shapeOld { Value = 1 } |> Expect.equal "old record" "old:Box"
        let listed = [ [| typeof<int> |]; [| typeof<string> |]; [| typeof<Box> |]; [| typeof<float> |] ]
        match prepareGenericUnit logger older newer listed with
        | Result.Error why -> failtestf "expected a unit, got %s" (GenericReload.Unreachable.describe why)
        | Result.Ok unit ->
          unit.Detours.Length |> Expect.equal "int, float, and the one shared body" 3
          let report = applyDetourPlan logger Map.empty noPlainDetours [ unit ]
          report.Failures |> Expect.equal "nothing failed" []
          report.Redirected |> Expect.contains "the function is reported re-pointed" older.FullName
          shapeOld 1 |> Expect.equal "int" "new:Int32"
          shapeOld "s" |> Expect.equal "string" "new:String"
          shapeOld { Value = 1 } |> Expect.equal "record, with the record's own type argument" "new:Box"
          // Not one of the listed instantiations, and not compiled before: a reference type is the shared body.
          shapeOld [ 1 ] |> Expect.equal "a reference type nobody listed" "new:FSharpList`1"
          // Listed, but first compiled after the patch.
          shapeOld 2.5 |> Expect.equal "a float that had never run" "new:Double"
          let readings = (ProbeRegistry.Shared.Read(report.Probes |> List.map _.Id)).Sightings
          readings |> List.map _.Status |> List.distinct |> Expect.equal "every probe has seen its new body run" [ ProbeStatus.Entered ]
          // The teeth: a value type nobody listed is compiled from the old IL, which is why the list has to be complete.
          shapeOld 3uy |> Expect.equal "an unlisted value type still has the old body" "old:Byte"

      testCase "WHY - a body shared by reference types is split by the value types beside them, and each split is patched on its own" <| fun _ ->
        let older = definition "pairOld"
        let newer = definition "pairNew"
        pairOld "a" 1 |> Expect.equal "old (string,int)" "old:String/Int32"
        pairOld "a" 2.5 |> Expect.equal "old (string,float)" "old:String/Double"
        let listed = [ [| typeof<string>; typeof<int> |]; [| typeof<Box>; typeof<int> |]; [| typeof<string>; typeof<float> |] ]
        match prepareGenericUnit logger older newer listed with
        | Result.Error why -> failtestf "expected a unit, got %s" (GenericReload.Unreachable.describe why)
        | Result.Ok unit ->
          unit.Detours.Length |> Expect.equal "(*,int) and (*,float)" 2
          let report = applyDetourPlan logger Map.empty noPlainDetours [ unit ]
          report.Failures |> Expect.equal "nothing failed" []
          pairOld "a" 1 |> Expect.equal "(string,int)" "new:String/Int32"
          pairOld { Value = 1 } 1 |> Expect.equal "(record,int)" "new:Box/Int32"
          pairOld "a" 2.5 |> Expect.equal "(string,float)" "new:String/Double"
          pairOld "a" "b" |> Expect.equal "(string,string) was not listed, so it is the old body" "old:String/String"

      testCase "WHY - the members of a generic type are re-pointed too: an instance member finds its type arguments in the object, a static one in the class, a generic one in the method" <| fun _ ->
        let objects = HolderOld("s"), HolderOld(1), HolderOld { Value = 3 }
        let s, i, r = objects
        s.Show() |> Expect.equal "old string" "old:String:s"
        i.Show() |> Expect.equal "old int" "old:Int32:1"
        HolderOld<string>.Make "m" |> Expect.equal "old static" "old:String:m"
        s.Pair 1 |> Expect.equal "old generic method" "old:String/Int32"
        let units =
          [ "Show"; "Make"; "Pair" ]
          |> List.map (fun name ->
            let args =
              match name with
              | "Pair" -> [ [| typeof<string>; typeof<int> |]; [| typeof<int>; typeof<string> |]; [| typeof<Box>; typeof<int> |] ]
              | _ -> [ [| typeof<string> |]; [| typeof<int> |]; [| typeof<Box> |]; [| typeof<float> |] ]
            match prepareGenericUnit logger (holderMember true name) (holderMember false name) args with
            | Result.Ok unit -> unit
            | Result.Error why -> failtestf "%s: expected a unit, got %s" name (GenericReload.Unreachable.describe why))
        let report = applyDetourPlan logger Map.empty noPlainDetours units
        report.Failures |> Expect.equal "nothing failed" []
        // Objects built BEFORE the save, of the OLD type, run the new member with their own type arguments.
        s.Show() |> Expect.equal "string object" "new:String:s"
        i.Show() |> Expect.equal "int object" "new:Int32:1"
        r.Show() |> Expect.equal "a record object, whose body the string object's detour covers" "new:Box:{ Value = 3 }"
        HolderOld<string>.Make "m" |> Expect.equal "static, string" "new:String:m"
        HolderOld<Box>.Make { Value = 1 } |> Expect.equal "static, record" "new:Box:{ Value = 1 }"
        HolderOld<float>.Make 2.5 |> Expect.equal "static, float, never compiled" "new:Double:2.5"
        s.Pair 1 |> Expect.equal "generic method of a generic type" "new:String/Int32"
        i.Pair "u" |> Expect.equal "the other way round" "new:Int32/String"
        r.Pair 1 |> Expect.equal "(record, int) shares the (string, int) body" "new:Box/Int32"
        // The teeth: (reference, reference) is a third shared body that nobody listed, so it is still the old one.
        r.Pair "u" |> Expect.equal "an unlisted shared body is the old body" "old:Box/String"

      testCase "WHY - a second save of the same function lands over the first, in the value-type bodies and in the shared one" <| fun _ ->
        let older = definition "againOld"
        let listed = [ [| typeof<int> |]; [| typeof<string> |]; [| typeof<Box> |] ]
        againOld 1 |> Expect.equal "before any save" "first:Int32"
        let save (newer: string) : unit =
          match prepareGenericUnit logger older (definition newer) listed with
          | Result.Error why -> failtestf "expected a unit, got %s" (GenericReload.Unreachable.describe why)
          | Result.Ok unit ->
            (applyDetourPlan logger Map.empty noPlainDetours [ unit ]).Failures |> Expect.equal "nothing failed" []
        save "againSecond"
        againOld 1 |> Expect.equal "after the first save, int" "second:Int32"
        againOld "s" |> Expect.equal "after the first save, string" "second:String"
        save "againThird"
        againOld 1 |> Expect.equal "after the second save, int" "third:Int32"
        againOld "s" |> Expect.equal "after the second save, string" "third:String"
        againOld { Value = 1 } |> Expect.equal "after the second save, record" "third:Box"

      testCase "WHY - a shared body that returns a struct is refused, because the stub cannot place the return buffer" <| fun _ ->
        let older = definition "structOld"
        let newer = definition "structNew"
        structOld "s" |> ignore
        match prepareGenericUnit logger older newer [ [| typeof<string> |] ] with
        | Result.Error(GenericReload.Unreachable.UnsupportedShape _) -> ()
        | other -> failtestf "expected UnsupportedShape, got %A" (other |> Result.map (fun u -> u.Detours.Length))
    ]
  ]
