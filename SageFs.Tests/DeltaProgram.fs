/// A small random method-body language, and what the metadata-delta emitter is checked against.
///
/// A program is a static class `Gen.Prog` of integer methods `m0 .. mN` (`int -> int`), one generic method
/// `G<T>(T, int)`, and a handful of closure classes with an instance `Invoke(int)` and a captured field.
/// Method bodies are built from arithmetic, a conditional, a local, a call to an earlier method, a call to a
/// closure, a call to the generic method, a string literal, `Math.Abs`, a `List<int>` built from an array, and
/// a try/catch. That is enough to need every kind of row a delta adds (MemberRef, TypeRef, TypeSpec,
/// MethodSpec, StandAloneSig, #US strings, an AssemblyRef the baseline never had) and an exception region.
///
/// Three things use it:
///   * `interpret` says what a program computes, with no compiler involved. It is the oracle.
///   * `compile` writes the program to a DLL with Mono.Cecil, unoptimized (DebuggableAttribute 0x103), so a
///     process started with DOTNET_MODIFIABLE_ASSEMBLIES=debug will take a delta for it.
///   * `mutate` makes the next version of a program: new bodies, new line numbers in the closure names (the
///     compiler moves those when a line is added above them), and sometimes an added method.
module SageFs.Tests.DeltaProgram

open System
open System.Collections.Generic
open System.IO
open FsCheck
open FsCheck.FSharp
open Mono.Cecil
open Mono.Cecil.Cil
open Mono.Cecil.Rocks

type BinOp =
  | Add
  | Sub
  | Mul
  | Div

type Expr =
  | Lit of int
  /// The method's `int` argument (the closure's `x`, the generic method's `n`).
  | Arg
  /// The method's local.
  | Var
  /// The closure's captured `k`.
  | Captured
  | Bin of BinOp * Expr * Expr
  /// `if a < b then c else d`.
  | Cond of Expr * Expr * Expr * Expr
  | CallM of int * Expr
  | StrLen of string
  | Abs of Expr
  /// The count of a `List<int>` built from an array of this many items.
  | ListCount of int
  /// `G<int>(a, b)`.
  | CallG of Expr * Expr
  /// `new Closure_i(k).Invoke(x)`.
  | CallClosure of int * Expr * Expr
  /// Sets the local, then evaluates the body.
  | Let of Expr * Expr

type Body =
  | Plain of Expr
  /// `try { a } catch (Exception) { b }`, at the top of a method only.
  | Guarded of Expr * Expr

type Closure =
  { /// The line number the compiler would put in the closure's name.
    Line: int
    Body: Expr }

type Program =
  { Methods: Body list
    /// `G<T>(T x, int n) = x.ToString().Length + <this over n>`.
    G: Expr
    Closures: Closure list
    /// How many methods the program started with. A method past this is one an edit added, and it only
    /// ever calls nothing, so no chain of edits can make two methods call each other.
    Fixed: int }

// -- reading ---------------------------------------------------------------------------------------------------

let rec private uses (predicate: Expr -> bool) (e: Expr) : bool =
  predicate e
  || (match e with
      | Bin (_, a, b)
      | CallG (a, b)
      | Let (a, b) -> uses predicate a || uses predicate b
      | CallClosure (_, k, x) -> uses predicate k || uses predicate x
      | Cond (a, b, c, d) -> [ a; b; c; d ] |> List.exists (uses predicate)
      | CallM (_, a)
      | Abs a -> uses predicate a
      | Lit _ | Arg | Var | Captured | StrLen _ | ListCount _ -> false)

/// The locals a body declares: none, one (for `Let` and `Var`), or two (a try/catch keeps its result in the second).
let private localCount (body: Body) : int =
  match body with
  | Guarded _ -> 2
  | Plain e ->
    match uses (function Let _ | Var -> true | _ -> false) e with
    | true -> 1
    | false -> 0

let methodName (index: int) : string = sprintf "m%d" index

/// Closures with the same base name are told apart only by their order, which is what the differ has to cope with.
let closureName (index: int) (closure: Closure) : string = sprintf "clo%d@%d-%d" (index / 2) closure.Line index

