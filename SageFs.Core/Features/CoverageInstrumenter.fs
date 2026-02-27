namespace SageFs.Features.LiveTesting

open System
open System.IO
open System.Reflection
open Mono.Cecil
open Mono.Cecil.Cil
open Mono.Cecil.Rocks

/// Cecil-based IL instrumentation for branch coverage.
/// Injects a __SageFsCoverage tracker class with bool[] Hits field,
/// and inserts Hit(slotId) calls at every non-hidden sequence point.
module CoverageInstrumenter =

  /// Collect all non-hidden sequence points across all methods,
  /// including methods in nested types (F# closures, lambdas).
  let collectSequencePoints (moduleDef: ModuleDefinition) =
    let mutable slotId = 0
    let rec methodsOf (t: TypeDefinition) =
      seq {
        yield! t.Methods
        for nt in t.NestedTypes do
          yield! methodsOf nt
      }
    [|
      for t in moduleDef.Types do
        for m in methodsOf t do
          if m.HasBody
             && m.DebugInformation <> null
             && m.DebugInformation.HasSequencePoints then
            for sp in m.DebugInformation.SequencePoints do
              if not sp.IsHidden then
                yield (m, sp, slotId)
                slotId <- slotId + 1
    |]

  /// Inject the __SageFsCoverage static class into the module.
  let injectTracker (moduleDef: ModuleDefinition) (totalSlots: int) =
    let objectType = moduleDef.ImportReference(typeof<obj>)
    let voidType = moduleDef.ImportReference(typeof<Void>)
    let int32Type = moduleDef.ImportReference(typeof<int32>)
    let boolType = moduleDef.ImportReference(typeof<bool>)

    let tracker =
      TypeDefinition(
        "", "__SageFsCoverage",
        TypeAttributes.Class
        ||| TypeAttributes.Public
        ||| TypeAttributes.Sealed
        ||| TypeAttributes.Abstract
        ||| TypeAttributes.BeforeFieldInit,
        objectType)

    let hitsField =
      FieldDefinition(
        "Hits",
        FieldAttributes.Public ||| FieldAttributes.Static,
        ArrayType(boolType))
    tracker.Fields.Add(hitsField)

    // .cctor: Hits = new bool[totalSlots]
    let cctor =
      MethodDefinition(
        ".cctor",
        MethodAttributes.Static
        ||| MethodAttributes.Private
        ||| MethodAttributes.SpecialName
        ||| MethodAttributes.RTSpecialName
        ||| MethodAttributes.HideBySig,
        voidType)
    let il = cctor.Body.GetILProcessor()
    il.Append(il.Create(OpCodes.Ldc_I4, totalSlots))
    il.Append(il.Create(OpCodes.Newarr, boolType))
    il.Append(il.Create(OpCodes.Stsfld, hitsField))
    il.Append(il.Create(OpCodes.Ret))
    tracker.Methods.Add(cctor)

    // Hit(int id): Hits[id] = true
    let hitMethod =
      MethodDefinition(
        "Hit",
        MethodAttributes.Public
        ||| MethodAttributes.Static
        ||| MethodAttributes.HideBySig,
        voidType)
    hitMethod.Parameters.Add(
      ParameterDefinition("id", ParameterAttributes.None, int32Type))
    let hitIl = hitMethod.Body.GetILProcessor()
    hitIl.Append(hitIl.Create(OpCodes.Ldsfld, hitsField))
    hitIl.Append(hitIl.Create(OpCodes.Ldarg_0))
    hitIl.Append(hitIl.Create(OpCodes.Ldc_I4_1))
    hitIl.Append(hitIl.Create(OpCodes.Stelem_I1))
    hitIl.Append(hitIl.Create(OpCodes.Ret))
    tracker.Methods.Add(hitMethod)

    moduleDef.Types.Add(tracker)
    (tracker, hitMethod, hitsField)

  /// Insert instructions before target, updating all exception handler
  /// boundaries and branch operands that reference the target instruction.
  /// Cecil's InsertBefore does NOT update these references — we must do it
  /// ourselves, exactly as AltCover and Coverlet both do.
  let private insertBeforeWithFixup
    (il: ILProcessor) (target: Instruction) (newInstrs: Instruction list) =
    let firstInserted =
      newInstrs
      |> List.rev
      |> List.fold (fun next instr -> il.InsertBefore(next, instr); instr) target

    for handler in il.Body.ExceptionHandlers do
      if handler.TryStart = target then handler.TryStart <- firstInserted
      if handler.TryEnd = target then handler.TryEnd <- firstInserted
      if handler.HandlerStart = target then handler.HandlerStart <- firstInserted
      if handler.HandlerEnd = target then handler.HandlerEnd <- firstInserted
      if handler.FilterStart = target then handler.FilterStart <- firstInserted

    for instr in il.Body.Instructions do
      match instr.OpCode.OperandType with
      | OperandType.InlineBrTarget
      | OperandType.ShortInlineBrTarget ->
        if Object.ReferenceEquals(instr.Operand, target) then
          instr.Operand <- firstInserted
      | OperandType.InlineSwitch ->
        let targets = instr.Operand :?> Instruction array
        for i in 0 .. targets.Length - 1 do
          if Object.ReferenceEquals(targets.[i], target) then
            targets.[i] <- firstInserted
      | _ -> ()

    firstInserted

  /// Insert Hit(slotId) calls before each sequence point instruction.
  /// Uses SimplifyMacros/OptimizeMacros to prevent short-branch overflow
  /// when probe instructions push branch targets beyond ±127 byte range.
  let insertProbes
    (hitMethod: MethodDefinition)
    (points: (MethodDefinition * Cil.SequencePoint * int) array) =
    let byMethod = points |> Array.groupBy (fun (m, _, _) -> m)
    for (m, methodPoints) in byMethod do
      m.Body.SimplifyMacros()
      let il = m.Body.GetILProcessor()
      // Insert from end to start to preserve IL offsets for lookup
      let sorted =
        methodPoints
        |> Array.sortByDescending (fun (_, sp, _) -> sp.Offset)
      for (_, sp, slotId) in sorted do
        let target =
          m.Body.Instructions
          |> Seq.tryFind (fun i -> i.Offset = sp.Offset)
        match target with
        | Some instr ->
          let loadId = il.Create(OpCodes.Ldc_I4, slotId)
          let callHit =
            il.Create(OpCodes.Call, hitMethod :> MethodReference)
          insertBeforeWithFixup il instr [ loadId; callHit ] |> ignore
        | None -> ()
      m.Body.OptimizeMacros()

  /// Instrument an assembly: inject tracker + probes at sequence points.
  /// Returns (InstrumentationMap, instrumentedAssemblyPath) or error.
  let instrumentAssembly (assemblyPath: string)
    : Result<InstrumentationMap * string, string> =
    try
      let pdbPath = Path.ChangeExtension(assemblyPath, ".pdb")
      let hasPdb = File.Exists(pdbPath)
      let readerParams =
        ReaderParameters(
          ReadSymbols = hasPdb,
          ReadingMode = ReadingMode.Deferred)
      use asm = AssemblyDefinition.ReadAssembly(assemblyPath, readerParams)
      let moduleDef = asm.MainModule
      let points = collectSequencePoints moduleDef
      if points.Length = 0 then
        Ok(InstrumentationMap.empty, assemblyPath)
      else
        let slots =
          points
          |> Array.map (fun (_, sp, slotId) ->
            { File =
                if sp.Document <> null then sp.Document.Url
                else ""
              Line = sp.StartLine
              Column = sp.StartColumn
              EndLine = sp.EndLine
              EndColumn = sp.EndColumn
              BranchId = slotId }
            : SageFs.Features.LiveTesting.SequencePoint)
        let map =
          { Slots = slots
            TotalProbes = points.Length
            TrackerTypeName = "__SageFsCoverage"
            HitsFieldName = "Hits" }
        let (_, hitMethod, _) =
          injectTracker moduleDef points.Length
        insertProbes hitMethod points
        let dir = Path.GetDirectoryName(assemblyPath)
        let name = Path.GetFileNameWithoutExtension(assemblyPath)
        let ext = Path.GetExtension(assemblyPath)
        let instrPath =
          Path.Combine(dir, sprintf "%s.instrumented%s" name ext)
        let writerParams = WriterParameters(WriteSymbols = hasPdb)
        asm.Write(instrPath, writerParams)
        Ok(map, instrPath)
    with ex ->
      Error(sprintf "Instrumentation failed: %s" ex.Message)

  /// Instrument an assembly in-place by writing to temp then replacing.
  /// Returns InstrumentationMap or error. The original path is overwritten.
  let instrumentAssemblyInPlace (assemblyPath: string)
    : Result<InstrumentationMap, string> =
    match instrumentAssembly assemblyPath with
    | Error e -> Error e
    | Ok(map, instrPath) ->
      if instrPath = assemblyPath then
        Ok map
      else
        try
          let pdbPath = Path.ChangeExtension(assemblyPath, ".pdb")
          let instrPdb = Path.ChangeExtension(instrPath, ".pdb")
          File.Delete(assemblyPath)
          File.Move(instrPath, assemblyPath)
          if File.Exists(instrPdb) && File.Exists(pdbPath) then
            File.Delete(pdbPath)
            File.Move(instrPdb, pdbPath)
          Ok map
        with ex ->
          Error(sprintf "Failed to replace assembly: %s" ex.Message)

  /// Collect coverage hits from an instrumented assembly via reflection.
  let collectCoverageHits
    (asm: Assembly)
    (map: InstrumentationMap)
    : CoverageState option =
    if map.TotalProbes = 0 then
      None
    else
      let trackerType = asm.GetType(map.TrackerTypeName)
      if trackerType = null then
        None
      else
        let hitsField = trackerType.GetField(map.HitsFieldName)
        if hitsField = null then
          None
        else
          let hits = hitsField.GetValue(null) :?> bool array
          Some(InstrumentationMap.toCoverageState hits map)

  /// Reset coverage hits to prepare for a new test run.
  let resetCoverageHits
    (asm: Assembly)
    (map: InstrumentationMap)
    : unit =
    if map.TotalProbes > 0 then
      let trackerType = asm.GetType(map.TrackerTypeName)
      if trackerType <> null then
        let hitsField = trackerType.GetField(map.HitsFieldName)
        if hitsField <> null then
          hitsField.SetValue(null, Array.create map.TotalProbes false)

  /// Discover __SageFsCoverage tracker in all loaded assemblies and collect hits.
  /// Concatenates hits from all instrumented assemblies in order.
  let discoverAndCollectHits (assemblies: Assembly array) : bool array option =
    let allHits =
      assemblies
      |> Array.choose (fun asm ->
        try
          let trackerType = asm.GetType("__SageFsCoverage")
          if trackerType <> null then
            let hitsField = trackerType.GetField("Hits")
            if hitsField <> null then
              Some(hitsField.GetValue(null) :?> bool array)
            else None
          else None
        with _ -> None)
    if allHits.Length = 0 then None
    else Some(Array.concat allHits)

  /// Reset __SageFsCoverage tracker in all loaded assemblies.
  let discoverAndResetHits (assemblies: Assembly array) : unit =
    for asm in assemblies do
      try
        let trackerType = asm.GetType("__SageFsCoverage")
        if trackerType <> null then
          let hitsField = trackerType.GetField("Hits")
          if hitsField <> null then
            let arr = hitsField.GetValue(null) :?> bool array
            if arr <> null then
              hitsField.SetValue(null, Array.create arr.Length false)
      with _ -> ()

  /// Track consecutive instrumentation failures for circuit breaker.
  let mutable private consecutiveFailures = 0
  let private maxConsecutiveFailures = 3

  /// Reset the circuit breaker (e.g., after a successful instrumentation).
  let resetCircuitBreaker () = consecutiveFailures <- 0

  /// Check if the circuit breaker has tripped.
  let isCircuitBroken () = consecutiveFailures >= maxConsecutiveFailures

  /// Instrument all project DLLs in a shadow-copied solution for IL coverage.
  /// Returns the instrumentation maps for all successfully instrumented assemblies.
  /// Circuit breaker: after 3 consecutive failures, disables instrumentation.
  let instrumentShadowSolution (projectTargetPaths: string list)
    : InstrumentationMap array =
    if isCircuitBroken () then
      eprintfn "⚠️ IL coverage instrumentation disabled after %d consecutive failures" consecutiveFailures
      [||]
    else
      let mutable hadFailure = false
      let results =
        projectTargetPaths
        |> List.toArray
        |> Array.choose (fun targetPath ->
          if File.Exists(targetPath) then
            match instrumentAssemblyInPlace targetPath with
            | Ok map when map.TotalProbes > 0 ->
              Some map
            | Ok _ -> None
            | Error msg ->
              hadFailure <- true
              eprintfn "⚠️ IL instrumentation failed for %s: %s" (Path.GetFileName targetPath) msg
              None
          else None)
      if hadFailure then
        consecutiveFailures <- consecutiveFailures + 1
        if isCircuitBroken () then
          eprintfn "🛑 IL coverage circuit breaker tripped after %d failures — instrumentation disabled for this session" consecutiveFailures
      else
        consecutiveFailures <- 0
      results
