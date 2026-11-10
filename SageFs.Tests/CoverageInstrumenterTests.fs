module SageFs.Tests.CoverageInstrumenterTests

open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open Mono.Cecil
open Mono.Cecil.Cil
open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting

/// Create a minimal assembly with a parent type and a nested closure type,
/// each having a method with sequence points.
let private createAssemblyWithNestedType () =
  let asm =
    AssemblyDefinition.CreateAssembly(
      AssemblyNameDefinition("TestNestedAsm", System.Version(1, 0, 0, 0)),
      "TestNested",
      ModuleKind.Dll)
  let modDef = asm.MainModule
  let voidRef = modDef.ImportReference(typeof<System.Void>)
  let int32Ref = modDef.ImportReference(typeof<int>)
  let objRef = modDef.ImportReference(typeof<obj>)

  let parentType =
    TypeDefinition(
      "TestNs", "ParentType",
      TypeAttributes.Public ||| TypeAttributes.Class, objRef)
  modDef.Types.Add(parentType)

  let parentMethod =
    MethodDefinition(
      "ParentMethod",
      MethodAttributes.Public ||| MethodAttributes.Static, voidRef)
  parentMethod.Body <- MethodBody(parentMethod)
  let il = parentMethod.Body.GetILProcessor()
  il.Append(il.Create(OpCodes.Ret))
  let doc = Document(@"C:\test\Foo.fs")
  let sp = SequencePoint(parentMethod.Body.Instructions.[0], doc)
  sp.StartLine <- 10; sp.StartColumn <- 5
  sp.EndLine <- 10; sp.EndColumn <- 20
  parentMethod.DebugInformation.SequencePoints.Add(sp)
  parentType.Methods.Add(parentMethod)

  // Nested type simulating an F# closure (e.g. moveToward@32)
  let nestedType =
    TypeDefinition(
      "", "moveToward@32",
      TypeAttributes.NestedPrivate ||| TypeAttributes.Class, objRef)
  parentType.NestedTypes.Add(nestedType)
  let nestedMethod =
    MethodDefinition("Invoke", MethodAttributes.Public, int32Ref)
  nestedMethod.Body <- MethodBody(nestedMethod)
  let nil = nestedMethod.Body.GetILProcessor()
  let nop1 = nil.Create(OpCodes.Ldc_I4_0)
  nil.Append(nop1)
  nil.Append(nil.Create(OpCodes.Ret))
  let nsp = SequencePoint(nop1, doc)
  nsp.StartLine <- 32; nsp.StartColumn <- 5
  nsp.EndLine <- 32; nsp.EndColumn <- 32
  nestedMethod.DebugInformation.SequencePoints.Add(nsp)
  nestedType.Methods.Add(nestedMethod)

  modDef

/// Assembly with two levels of nesting (closure capturing closure).
let private createAssemblyWithDeepNesting () =
  let asm =
    AssemblyDefinition.CreateAssembly(
      AssemblyNameDefinition("TestDeepAsm", System.Version(1, 0, 0, 0)),
      "TestDeep",
      ModuleKind.Dll)
  let modDef = asm.MainModule
  let voidRef = modDef.ImportReference(typeof<System.Void>)
  let objRef = modDef.ImportReference(typeof<obj>)
  let doc = Document(@"C:\test\Bar.fs")

  let parentType =
    TypeDefinition(
      "TestNs", "Parent",
      TypeAttributes.Public ||| TypeAttributes.Class, objRef)
  modDef.Types.Add(parentType)

  let addSeqPt (m: MethodDefinition) line =
    m.Body <- MethodBody(m)
    let il = m.Body.GetILProcessor()
    let instr = il.Create(OpCodes.Ret)
    il.Append(instr)
    let sp = SequencePoint(instr, doc)
    sp.StartLine <- line; sp.StartColumn <- 1
    sp.EndLine <- line; sp.EndColumn <- 10
    m.DebugInformation.SequencePoints.Add(sp)

  let pm =
    MethodDefinition(
      "Run",
      MethodAttributes.Public ||| MethodAttributes.Static, voidRef)
  addSeqPt pm 1
  parentType.Methods.Add(pm)

  let n1 =
    TypeDefinition(
      "", "outer@10",
      TypeAttributes.NestedPrivate ||| TypeAttributes.Class, objRef)
  parentType.NestedTypes.Add(n1)
  let n1m = MethodDefinition("Invoke", MethodAttributes.Public, voidRef)
  addSeqPt n1m 10
  n1.Methods.Add(n1m)

  let n2 =
    TypeDefinition(
      "", "inner@20",
      TypeAttributes.NestedPrivate ||| TypeAttributes.Class, objRef)
  n1.NestedTypes.Add(n2)
  let n2m = MethodDefinition("Invoke", MethodAttributes.Public, voidRef)
  addSeqPt n2m 20
  n2.Methods.Add(n2m)

  modDef

