/// The metadata-delta emitter against a process that really applies its output.
///
/// The claim is about a running process, so the checks that matter apply deltas in child processes
/// (see DeltaChild): a program written in the small language of DeltaProgram is compiled to a DLL, loaded
/// with DOTNET_MODIFIABLE_ASSEMBLIES=debug, run, patched by the emitter's delta, and run again, and the
/// answers have to be the ones the oracle gives for the NEXT version of the program. A case is one seed, so
/// a red run names the number that replays it.
///
/// Each check that has to be able to fail has a twin: the same case run through a deliberately wrong
/// emitter (a chain that forgot its last delta, a diff that dropped a change). A twin that comes back green
/// means the check has no teeth.
module SageFs.Tests.MetadataDeltaTests

open System
open System.Collections.Generic
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Mono.Cecil
open Mono.Cecil.Cil
open SageFs.Features.LiveTesting
open SageFs.Features.MetadataDelta
open SageFs.Tests.DeltaProgram
open SageFs.Tests.DeltaChild

// -- scratch ---------------------------------------------------------------------------------------------------

let private scratch () : string =
  let directory = Path.Combine(Path.GetTempPath(), sprintf "sagefs-delta-%s" (Guid.NewGuid().ToString "N"))
  Directory.CreateDirectory directory |> ignore
  directory

let private removeQuietly (directory: string) : unit =
  try Directory.Delete(directory, true) with _ -> ()

/// A deterministic edit id, so a case replays to the same bytes.
let private encIdFor (seed: uint64) (generation: int) : Guid =
  Guid(int (seed &&& 0xFFFFFFFFUL), int16 generation, 0s, [| 1uy; 2uy; 3uy; 4uy; 5uy; 6uy; 7uy; 8uy |])

// -- the emitter, driven the way the planner will drive it -----------------------------------------------------

/// A wrong emitter, to show a check can fail.
[<RequireQualifiedAccess>]
type Twin =
  | Honest
  /// The second delta is prepared from the chain as it was before the first applied.
  | ChainForgetsItsLastDelta
  /// The first changed method is reported unchanged, so its delta is never written.
  | DiffDropsAChange

type private Prepared =
  { Directory: string
    /// The delta files in order, as `(meta, il)` names relative to the directory. An unchanged version has none.
    Deltas: (string * string) list
    /// The version each delta takes the process to, in the same order.
    Versions: int list
    Problems: string list }

/// The delta files for each step of a chain of versions, written beside the baseline copy.
let private prepare (twin: Twin) (seed: uint64) (probes: ProbeStripping) (directory: string) (paths: string list) : Prepared =
  let baselineCopy = Path.Combine(directory, "base", "Gen.dll")
  Directory.CreateDirectory(Path.GetDirectoryName baselineCopy) |> ignore
  File.Copy(List.head paths, baselineCopy, true)
  let start = DeltaChain.Start(PeImage.OfFile baselineCopy, probes)
  let mutable chain = start
  let deltas = ResizeArray<string * string>()
  let versions = ResizeArray<int>()
  let problems = ResizeArray<string>()
  for i in 1 .. paths.Length - 1 do
    let next = PeImage.OfFile paths[i]
    let encId = encIdFor seed i
    let outcome =
      match twin, i with
      | Twin.ChainForgetsItsLastDelta, 2 -> start.Prepare(encId, next)
      | Twin.DiffDropsAChange, 1 ->
        let diff = MethodDiff.diff { PreviousProbes = probes } (PeImage.OfFile baselineCopy) next
        let dropped = ref false
        let tampered =
          { diff with
              Methods =
                diff.Methods
                |> List.map (fun v ->
                  match v.Change, dropped.Value with
                  | MethodChange.BodyChanged, false ->
                    dropped.Value <- true
                    { v with Change = MethodChange.Unchanged }
                  | _ -> v) }
        chain.PrepareFrom(encId, next, tampered)
      | _ -> chain.Prepare(encId, next)
    match outcome with
    | PrepareOutcome.NothingChanged -> ()
    | PrepareOutcome.Refused causes ->
      problems.Add(sprintf "version %d was refused: %s" i (String.Join("; ", causes |> List.map RudeCause.describe)))
    | PrepareOutcome.Ready prepared ->
      let meta = sprintf "d%d.meta" i
      let il = sprintf "d%d.il" i
      File.WriteAllBytes(Path.Combine(directory, meta), prepared.Payload.Metadata)
      File.WriteAllBytes(Path.Combine(directory, il), prepared.Payload.Il)
      deltas.Add((meta, il))
      versions.Add i
      match twin, i with
      | Twin.ChainForgetsItsLastDelta, 1 -> ()
      | _ -> chain <- chain.Commit prepared
  { Directory = directory
    Deltas = List.ofSeq deltas
    Versions = List.ofSeq versions
    Problems = List.ofSeq problems }

