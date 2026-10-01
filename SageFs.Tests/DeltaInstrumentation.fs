/// Makes the baseline the way the worker's baseline is made: a DLL with debug information, run through the
/// real CoverageInstrumenter, so its methods carry a probe in front of every sequence point.
///
/// The programs DeltaProgram compiles have no PDB, so one is written here: a sequence point at the first
/// instruction of each method and at every branch target (the labels `Cond` makes), because a probe in front
/// of a branch target is the case that needs the differ to retarget a branch.
module SageFs.Tests.DeltaInstrumentation

open System.IO
open Mono.Cecil
open Mono.Cecil.Cil
open SageFs.Features.LiveTesting

/// Write sequence points and a portable PDB into the DLL at `path`.
let private withSequencePoints (path: string) : unit =
  let source = File.ReadAllBytes path
  use assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(source))
  let document = Document(Path.Combine(Path.GetDirectoryName path, "Gen.fs"))
  let rec methodsOf (t: TypeDefinition) =
    seq {
      yield! t.Methods
      for nested in t.NestedTypes do
        yield! methodsOf nested
    }
  let mutable line = 1
  for t in assembly.MainModule.Types do
    for m in methodsOf t do
      match m.HasBody with
      | false -> ()
      | true ->
        let targets =
          m.Body.Instructions
          |> Seq.collect (fun i ->
            match i.Operand with
            | :? Instruction as target -> [ target ]
            | _ -> [])
          |> Seq.toList
        let points = (m.Body.Instructions[0] :: targets) |> List.distinct
        for instruction in points do
          let point = SequencePoint(instruction, document)
          point.StartLine <- line
          point.StartColumn <- 1
          point.EndLine <- line
          point.EndColumn <- 10
          line <- line + 1
          m.DebugInformation.SequencePoints.Add point
  assembly.Write(path, WriterParameters(WriteSymbols = true, SymbolWriterProvider = PortablePdbWriterProvider()))

/// Instrument the DLL at `path` in place, with the real instrumenter. The answer is how many probes it wrote.
let instrumentInPlace (path: string) : Result<int, string> =
  withSequencePoints path
  match CoverageInstrumenter.instrumentAssemblyInPlace path with
  | Result.Ok map -> Result.Ok map.TotalProbes
  | Result.Error message -> Result.Error message