[<Tests>]
let coverageInstrumenterTests =
  testList "CoverageInstrumenter" [
    test "collectSequencePoints finds points in nested types" {
      let modDef = createAssemblyWithNestedType()
      let points = CoverageInstrumenter.collectSequencePoints modDef
      points
      |> Array.length
      |> Expect.equal "should find 2 points (parent+nested)" 2
    }

    test "includes correct line numbers from nested types" {
      let modDef = createAssemblyWithNestedType()
      let points = CoverageInstrumenter.collectSequencePoints modDef
      let lines =
        points
        |> Array.map (fun (_, sp: Mono.Cecil.Cil.SequencePoint, _) -> sp.StartLine)
        |> Array.sort
      lines
      |> Expect.equal "should have lines 10 and 32" [| 10; 32 |]
    }

    test "slot IDs are sequential across parent and nested" {
      let modDef = createAssemblyWithNestedType()
      let points = CoverageInstrumenter.collectSequencePoints modDef
      let slots = points |> Array.map (fun (_, _, slotId) -> slotId)
      slots |> Expect.equal "should be 0 and 1" [| 0; 1 |]
    }

    test "recurses through deeply nested types" {
      let modDef = createAssemblyWithDeepNesting()
      let points = CoverageInstrumenter.collectSequencePoints modDef
      let lines =
        points
        |> Array.map (fun (_, sp: Mono.Cecil.Cil.SequencePoint, _) -> sp.StartLine)
        |> Array.sort
      lines
      |> Expect.equal "should find all 3 nesting levels" [| 1; 10; 20 |]
    }

    // A debugger finds a module's symbols through the DLL's debug directory: the pdb path it names, then a file of that name
    // beside the DLL. The in-place instrumentation wrote the copy as `<name>.instrumented.dll`, so the directory named
    // `<name>.instrumented.pdb`, which the move renames to `<name>.pdb`; netcoredbg attached to a host then reported "no symbols
    // loaded" for the person's own assembly and no breakpoint in their test could bind (hand run, 2026-11-10).
    test "WHY: an assembly instrumented in place still has symbols a debugger finds beside it" {
      let directory = Directory.CreateTempSubdirectory("sagefs-instrument-").FullName
      let dll = Path.Combine(directory, "Gen.dll")
      let modDef = createAssemblyWithNestedType ()
      modDef.Assembly.Write(dll, WriterParameters(WriteSymbols = true, SymbolWriterProvider = PortablePdbWriterProvider()))
      let instrumented = CoverageInstrumenter.instrumentAssemblyInPlace dll
      instrumented
      |> Result.map (fun map -> map.TotalProbes)
      |> Expect.equal "both sequence points were instrumented" (Result.Ok 2)
      use image = new PEReader(File.OpenRead dll)
      let mutable provider : MetadataReaderProvider = null
      let mutable pdbPath : string = null
      let found =
        image.TryOpenAssociatedPortablePdb(
          dll,
          (fun path -> match File.Exists path with | true -> File.OpenRead path :> Stream | false -> null),
          &provider,
          &pdbPath)
      found |> Expect.isTrue "a portable pdb is found for the instrumented DLL"
      match provider with
      | null -> ()
      | opened -> opened.Dispose()
      Path.GetFileName pdbPath |> Expect.equal "it is the pdb the original had" "Gen.pdb"
      Directory.GetFileSystemEntries directory
      |> Array.map Path.GetFileName
      |> Array.sort
      |> Expect.equal "the staging folder is gone" [| "Gen.dll"; "Gen.pdb" |]
    }
  ]