/// What one case came to.
type private CaseResult =
  { Seed: uint64
    Problems: string list
    Program: string }

let private firstDifference (actual: string list) (expected: string list) : string =
  match List.zip (List.truncate expected.Length actual) (List.truncate actual.Length expected) |> List.tryFind (fun (a, e) -> a <> e) with
  | Some (a, e) -> sprintf "got %s, wanted %s" a e
  | None -> sprintf "got %d lines, wanted %d" actual.Length expected.Length

/// Compile a case, patch it version by version in a child, and compare every answer with the oracle.
let private runCase (twin: Twin) (probes: ProbeStripping) (instrument: bool) (seed: uint64, versions: Program list) : Task<CaseResult> =
  task {
    let directory = scratch ()
    try
      let paths = compileAll directory versions
      let baselinePath = List.head paths
      if instrument then
        match SageFs.Tests.DeltaInstrumentation.instrumentInPlace baselinePath with
        | Result.Ok _ -> ()
        | Result.Error message -> failwithf "instrumenting the baseline failed: %s" message
      let prepared = prepare twin seed probes directory paths
      // The versions that came out with a delta, in order. A version that came out unchanged has none.
      let script =
        [ yield "load base/Gen.dll"
          yield "eval"
          for (meta, il) in prepared.Deltas do
            yield sprintf "apply %s %s" meta il
            yield "eval"
          yield "evallate" ]
      let! result = DeltaChild.run directory ModifiableAssemblies.Debug script
      let blocks = DeltaChild.evalBlocks result
      let applies = DeltaChild.applyOutcomes result
      let problems = ResizeArray<string>(prepared.Problems)
      match result.ExitCode with
      | 0 -> ()
      | code -> problems.Add(sprintf "the child exited %d: %s" code result.Stderr)
      for outcome in applies do
        match outcome with
        | "Applied" -> ()
        | other -> problems.Add(sprintf "the runtime answered %s" other)
      // The child evaluates before any apply, after each one, and last with the generic method at a type that
      // has not run. The version each evaluation has to match: the baseline, then the version each delta took
      // the process to, then the final one (versions after the last delta are unchanged, so it is the same process).
      let evaluationVersions = [ 0 ] @ prepared.Versions @ [ versions.Length - 1 ]
      match blocks.Length = evaluationVersions.Length with
      | false -> problems.Add(sprintf "the child printed %d evaluations, wanted %d" blocks.Length evaluationVersions.Length)
      | true ->
        evaluationVersions
        |> List.iteri (fun k version ->
          let expected = interpret versions[version] (k = evaluationVersions.Length - 1)
          match blocks[k] = expected with
          | true -> ()
          | false -> problems.Add(sprintf "evaluation %d (version %d): %s" k version (firstDifference blocks[k] expected)))
      match problems.Count, result.Stderr with
      | 0, _ | _, "" -> ()
      | _, stderr -> problems.Add(sprintf "child stderr: %s" (stderr.Substring(0, min 600 stderr.Length)))
      return
        { Seed = seed
          Problems = List.ofSeq problems
          Program = String.Join("\n---\n", versions |> List.map describe) }
    finally
      removeQuietly directory
  }

/// How many children run at once. Each is a process that loads the runtime.
let private concurrentChildren = 4

/// How many times more seeds a run takes than the gate does: SAGEFS_DELTA_SCALE=20 for a soak, unset for the gate.
let private scale : int =
  match Int32.TryParse(Environment.GetEnvironmentVariable "SAGEFS_DELTA_SCALE") with
  | true, n when n > 0 -> n
  | _ -> 1

let private cases (seed: uint64) (count: int) (chain: int) : (uint64 * Program list) list = casesOf seed (count * scale) chain

