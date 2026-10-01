namespace SageFs.Features.LiveTesting

open System.Reflection
open SageFs.Utils

/// The runtime side of coverage: reading and resetting the `__SageFsCoverage` tracker the instrumenter injected. Reflection
/// only (no Cecil), so it compiles into the FSI host, where the instrumented code actually runs.
module CoverageProbes =

  /// The probe array of every instrumented assembly among these, found by looking for the tracker type. Looking
  /// costs far more than a test often does (a type lookup per loaded assembly), so a caller that reads after
  /// every test finds the trackers once and reuses them for as long as the loaded assemblies are the same.
  let findTrackers (assemblies: Assembly array) : FieldInfo array =
    assemblies
    |> Array.choose (fun asm ->
      try
        let trackerType = asm.GetType("__SageFsCoverage")
        match isNull trackerType with
        | true -> None
        | false ->
          match trackerType.GetField("Hits") with
          | null -> None
          | hitsField -> Some hitsField
      with ex ->
        Log.warn "[CoverageInstrumenter] findTrackers failed for %s: %s" (asm.GetName().Name) ex.Message
        None)

  /// Concatenate what the trackers recorded, in order, and clear them in place for the next reading.
  /// None when there are no trackers: nothing here records coverage.
  let readAndClear (trackers: FieldInfo array) : bool array option =
    match trackers.Length with
    | 0 -> None
    | _ ->
      let arrays = trackers |> Array.map (fun field -> field.GetValue(null) :?> bool array)
      let hits = Array.concat arrays
      for probes in arrays do
        System.Array.Clear probes
      Some hits

  /// Discover __SageFsCoverage tracker in all loaded assemblies and collect hits.
  /// Concatenates hits from all instrumented assemblies in order.
  let discoverAndCollectHits (assemblies: Assembly array) : bool array option =
    let allHits =
      assemblies
      |> Array.choose (fun asm ->
        try
          let trackerType = asm.GetType("__SageFsCoverage")
          match trackerType <> null with
          | true ->
            let hitsField = trackerType.GetField("Hits")
            match hitsField <> null with
            | true ->
              Some(hitsField.GetValue(null) :?> bool array)
            | false -> None
          | false -> None
        with ex ->
          Log.warn "[CoverageInstrumenter] discoverAndCollectHits failed for %s: %s" (asm.GetName().Name) ex.Message
          None)
    match allHits.Length = 0 with
    | true -> None
    | false -> Some(Array.concat allHits)

  /// Reset __SageFsCoverage tracker in all loaded assemblies.
  let discoverAndResetHits (assemblies: Assembly array) : unit =
    for asm in assemblies do
      try
        let trackerType = asm.GetType("__SageFsCoverage")
        match trackerType <> null with
        | true ->
          let hitsField = trackerType.GetField("Hits")
          match hitsField <> null with
          | true ->
            let arr = hitsField.GetValue(null) :?> bool array
            match arr <> null with
            | true ->
              hitsField.SetValue(null, Array.create arr.Length false)
            | false -> ()
          | false -> ()
        | false -> ()
      with ex ->
        Log.warn "[CoverageInstrumenter] discoverAndResetHits failed for %s: %s" (asm.GetName().Name) ex.Message
        ()
