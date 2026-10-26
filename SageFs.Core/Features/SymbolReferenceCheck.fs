namespace SageFs.Features

open System
open System.IO
open System.Diagnostics
open SageFs
open SageFs.Utils
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Symbols

/// The compiler half of the references rung: check each project of the session the way the compiler sees it (its source
/// files in compile order, its references) and read every use that resolves to the NAME the caller gave. The name is
/// compared with the compiler's own full name for the symbol, so a use through an alias, an `open` or a pipeline counts
/// and a same-spelled name in another module does not. The pure half, which decides what scope a reply may claim, is
/// `ReferenceReply.ofAnswer`.
module SymbolReferenceCheck =

  /// A checker of its own, with a project cache sized for a whole session. A check that runs out of budget is cancelled and
  /// keeps the files it finished, so asking again goes further, but only while the project stays cached: with a cache of
  /// three (the caller check's), a session of eight projects evicted each project before the next ask came back to it,
  /// and the same question started from nothing every time (measured on the SageFs.Tests session, 2026-10-25).
  let newChecker () : FSharpChecker =
    FSharpChecker.Create(projectCacheSize = SageFs.SageFsConfig.ReferencesProjectCacheSize)

  /// Gives back what a checker cached: its project caches, the shared reference-assembly readers, and then a full
  /// collection, so the memory returns to the worker and not just to the checker's free list.
  let dropChecker (checker: FSharpChecker) : unit =
    checker.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients()
    GC.Collect(2, GCCollectionMode.Aggressive, true, true)

  /// The worker's resident set, which is what the machine sees and what a project count cannot bound.
  let residentBytes () : int64 =
    use self = Process.GetCurrentProcess()
    self.WorkingSet64

  /// The ceiling and the retention the cache is held to. The project cache size says how many projects are held and
  /// nothing about their bytes, so bytes and idleness decide separately whether the cache is kept
  /// (`ReferenceCacheRetention.decide`).
  let cachePolicy : CachePolicy =
    { CeilingBytes = SageFs.SageFsConfig.ReferencesResidentCeilingBytes
      Retention = Timeouts.referencesCacheRetention }

  /// The held checker and the timer that sweeps it while nobody asks. The timer is a field of this record, and the record
  /// is held by the lazy below for the life of the worker, so the timer is never collected and silently stopped.
  type HeldCache =
    { Holder: CacheHolder<FSharpChecker>
      SweepTimer: Threading.Timer }

  /// Built on the first ask, so a worker that never answers `list_references` pays for no timer.
  let cache : Lazy<HeldCache> =
    lazy
      (let clock = Stopwatch.StartNew()
       let holder =
         CacheHolder<FSharpChecker>(
           { Create = newChecker
             Drop = dropChecker
             ResidentBytes = residentBytes
             Pressure = fun () -> ReferenceCacheRetention.pressureOf (MachineMemory.current ())
             Now = fun () -> clock.Elapsed
             Policy = cachePolicy
             Released = fun reason -> Log.info "list_references: compiler cache dropped, %s" (ReleaseReason.describe reason) })
       let sweepEvery = Timeouts.referencesCacheSweep
       // A sweep runs as an async that reports its failure: an exception thrown on a timer thread ends the worker.
       let sweep =
         async {
           let! outcome = Async.Catch(async { holder.Sweep() })
           match outcome with
           | Choice1Of2() -> ()
           | Choice2Of2 failure -> Log.warn "list_references: the cache sweep failed: %s" failure.Message
         }
       { Holder = holder
         SweepTimer = new Threading.Timer((fun _ -> Async.Start sweep), null, sweepEvery, sweepEvery) })

  /// The full name the compiler gives a symbol, or empty when it has none. An entity with no qualified name (a type
  /// abbreviation) answers through `TryFullName`; everything else answers through `FullName`.
  let fullNameOf (symbol: FSharpSymbol) : string =
    match symbol with
    | :? FSharpEntity as entity -> entity.TryFullName |> Option.defaultValue ""
    | other -> other.FullName

  let projectNameOf (options: FSharpProjectOptions) : string = Path.GetFileNameWithoutExtension options.ProjectFileName

  /// Every definition and use of `name` in a checked project, and the files the compiler reported errors in. A file that
  /// did not check cleanly may hold uses the compiler could not place, so the reply names it.
  let sitesOf (cancel: Threading.CancellationToken) (name: string) (results: FSharpCheckProjectResults) : ReferenceSite list * string list =
    let sites =
      results.GetAllUsesOfAllSymbols cancel
      |> Array.filter (fun symbolUse ->
        cancel.ThrowIfCancellationRequested()
        String.Equals(fullNameOf symbolUse.Symbol, name, StringComparison.Ordinal))
      |> Array.map (fun symbolUse ->
        { File = symbolUse.FileName
          Line = symbolUse.Range.StartLine
          Column = symbolUse.Range.StartColumn
          Kind = (if symbolUse.IsFromDefinition then ReferenceKind.Definition else ReferenceKind.Use) })
      |> Array.toList
    let unclean =
      results.Diagnostics
      |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
      |> Array.map (fun d -> d.FileName)
      |> Array.distinct
      |> Array.toList
    sites, unclean

  /// Checks one project and reads the uses of `name`, giving up when `bound` passes. The scan of the uses runs inside the
  /// bound with the check, so a slow scan is cut off like a slow check.
  let checkProject (name: string) (options: FSharpProjectOptions) (bound: TimeSpan) : Async<ProjectCheck> =
    let work =
      async {
        let! cancel = Async.CancellationToken
        // Asked before every project: a ceiling crossed or a machine squeezed partway through a question drops the cache
        // before the next project, and the time the rebuild takes is spent from the same budget.
        let checker = cache.Value.Holder.Acquire()
        let! results = checker.ParseAndCheckProject options
        let sites, unclean = sitesOf cancel name results
        return ProjectCheck.Answered(sites, unclean)
      }
    async {
      try
        return! ReloadPlanning.withinBound bound work
      with
      | :? TimeoutException -> return ProjectCheck.CutOff
      | ex -> return ProjectCheck.Raised ex.Message
    }

  /// A project is checked without the projects it references: their built assemblies are in its references, and a use of
  /// their symbols resolves against those. Every project of the session is tried, so a use in any of them is found.
  let withoutReferencedProjects (options: FSharpProjectOptions) : FSharpProjectOptions = { options with ReferencedProjects = [||] }

  /// The order the projects are tried in: the smallest first, so one budget reaches the most projects and the biggest,
  /// which the budget is most likely to run out on, is the one left for a second ask. Measured on the SageFs.Tests
  /// session, where the test project came first and used the whole budget before any other project was reached.
  let inBudgetOrder (projects: FSharpProjectOptions list) : FSharpProjectOptions list =
    projects |> List.sortBy (fun o -> o.SourceFiles.Length)

  /// The worker's answer for `name`: every project of the session checked under one budget, as `ReferenceGather` runs it.
  let answerAsync (budget: TimeSpan) (projects: FSharpProjectOptions list) (name: string) : Async<ReferencesAnswer> =
    let ordered = projects |> inBudgetOrder |> List.map (fun o -> projectNameOf o, withoutReferencedProjects o)
    let byName = Map.ofList ordered
    let clock = Stopwatch.StartNew()
    async {
      try
        return!
          ReferenceGather.run
            budget
            (fun () -> clock.Elapsed)
            (fun project left -> checkProject name byName.[project] left)
            (ordered |> List.map fst)
      finally
        // Whatever the ask ended in, the cache is no longer in use, so a sweep may drop it and the ceiling is asked now.
        cache.Value.Holder.Settle()
    }
