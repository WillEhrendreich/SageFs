namespace SageFs.Features

open System
open System.IO
open System.Diagnostics
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
  let checker : Lazy<FSharpChecker> =
    lazy (FSharpChecker.Create(projectCacheSize = SageFs.SageFsConfig.ReferencesProjectCacheSize))

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
        let! results = checker.Value.ParseAndCheckProject options
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
    ReferenceGather.run
      budget
      (fun () -> clock.Elapsed)
      (fun project left -> checkProject name byName.[project] left)
      (ordered |> List.map fst)