/// What a method does on an input, as the text a run prints.
let inputs : int list = [ -3; 0; 1; 7; 100 ]

// -- the oracle ------------------------------------------------------------------------------------------------

/// What an evaluation produced: a number, or the name of the exception that ended it.
let private attempt (f: unit -> int) : string =
  try string (f ()) with e -> "exc:" + e.GetType().Name

let rec private eval (program: Program) (locals: int array) (arg: int) (captured: int) (e: Expr) : int =
  let go = eval program locals arg captured
  match e with
  | Lit n -> n
  | Arg -> arg
  | Var -> locals[0]
  | Captured -> captured
  | Bin (op, a, b) ->
    let x = go a
    let y = go b
    match op with
    | Add -> x + y
    | Sub -> x - y
    | Mul -> x * y
    | Div -> x / y
  | Cond (a, b, c, d) ->
    let x = go a
    let y = go b
    match x < y with
    | true -> go c
    | false -> go d
  | CallM (index, a) -> callMethod program index (go a)
  | StrLen s -> s.Length
  | Abs a -> Math.Abs(go a)
  | ListCount n -> n
  | CallG (a, b) ->
    let x = go a
    let n = go b
    callG program (string x) n
  | CallClosure (index, k, x) ->
    let capturedValue = go k
    let value = go x
    eval program (Array.zeroCreate 2) value capturedValue program.Closures[index].Body
  | Let (a, body) ->
    locals[0] <- go a
    go body

and private callMethod (program: Program) (index: int) (x: int) : int =
  let locals = Array.zeroCreate 2
  match program.Methods[index] with
  | Plain e -> eval program locals x 0 e
  | Guarded (a, b) ->
    try eval program locals x 0 a
    with _ -> eval program locals x 0 b

and private callG (program: Program) (text: string) (n: int) : int =
  text.Length + eval program (Array.zeroCreate 2) n 0 program.G

/// Everything `evaluateLines` prints for a program: one line per method per input, and the generic method with an
/// int and a string always and with a double only when `late` (a first instantiation after an update).
let interpret (program: Program) (late: bool) : string list =
  [ for i in 0 .. program.Methods.Length - 1 do
      for x in inputs do
        yield sprintf "%s(%d)=%s" (methodName i) x (attempt (fun () -> callMethod program i x))
    for k in inputs do
      yield sprintf "G<int>(7,%d)=%s" k (attempt (fun () -> callG program "7" k))
      yield sprintf "G<string>(abc,%d)=%s" k (attempt (fun () -> callG program "abc" k))
      match late with
      | true -> yield sprintf "G<double>(1.5,%d)=%s" k (attempt (fun () -> callG program "1.5" k))
      | false -> () ]

// -- generating ------------------------------------------------------------------------------------------------

/// Whether an expression sits in a closure, where the captured field exists.
[<RequireQualifiedAccess>]
type CapturedPolicy =
  | HasCaptured
  | NoCaptured

/// Where an expression sits, which decides what it may call.
type Context =
  /// A method body: may call these methods and any closure, and the generic method.
  | InMethod of callable: int list * closures: int
  /// A closure, the generic method, or an added method: no calls.
  | Leaf of CapturedPolicy