let private runCases (twin: Twin) (probes: ProbeStripping) (instrument: bool) (cases: (uint64 * Program list) list) : Task<CaseResult list> =
  task {
    use gate = new SemaphoreSlim(concurrentChildren)
    let one (case: uint64 * Program list) : Task<CaseResult> =
      task {
        do! gate.WaitAsync()
        try
          try
            return! runCase twin probes instrument case
          with e ->
            return { Seed = fst case; Problems = [ e.Message ]; Program = "" }
        finally
          gate.Release() |> ignore
      }
    let! all = cases |> List.map one |> Task.WhenAll
    return List.ofArray all
  }

let private report (results: CaseResult list) : string =
  results
  |> List.filter (fun r -> not r.Problems.IsEmpty)
  |> List.map (fun r -> sprintf "seed %d: %s\n%s" r.Seed (String.Join(" | ", r.Problems)) r.Program)
  |> String.concat "\n\n"

// -- classification fixtures -----------------------------------------------------------------------------------

/// A program with closures that share a base name and one that does not, a method that calls them, and a
/// method that calls a method. Small enough that every refusal below has one obvious cause.
let private fixedProgram : Program =
  { Methods =
      [ Plain (Bin (Add, Arg, StrLen "fixed"))
        Plain (Bin (Add, CallClosure (0, Lit 1, Arg), CallClosure (1, Lit 2, Arg)))
        Plain (CallM (0, Arg)) ]
    G = Arg
    Closures =
      [ { Line = 10; Body = Bin (Add, Captured, Arg) }
        { Line = 20; Body = Bin (Mul, Captured, Arg) }
        { Line = 30; Body = Captured } ]
    Fixed = 3 }

let private rewrite (source: string) (target: string) (edit: AssemblyDefinition -> unit) : unit =
  let assembly = AssemblyDefinition.ReadAssembly source
  edit assembly
  assembly.Write target
  assembly.Dispose()

let private progType (assembly: AssemblyDefinition) : TypeDefinition = assembly.MainModule.GetType "Gen.Prog"

let private closureType (assembly: AssemblyDefinition) (index: int) : TypeDefinition =
  progType(assembly).NestedTypes |> Seq.find (fun t -> t.Name.StartsWith(sprintf "clo%d@" (index / 2), StringComparison.Ordinal) && t.Name.EndsWith(sprintf "-%d" index, StringComparison.Ordinal))

let private method' (t: TypeDefinition) (name: string) : MethodDefinition = t.Methods |> Seq.find (fun m -> m.Name = name)

/// Give the baseline a static constructor, which the refusal for one changing needs to have something to change.
let private withStaticConstructor (assembly: AssemblyDefinition) : unit =
  let prog = progType assembly
  let cctor =
    MethodDefinition(
      ".cctor",
      MethodAttributes.Private ||| MethodAttributes.Static ||| MethodAttributes.SpecialName ||| MethodAttributes.RTSpecialName ||| MethodAttributes.HideBySig,
      assembly.MainModule.TypeSystem.Void)
  cctor.Body.GetILProcessor().Emit OpCodes.Ret
  prog.Methods.Add cctor

/// Make an Invoke virtual, so a signature change to it is a change to every override.
let private withVirtualInvoke (assembly: AssemblyDefinition) : unit =
  let invoke = method' (closureType assembly 2) "Invoke"
  invoke.Attributes <- invoke.Attributes ||| MethodAttributes.Virtual ||| MethodAttributes.NewSlot

/// The shape of each way an edit cannot be carried: how to make it, and the cause the emitter has to name.
let private rudeEdits : (string * (AssemblyDefinition -> unit) * string) list =
  [ "a closure class is removed",
    (fun a -> progType(a).NestedTypes.Remove(closureType a 2) |> ignore),
    "TypeRemoved"
    "a type is added",
    (fun a -> progType(a).NestedTypes.Add(TypeDefinition("", "brandnew", TypeAttributes.NestedAssembly, a.MainModule.TypeSystem.Object))),
    "TypeAdded"
    "a closure gains a field",
    (fun a -> (closureType a 2).Fields.Add(FieldDefinition("extra", FieldAttributes.Public, a.MainModule.TypeSystem.Int32))),
    "FieldsChanged"
    "a type's flags change",
    (fun a ->
      let t = closureType a 2
      t.Attributes <- t.Attributes &&& ~~~TypeAttributes.Sealed),
    "TypeShapeChanged"
    "a property is added",
    (fun a -> progType(a).Properties.Add(PropertyDefinition("P", PropertyAttributes.None, a.MainModule.TypeSystem.Int32))),
    "PropertiesOrEventsChanged"
    "a method gains a custom attribute",
    (fun a ->
      let ctor = a.MainModule.ImportReference(typeof<ObsoleteAttribute>.GetConstructor Type.EmptyTypes)
      (method' (progType a) "m0").CustomAttributes.Add(CustomAttribute ctor)),
    "AttributesChanged"
    "a method is removed",
    (fun a -> progType(a).Methods.Remove(method' (progType a) "m1") |> ignore),
    "MethodRemoved"
    "a method changes its parameter type",
    (fun a -> (method' (progType a) "m0").Parameters[0].ParameterType <- a.MainModule.TypeSystem.Int64),
    "SignatureChanged"
    "a method's parameter is renamed",
    (fun a -> (method' (progType a) "m0").Parameters[0].Name <- "renamed"),
    "ParametersChanged"
    "a method changes its flags",
    (fun a -> (method' (closureType a 2) "Invoke").Attributes <- MethodAttributes.Assembly ||| MethodAttributes.HideBySig),
    "MethodFlagsChanged"
    "a virtual member changes its signature",
    (fun a -> (method' (closureType a 2) "Invoke").Parameters[0].ParameterType <- a.MainModule.TypeSystem.Int64),
    "VirtualSignatureChanged"
    "a virtual method is added",
    (fun a ->
      let m = MethodDefinition("extra", MethodAttributes.Public ||| MethodAttributes.Virtual ||| MethodAttributes.NewSlot ||| MethodAttributes.HideBySig, a.MainModule.TypeSystem.Int32)
      let il = m.Body.GetILProcessor()
      il.Emit(OpCodes.Ldc_I4_0)
      il.Emit(OpCodes.Ret)
      (closureType a 2).Methods.Add m),
    "AddedMethodUnsupported"
    "a static constructor changes",
    (fun a ->
      let il = (method' (progType a) ".cctor").Body.GetILProcessor()
      let first = il.Body.Instructions[0]
      il.InsertBefore(first, il.Create OpCodes.Nop)),
    "StaticInitializerChanged"
    "a closure is added to a group of closures that share a name",
    (fun a -> progType(a).NestedTypes.Add(TypeDefinition("", "clo0@99-9", TypeAttributes.NestedAssembly, a.MainModule.TypeSystem.Object))),
    "ClosureMatchAmbiguous"
    "a body uses calli",
    (fun a ->
      let m = method' (progType a) "m0"
      let il = m.Body.GetILProcessor()
      m.Body.Instructions.Clear()
      il.Emit(OpCodes.Ldc_I4_0)
      il.Emit(OpCodes.Conv_I)
      il.Emit(OpCodes.Calli, CallSite(a.MainModule.TypeSystem.Int32))
      il.Emit(OpCodes.Ret)),
    "UnreadableBody" ]

/// The causes the diff of two compiles names, by case.
let private causeNames (diff: ImageDiff) : string list =
  ImageDiff.causes diff |> List.map RudeCause.caseName |> List.distinct |> List.sort

[<Tests>]
let metadataDeltaTests =
  testList "metadata delta emitter" [

    // -- the switch and the closed sets ---------------------------------------------------------------------

    testCase "WHY - the delta path is off unless the process turns it on, and only the named spellings do" <| fun _ ->
      [ null, MetadataDeltaMode.Off
        "", MetadataDeltaMode.Off
        "0", MetadataDeltaMode.Off
        "off", MetadataDeltaMode.Off
        "yes please", MetadataDeltaMode.Off
        "1", MetadataDeltaMode.On
        "on", MetadataDeltaMode.On
        " TRUE ", MetadataDeltaMode.On ]
      |> List.iter (fun (text, expected) ->
        MetadataDeltaMode.parse text |> Expect.equal (sprintf "%A means %A" text expected) expected)
      MetadataDeltaMode.toText MetadataDeltaMode.On |> Expect.equal "on is spelled on" "on"

    testCase "WHY - every rude cause has its own name and its own words, so a row can say which one it was" <| fun _ ->
      let causes =
        [ RudeCause.TypeRemoved "T"
          RudeCause.TypeAdded "T"
          RudeCause.FieldsChanged "T"
          RudeCause.TypeShapeChanged "T"
          RudeCause.PropertiesOrEventsChanged "T"
          RudeCause.AttributesChanged "S"
          RudeCause.MethodRemoved ("T", "m")
          RudeCause.SignatureChanged ("T", "m")
          RudeCause.VirtualSignatureChanged ("T", "m")
          RudeCause.MethodFlagsChanged ("T", "m")
          RudeCause.ParametersChanged ("T", "m")
          RudeCause.AddedMethodUnsupported ("T", "m", "why")
          RudeCause.StaticInitializerChanged ("T", "m")
          RudeCause.ClosureMatchAmbiguous "T"
          RudeCause.UnreadableBody ("T", "m", IlRefusal.CalliNotSupported) ]
      causes |> List.map RudeCause.caseName |> List.distinct |> List.length |> Expect.equal "one name per case" causes.Length
      causes |> List.map RudeCause.describe |> List.distinct |> List.length |> Expect.equal "one wording per case" causes.Length
      let numberOfCases = Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<RudeCause>).Length
      causes.Length |> Expect.equal "this list names every case of the union" numberOfCases

    // -- the differ -----------------------------------------------------------------------------------------

    testCase "WHY - a recompile that only moves the lines closures are written on changes nothing, even though every closure is renamed" <| fun _ ->
      let directory = scratch ()
      try
        let failures =
          casesOf 500UL 20 1
          |> List.choose (fun (seed, versions) ->
            let program = List.head versions
            let moved = { program with Closures = program.Closures |> List.map (fun c -> { c with Line = c.Line + 7 }) }
            let paths = compileAll (Path.Combine(directory, string seed)) [ program; moved ]
            let diff = MethodDiff.diff { PreviousProbes = ProbeStripping.KeepEveryInstruction } (PeImage.OfFile paths[0]) (PeImage.OfFile paths[1])
            let changed = diff.Methods |> List.filter (fun v -> v.Change <> MethodChange.Unchanged)
            match changed, diff.Refusals with
            | [], [] -> None
            | _ -> Some (sprintf "seed %d: %A %A" seed changed diff.Refusals))
        failures |> Expect.isEmpty (String.Join("\n", failures))
        // A baseline and the same program compiled again are the same, and a delta of nothing is no delta.
        let paths = compileAll (Path.Combine(directory, "same")) [ fixedProgram; fixedProgram ]
        match (DeltaChain.Start(PeImage.OfFile paths[0], ProbeStripping.KeepEveryInstruction)).Prepare(Guid.NewGuid(), PeImage.OfFile paths[1]) with
        | PrepareOutcome.NothingChanged -> ()
        | other -> failtestf "two compiles of one program should be no change, and this said %A" other
      finally
        removeQuietly directory

    testCase "WHY - an edit to one body changes that method and nothing else" <| fun _ ->
      let directory = scratch ()
      try
        let failures =
          [ 0 .. fixedProgram.Methods.Length - 1 ]
          |> List.choose (fun index ->
            let edited = { fixedProgram with Methods = fixedProgram.Methods |> List.mapi (fun i b -> match i = index with | true -> Plain (Bin (Add, Lit 424242, Arg)) | false -> b) }
            let paths = compileAll (Path.Combine(directory, string index)) [ fixedProgram; edited ]
            let diff = MethodDiff.diff { PreviousProbes = ProbeStripping.KeepEveryInstruction } (PeImage.OfFile paths[0]) (PeImage.OfFile paths[1])
            let changed = diff.Methods |> List.filter (fun v -> v.Change <> MethodChange.Unchanged) |> List.map (fun v -> v.Method.Name, v.Change)
            match changed, diff.Refusals with
            | [ name, MethodChange.BodyChanged ], [] when name = methodName index -> None
            | _ -> Some (sprintf "method %d: %A %A" index changed diff.Refusals))
        failures |> Expect.isEmpty (String.Join("\n", failures))
      finally
        removeQuietly directory

    testCase "WHY - a method the running assembly does not have is Added, and a closure body edit is a body change of that closure's Invoke" <| fun _ ->
      let directory = scratch ()
      try
        let edited =
          { fixedProgram with
              Methods = fixedProgram.Methods @ [ Plain (Bin (Add, Arg, Lit 5)) ]
              Closures = fixedProgram.Closures |> List.mapi (fun i c -> match i with | 0 -> { c with Body = Bin (Sub, Captured, Arg) } | _ -> c) }
        let paths = compileAll directory [ fixedProgram; edited ]
        let diff = MethodDiff.diff { PreviousProbes = ProbeStripping.KeepEveryInstruction } (PeImage.OfFile paths[0]) (PeImage.OfFile paths[1])
        diff.Refusals |> Expect.isEmpty "nothing here needs a restart"
        diff.Methods
        |> List.filter (fun v -> v.Change <> MethodChange.Unchanged)
        |> List.map (fun v -> v.Method.TypeKey, v.Method.Name, v.Change)
        |> List.sort
        |> Expect.equal "the added method and the one closure's Invoke" [ "Gen.Prog", "m3", MethodChange.Added; "Gen.Prog+clo0@_#0", "Invoke", MethodChange.BodyChanged ]
      finally
        removeQuietly directory

    testCase "WHY - each edit a delta cannot carry is refused with its own cause" <| fun _ ->
      let directory = scratch ()
      try
        let baseline = Path.Combine(directory, "baseline.dll")
        compile fixedProgram (Path.Combine(directory, "plain.dll"))
        rewrite (Path.Combine(directory, "plain.dll")) baseline (fun a ->
          withStaticConstructor a
          withVirtualInvoke a)
        let failures =
          rudeEdits
          |> List.choose (fun (what, edit, expected) ->
            let target = Path.Combine(directory, sprintf "%s.dll" (expected + string (what.GetHashCode())))
            rewrite baseline target edit
            let diff = MethodDiff.diff { PreviousProbes = ProbeStripping.KeepEveryInstruction } (PeImage.OfFile baseline) (PeImage.OfFile target)
            let names = causeNames diff
            match List.contains expected names with
            | true -> None
            | false -> Some (sprintf "%s should be %s and was %A" what expected names))
        failures |> Expect.isEmpty (String.Join("\n", failures))
        // And the refusal reaches the planner's door: Prepare answers Refused with the same causes, writing nothing.
        let target = Path.Combine(directory, "refused.dll")
        rewrite baseline target (fun a -> progType(a).Methods.Remove(method' (progType a) "m1") |> ignore)
        match (DeltaChain.Start(PeImage.OfFile baseline, ProbeStripping.KeepEveryInstruction)).Prepare(Guid.NewGuid(), PeImage.OfFile target) with
        | PrepareOutcome.Refused causes ->
          causes |> List.map RudeCause.caseName |> List.contains "MethodRemoved" |> Expect.isTrue "the refusal names MethodRemoved"
        | other -> failtestf "removing a method should be refused, and this said %A" other
      finally
        removeQuietly directory

    testCase "WHY - a baseline with no user-string heap cannot take a literal, and the refusal says so instead of writing a delta the runtime reads wrongly" <| fun _ ->
      let directory = scratch ()
      try
        let bare = { Methods = [ Plain Arg ]; G = Arg; Closures = []; Fixed = 1 }
        let withLiteral = { bare with Methods = [ Plain (StrLen "x") ] }
        let paths = compileAll directory [ bare; withLiteral ]
        let baseline = Path.Combine(directory, "no-strings.dll")
        rewrite paths[0] baseline (fun a -> progType(a).Methods.Remove(method' (progType a) "Label") |> ignore)
        (PeImage.OfFile baseline).Reader.GetHeapSize(HeapIndex.UserString)
        |> Expect.equal "Cecil left the #US heap out of a module with no literal, which is what this case is about" 0
        match (DeltaChain.Start(PeImage.OfFile baseline, ProbeStripping.KeepEveryInstruction)).Prepare(Guid.NewGuid(), PeImage.OfFile paths[1]) with
        | PrepareOutcome.Refused causes ->
          causes
          |> List.exists (function RudeCause.UnreadableBody (_, _, IlRefusal.NoUserStringHeap) -> true | _ -> false)
          |> Expect.isTrue (sprintf "the refusal names the missing heap: %A" causes)
        | other -> failtestf "a literal into a module with no #US heap should be refused, and this said %A" other
      finally
        removeQuietly directory

    // -- the delta, applied --------------------------------------------------------------------------------

    testTask "WHY - a delta takes a running process to the next version of the program, for every instantiation, closure and added method" {
      let! results = runCases Twin.Honest ProbeStripping.KeepEveryInstruction false (cases 1000UL 24 1)
      results |> List.filter (fun r -> not r.Problems.IsEmpty) |> List.length |> Expect.equal (report results) 0
    }

    testTask "WHY - two and three deltas in a row each see what the one before added: the chain carries heaps, row counts and rows" {
      let! two = runCases Twin.Honest ProbeStripping.KeepEveryInstruction false (cases 2000UL 12 2)
      let! three = runCases Twin.Honest ProbeStripping.KeepEveryInstruction false (cases 3000UL 8 3)
      let all = two @ three
      all |> List.filter (fun r -> not r.Problems.IsEmpty) |> List.length |> Expect.equal (report all) 0
    }

    testTask "TWIN - a chain that forgot its last delta is caught: the same cases, run through it, come back red" {
      let! results = runCases Twin.ChainForgetsItsLastDelta ProbeStripping.KeepEveryInstruction false (casesOf 2000UL 12 2)
      (results |> List.filter (fun r -> not r.Problems.IsEmpty) |> List.length, 0) |> Expect.isGreaterThan "the check has teeth"
    }

    testTask "TWIN - a diff that drops a change is caught: the process keeps serving the old body" {
      let! results = runCases Twin.DiffDropsAChange ProbeStripping.KeepEveryInstruction false (casesOf 1000UL 24 1)
      (results |> List.filter (fun r -> not r.Problems.IsEmpty) |> List.length, 0) |> Expect.isGreaterThan "the check has teeth"
    }

    // -- the baseline the process actually holds ------------------------------------------------------------

    testTask "WHY - the baseline is the Cecil-instrumented module the worker loads, and a delta computed against it takes the process to the next version" {
      let! results = runCases Twin.Honest (ProbeStripping.StripCoverageProbes CoverageProbe.hitSymbol) true (cases 4000UL 16 1)
      results |> List.filter (fun r -> not r.Problems.IsEmpty) |> List.length |> Expect.equal (report results) 0
    }

    testCase "WHY - without looking through the probes every method of an instrumented baseline reads as edited, which is why the differ looks through them" <| fun _ ->
      let directory = scratch ()
      try
        let paths = compileAll directory [ fixedProgram; fixedProgram ]
        match SageFs.Tests.DeltaInstrumentation.instrumentInPlace paths[0] with
        | Result.Error message -> failtestf "instrumenting failed: %s" message
        | Result.Ok probes ->
          let baseline = PeImage.OfFile paths[0]
          let next = PeImage.OfFile paths[1]
          let keeping = MethodDiff.diff { PreviousProbes = ProbeStripping.KeepEveryInstruction } baseline next
          let stripping = MethodDiff.diff { PreviousProbes = ProbeStripping.StripCoverageProbes CoverageProbe.hitSymbol } baseline next
          // The tracker is the instrumenter's own type: a clean build does not have it, so only the stripped diff may ignore it.
          ((keeping.Methods |> List.filter (fun v -> v.Change = MethodChange.BodyChanged)).Length, 0)
          |> Expect.isGreaterThan "every instrumented method differs from the clean build"
          stripping.Methods |> List.filter (fun v -> v.Change <> MethodChange.Unchanged) |> Expect.isEmpty "looking through the probes, nothing changed"
          stripping.Refusals |> Expect.isEmpty "and the tracker class is not a removed type"
          // Contract with the instrumenter: the symbol this module strips is the one the instrumenter calls.
          let hits (image: PeImage) (stripped: ProbeStripping) =
            image.Reader.MethodDefinitions
            |> Seq.sumBy (fun h ->
              let m = image.Reader.GetMethodDefinition h
              match m.RelativeVirtualAddress with
              | 0 -> 0
              | rva ->
                match IlCanon.canonicalize image (image.Pe.GetMethodBody rva) stripped with
                | Result.Ok body ->
                  body.Instructions |> Array.filter (fun i -> i.Operand = Operand.Symbol CoverageProbe.hitSymbol) |> Array.length
                | Result.Error _ -> 0)
          hits baseline ProbeStripping.KeepEveryInstruction |> Expect.equal "every probe the instrumenter wrote is a call to the symbol" probes
          hits baseline (ProbeStripping.StripCoverageProbes CoverageProbe.hitSymbol) |> Expect.equal "and none is left once they are looked through" 0
      finally
        removeQuietly directory

    // -- what the runtime says -------------------------------------------------------------------------------

    testTask "WHY - a process that did not start with DOTNET_MODIFIABLE_ASSEMBLIES says so before and after the call, and the apply is a closed outcome, not a throw" {
      let directory = scratch ()
      try
        let paths = compileAll directory [ fixedProgram; { fixedProgram with Methods = [ Plain (Lit 1); Plain (Lit 2); Plain (Lit 3) ] } ]
        let prepared = prepare Twin.Honest 1UL ProbeStripping.KeepEveryInstruction directory paths
        let meta, il = List.head prepared.Deltas
        let baseline = "base/Gen.dll"
        let! result =
          DeltaChild.run directory ModifiableAssemblies.NotSet [ sprintf "load %s" baseline; "capability"; sprintf "check %s %s" meta il; sprintf "apply %s %s" meta il ]
        result.ExitCode |> Expect.equal (sprintf "the child lived: %s" result.Stderr) 0
        result.Facts |> List.filter (fun f -> f.StartsWith "CHECK") |> List.exists (fun f -> f.Contains "DOTNET_MODIFIABLE_ASSEMBLIES")
        |> Expect.isTrue (sprintf "the check names the missing variable: %A" result.Facts)
        applyOutcomes result |> List.head |> Expect.stringStarts "the runtime refuses and the outcome says it cannot edit the assembly" "RuntimeNotModifiable"
      finally
        removeQuietly directory
    }

    testTask "WHY - a module built with optimizations is named as the reason before anything is applied" {
      let directory = scratch ()
      try
        let paths = compileAll directory [ fixedProgram; { fixedProgram with Methods = [ Plain (Lit 1); Plain (Lit 2); Plain (Lit 3) ] } ]
        let prepared = prepare Twin.Honest 1UL ProbeStripping.KeepEveryInstruction directory paths
        let meta, il = List.head prepared.Deltas
        // Strip the DebuggableAttribute from the baseline copy, which is what a Release build is.
        let optimized = Path.Combine(directory, "optimized")
        Directory.CreateDirectory optimized |> ignore
        rewrite (Path.Combine(directory, "base", "Gen.dll")) (Path.Combine(optimized, "Gen.dll")) (fun a -> a.CustomAttributes.Clear())
        File.Copy(Path.Combine(directory, meta), Path.Combine(optimized, meta))
        File.Copy(Path.Combine(directory, il), Path.Combine(optimized, il))
        let! result = DeltaChild.run optimized ModifiableAssemblies.Debug [ "load Gen.dll"; sprintf "check %s %s" meta il; sprintf "apply %s %s" meta il ]
        result.ExitCode |> Expect.equal (sprintf "the child lived: %s" result.Stderr) 0
        result.Facts |> List.exists (fun f -> f.StartsWith "CHECK" && f.Contains "optimizations")
        |> Expect.isTrue (sprintf "the check says the module is optimized: %A" result.Facts)
        applyOutcomes result |> List.head |> Expect.stringStarts "and the runtime agrees" "RuntimeNotModifiable"
      finally
        removeQuietly directory
    }

    testTask "WHY - a process started the way a hot reload app is started reports it can take a delta" {
      let directory = scratch ()
      try
        let! result = DeltaChild.run directory ModifiableAssemblies.Debug [ "capability" ]
        result.ExitCode |> Expect.equal (sprintf "the child lived: %s" result.Stderr) 0
        let line = result.Facts |> List.find (fun f -> f.StartsWith "CAPABILITY")
        line |> Expect.stringContains "updates are supported" "support=Supported"
        line |> Expect.stringContains "the environment is the debug one" "environment=Debug"
        line |> Expect.stringContains "and the runtime lists the baseline capability" "Baseline"
      finally
        removeQuietly directory
    }
  ]