/// All of the generators, in order, as one.
let private sequence (gens: Gen<'a> list) : Gen<'a list> =
  List.foldBack
    (fun (g: Gen<'a>) (rest: Gen<'a list>) ->
      gen {
        let! x = g
        let! xs = rest
        return x :: xs
      })
    gens
    (Gen.constant [])

let private strings =[ ""; "a"; "hello"; "héllo wörld"; "a much longer literal than the others, to move the heap along"; "tab\there" ]

let private literal : Gen<Expr> = Gen.choose (-9, 9) |> Gen.map Lit

let rec genExpr (context: Context) (depth: int) : Gen<Expr> =
  let leaves =
    [ literal
      Gen.constant Arg
      Gen.map StrLen (Gen.elements strings)
      Gen.map ListCount (Gen.choose (0, 5)) ]
    @ (match context with
       | InMethod _ -> [ Gen.constant Var ]
       | Leaf CapturedPolicy.HasCaptured -> [ Gen.constant Captured ]
       | Leaf CapturedPolicy.NoCaptured -> [])
  let leaf = Gen.oneof leaves
  match depth <= 0 with
  | true -> leaf
  | false ->
    let sub = genExpr context (depth - 1)
    let calls =
      match context with
      | Leaf _ -> []
      | InMethod (callable, closures) ->
        [ yield 1, Gen.map2 (fun a b -> CallG (a, b)) sub sub
          yield 1, Gen.map2 (fun a b -> Let (a, b)) sub sub
          match callable with
          | [] -> ()
          | _ -> yield 2, Gen.map2 (fun i a -> CallM (i, a)) (Gen.elements callable) sub
          match closures with
          | 0 -> ()
          | n -> yield 2, Gen.map3 (fun i k x -> CallClosure (i, k, x)) (Gen.choose (0, n - 1)) sub sub ]
    Gen.frequency
      ([ 4, leaf
         3, Gen.map3 (fun op a b -> Bin (op, a, b)) (Gen.elements [ Add; Sub; Mul; Div ]) sub sub
         2, Gen.map4 (fun a b c d -> Cond (a, b, c, d)) sub sub sub sub
         1, Gen.map Abs sub ]
       @ calls)

let private genBody (context: Context) : Gen<Body> =
  Gen.frequency
    [ 4, Gen.map Plain (genExpr context 3)
      1, Gen.map2 (fun a b -> Guarded (a, b)) (genExpr context 3) (genExpr context 2) ]

let private genLine : Gen<int> = Gen.choose (1, 400)

let private genClosure : Gen<Closure> =
  Gen.map2 (fun line body -> { Line = line; Body = body }) genLine (genExpr (Leaf CapturedPolicy.HasCaptured) 3)

let genProgram : Gen<Program> =
  gen {
    let! methodCount = Gen.choose (2, 5)
    let! closureCount = Gen.choose (0, 4)
    let! closures = Gen.listOfLength closureCount genClosure
    let! methods =
      [ for i in 0 .. methodCount - 1 -> genBody (InMethod ([ 0 .. i - 1 ], closureCount)) ]
      |> sequence
    let! g = genExpr (Leaf CapturedPolicy.NoCaptured) 3
    return { Methods = methods; G = g; Closures = closures; Fixed = methodCount }
  }

/// The next version of a program: some bodies replaced, every closure's line moved, sometimes a method added.
/// Nothing changes a signature, a field or the number of closures, so the delta can carry all of it.
let genNextVersion (program: Program) : Gen<Program> =
  gen {
    let! addMethod = Gen.frequency [ 2, Gen.constant true; 3, Gen.constant false ]
    let count = program.Methods.Length
    let closures = program.Closures.Length
    let total = count + (match addMethod with | true -> 1 | false -> 0)
    // A method the program started with calls an earlier one or any added one. An added method calls nothing.
    let contextFor (i: int) : Context =
      match i < program.Fixed with
      | true -> InMethod ([ 0 .. i - 1 ] @ [ program.Fixed .. total - 1 ], closures)
      | false -> Leaf CapturedPolicy.NoCaptured
    let! methods =
      program.Methods
      |> List.mapi (fun i current ->
        Gen.frequency
          [ 2, Gen.constant current
            3, genBody (contextFor i) ])
      |> sequence
    let! added =
      match addMethod with
      | true -> Gen.map (fun e -> [ Plain e ]) (genExpr (Leaf CapturedPolicy.NoCaptured) 3)
      | false -> Gen.constant []
    let! g = Gen.frequency [ 3, Gen.constant program.G; 1, genExpr (Leaf CapturedPolicy.NoCaptured) 3 ]
    let! moved =
      program.Closures
      |> List.map (fun c ->
        Gen.frequency
          [ 2, Gen.map (fun line -> { c with Line = line }) genLine
            1, Gen.map2 (fun line body -> { Line = line; Body = body }) genLine (genExpr (Leaf CapturedPolicy.HasCaptured) 3) ])
      |> sequence
    return { Methods = methods @ added; G = g; Closures = moved; Fixed = program.Fixed }
  }

/// A program and the next version of it, from one seed: the whole case replays from the number.
let casesOf (seed: uint64) (count: int) (chain: int) : (uint64 * Program list) list =
  let one (s: uint64) =
    let first = Gen.sampleWithSeed (Rnd s) 10 1 genProgram |> Array.head
    let rec next (n: int) (current: Program) (acc: Program list) (rnd: uint64) =
      match n with
      | 0 -> List.rev acc
      | _ ->
        let following = Gen.sampleWithSeed (Rnd (s * 7919UL + rnd)) 10 1 (genNextVersion current) |> Array.head
        next (n - 1) following (following :: acc) (rnd + 1UL)
    s, next chain first [ first ] 1UL
  [ for i in 0UL .. uint64 count - 1UL -> one (seed + i) ]

// -- compiling -------------------------------------------------------------------------------------------------

let private debuggableAttribute (module': ModuleDefinition) : CustomAttribute =
  let modes = typeof<System.Diagnostics.DebuggableAttribute.DebuggingModes>
  let ctor = module'.ImportReference(typeof<System.Diagnostics.DebuggableAttribute>.GetConstructor [| modes |])
  let attribute = CustomAttribute ctor
  // Default | IgnoreSymbolStoreSequencePoints | DisableOptimizations, the 0x103 an F# Optimize=false build writes.
  attribute.ConstructorArguments.Add(CustomAttributeArgument(module'.ImportReference modes, 0x103))
  attribute

type private Env =
  { Module: ModuleDefinition
    Methods: MethodDefinition array
    G: MethodDefinition
    ClosureCtors: MethodDefinition array
    ClosureInvokes: MethodDefinition array
    ClosureFields: FieldDefinition array
    StringLength: MethodReference
    Abs: MethodReference
    ListFromEnumerable: MethodReference
    ListCount: MethodReference
    ObjectToString: MethodReference }

let rec private emit (il: ILProcessor) (env: Env) (argument: int) (e: Expr) : unit =
  let go = emit il env argument
  match e with
  | Lit n -> il.Emit(OpCodes.Ldc_I4, n)
  | Arg ->
    match argument with
    | 0 -> il.Emit OpCodes.Ldarg_0
    | _ -> il.Emit OpCodes.Ldarg_1
  | Var -> il.Emit(OpCodes.Ldloc_0)
  | Captured ->
    il.Emit(OpCodes.Ldarg_0)
    // Closure i's field: the closure being compiled is the only one that reads it, so any field works.
    il.Emit(OpCodes.Ldfld, env.ClosureFields[0])
  | Bin (op, a, b) ->
    go a
    go b
    il.Emit(
      match op with
      | Add -> OpCodes.Add
      | Sub -> OpCodes.Sub
      | Mul -> OpCodes.Mul
      | Div -> OpCodes.Div)
  | Cond (a, b, c, d) ->
    let elseLabel = il.Create OpCodes.Nop
    let endLabel = il.Create OpCodes.Nop
    go a
    go b
    il.Emit(OpCodes.Bge, elseLabel)
    go c
    il.Emit(OpCodes.Br, endLabel)
    il.Append elseLabel
    go d
    il.Append endLabel
  | CallM (index, a) ->
    go a
    il.Emit(OpCodes.Call, env.Methods[index])
  | StrLen s ->
    il.Emit(OpCodes.Ldstr, s)
    il.Emit(OpCodes.Callvirt, env.StringLength)
  | Abs a ->
    go a
    il.Emit(OpCodes.Call, env.Abs)
  | ListCount n ->
    il.Emit(OpCodes.Ldc_I4, n)
    il.Emit(OpCodes.Newarr, env.Module.TypeSystem.Int32)
    il.Emit(OpCodes.Newobj, env.ListFromEnumerable)
    il.Emit(OpCodes.Callvirt, env.ListCount)
  | CallG (a, b) ->
    go a
    go b
    let instance = GenericInstanceMethod(env.G)
    instance.GenericArguments.Add env.Module.TypeSystem.Int32
    il.Emit(OpCodes.Call, instance)
  | CallClosure (index, k, x) ->
    go k
    il.Emit(OpCodes.Newobj, env.ClosureCtors[index])
    go x
    il.Emit(OpCodes.Call, env.ClosureInvokes[index])
  | Let (a, body) ->
    go a
    il.Emit OpCodes.Stloc_0
    go body

let private emitBody (env: Env) (m: MethodDefinition) (body: Body) : unit =
  let processor = m.Body.GetILProcessor()
  // The oracle reads a local nobody set as 0, which is what a zero-initialising body gives.
  m.Body.InitLocals <- true
  for _ in 1 .. localCount body do
    m.Body.Variables.Add(VariableDefinition env.Module.TypeSystem.Int32)
  match body with
  | Plain e ->
    emit processor env 0 e
    processor.Emit OpCodes.Ret
  | Guarded (a, b) ->
    let tryStart = processor.Create OpCodes.Nop
    let handlerStart = processor.Create OpCodes.Pop
    let finish = processor.Create OpCodes.Ldloc_1
    processor.Append tryStart
    emit processor env 0 a
    processor.Emit OpCodes.Stloc_1
    processor.Emit(OpCodes.Leave, finish)
    processor.Append handlerStart
    emit processor env 0 b
    processor.Emit OpCodes.Stloc_1
    processor.Emit(OpCodes.Leave, finish)
    processor.Append finish
    processor.Emit OpCodes.Ret
    let handler = ExceptionHandler ExceptionHandlerType.Catch
    handler.TryStart <- tryStart
    handler.TryEnd <- handlerStart
    handler.HandlerStart <- handlerStart
    handler.HandlerEnd <- finish
    handler.CatchType <- env.Module.ImportReference typeof<exn>
    m.Body.ExceptionHandlers.Add handler

/// Write a program to a DLL.
let compile (program: Program) (path: string) : unit =
  let assembly = AssemblyDefinition.CreateAssembly(AssemblyNameDefinition("Gen", Version(1, 0, 0, 0)), "Gen", ModuleKind.Dll)
  let module' = assembly.MainModule
  assembly.CustomAttributes.Add(debuggableAttribute module')
  let int32 = module'.TypeSystem.Int32
  let objectType = module'.TypeSystem.Object
  let prog =
    TypeDefinition(
      "Gen", "Prog",
      TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed ||| TypeAttributes.BeforeFieldInit,
      objectType)
  module'.Types.Add prog
  let staticMethod (name: string) =
    let m = MethodDefinition(name, MethodAttributes.Public ||| MethodAttributes.Static ||| MethodAttributes.HideBySig, int32)
    prog.Methods.Add m
    m
  let methods =
    program.Methods
    |> List.mapi (fun i _ ->
      let m = staticMethod (methodName i)
      m.Parameters.Add(ParameterDefinition("x", ParameterAttributes.None, int32))
      m)
    |> List.toArray
  // A compiler's output always has a #US heap. Cecil leaves it out of a module with no string literal, which is
  // a module no delta can add a first literal to, so every program keeps one literal that nothing reads.
  let label = MethodDefinition("Label", MethodAttributes.Public ||| MethodAttributes.Static ||| MethodAttributes.HideBySig, module'.TypeSystem.String)
  prog.Methods.Add label
  let labelIl = label.Body.GetILProcessor()
  labelIl.Emit(OpCodes.Ldstr, "anchor")
  labelIl.Emit OpCodes.Ret
  let g = staticMethod "G"
  let parameter = GenericParameter("T", g)
  g.GenericParameters.Add parameter
  g.Parameters.Add(ParameterDefinition("x", ParameterAttributes.None, parameter))
  g.Parameters.Add(ParameterDefinition("n", ParameterAttributes.None, int32))
  // Closures: a nested class with a captured int and an `Invoke(int)`.
  let closures =
    program.Closures
    |> List.mapi (fun i closure ->
      let t =
        TypeDefinition("", closureName i closure, TypeAttributes.NestedAssembly ||| TypeAttributes.Sealed ||| TypeAttributes.BeforeFieldInit, objectType)
      prog.NestedTypes.Add t
      let field = FieldDefinition("k", FieldAttributes.Public, int32)
      t.Fields.Add field
      let ctor =
        MethodDefinition(
          ".ctor",
          MethodAttributes.Public ||| MethodAttributes.HideBySig ||| MethodAttributes.SpecialName ||| MethodAttributes.RTSpecialName,
          module'.TypeSystem.Void)
      ctor.Parameters.Add(ParameterDefinition("k", ParameterAttributes.None, int32))
      t.Methods.Add ctor
      let invoke = MethodDefinition("Invoke", MethodAttributes.Public ||| MethodAttributes.HideBySig, int32)
      invoke.Parameters.Add(ParameterDefinition("x", ParameterAttributes.None, int32))
      t.Methods.Add invoke
      t, ctor, invoke, field)
    |> List.toArray
  let env =
    { Module = module'
      Methods = methods
      G = g
      ClosureCtors = closures |> Array.map (fun (_, ctor, _, _) -> ctor)
      ClosureInvokes = closures |> Array.map (fun (_, _, invoke, _) -> invoke)
      ClosureFields = closures |> Array.map (fun (_, _, _, field) -> field)
      StringLength = module'.ImportReference(typeof<string>.GetProperty("Length").GetGetMethod())
      Abs = module'.ImportReference(typeof<Math>.GetMethod("Abs", [| typeof<int> |]))
      ListFromEnumerable = module'.ImportReference(typeof<List<int>>.GetConstructor [| typeof<IEnumerable<int>> |])
      ListCount = module'.ImportReference(typeof<List<int>>.GetProperty("Count").GetGetMethod())
      ObjectToString = module'.ImportReference(typeof<obj>.GetMethod("ToString", Type.EmptyTypes)) }
  List.zip program.Methods (Array.toList methods) |> List.iter (fun (body, m) -> emitBody env m body)
  // G<T>(x, n) = x.ToString().Length + <e over n>
  let gil = g.Body.GetILProcessor()
  gil.Emit OpCodes.Ldarg_0
  gil.Emit(OpCodes.Box, parameter)
  gil.Emit(OpCodes.Callvirt, env.ObjectToString)
  gil.Emit(OpCodes.Callvirt, env.StringLength)
  emit gil env 1 program.G
  gil.Emit OpCodes.Add
  gil.Emit OpCodes.Ret
  for i in 0 .. closures.Length - 1 do
    let _, ctor, invoke, field = closures[i]
    let cil = ctor.Body.GetILProcessor()
    cil.Emit OpCodes.Ldarg_0
    cil.Emit(OpCodes.Call, module'.ImportReference(typeof<obj>.GetConstructor Type.EmptyTypes))
    cil.Emit OpCodes.Ldarg_0
    cil.Emit OpCodes.Ldarg_1
    cil.Emit(OpCodes.Stfld, field)
    cil.Emit OpCodes.Ret
    let iil = invoke.Body.GetILProcessor()
    // The captured field of THIS closure: emit through an env whose first field is this closure's.
    emit iil { env with ClosureFields = [| field |] } 1 program.Closures[i].Body
    iil.Emit OpCodes.Ret
  for m in prog.Methods do
    m.Body.OptimizeMacros()
  for _, _, invoke, _ in closures do
    invoke.Body.OptimizeMacros()
  Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
  assembly.Write path

/// Compile every version of a program into its own folder under `directory`, and say where each DLL is.
let compileAll (directory: string) (versions: Program list) : string list =
  versions
  |> List.mapi (fun i program ->
    let path = Path.Combine(directory, sprintf "v%d" i, "Gen.dll")
    compile program path
    path)

/// Render a program as text, for the message of a failing case.
let describe (program: Program) : string = sprintf "%A" program
