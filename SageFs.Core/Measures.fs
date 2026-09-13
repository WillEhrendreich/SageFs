namespace SageFs

/// Units of measure for type-safe timing and sizing.
/// Prevents mixing milliseconds with raw integers at compile time.
module Measures =

  /// Milliseconds — the primary timing unit throughout SageFs.
  [<Measure>] type ms

  /// A cohort claim's fencing token (SageFs.Cohort) — cohort-monotonic, bumped on
  /// every reassignment/orphan/release so a command presenting a stale fence is
  /// structurally distinguishable from one presenting the current one.
  [<Measure>] type fence

  /// A cohort ledger position (SageFs.Cohort) — dense and monotonic; it is also the
  /// CohortFrame's Version, so the same integer names "replay up to here", "the
  /// frame this render came from", and "re-read from v" for an MCP resource.
  ///
  /// NOT named `seq`: the multi-agent vision doc's own name for this measure is
  /// `seq`, matching F#'s `seq<'T>`. A compile check (`dotnet fsi` against a
  /// throwaway module opening a `[<Measure>] type seq`) confirmed that name
  /// shadows both `seq<'T>` type annotations AND the `seq { }` computation
  /// expression builder for every file that opens the module it is defined in —
  /// this module is already `open`ed by files that use `seq { }`
  /// (Features/LiveTestingTypes.fs among them), so that name would have broken
  /// the build repo-wide the moment those files recompiled. `ledgerSeq` carries
  /// the same meaning without the collision.
  [<Measure>] type ledgerSeq

  /// Convert a TimeSpan to float<ms>.
  let inline toMs (ts: System.TimeSpan) : float<ms> =
    LanguagePrimitives.FloatWithMeasure<ms> ts.TotalMilliseconds

  /// Wrap a raw float as float<ms> (for interop boundaries).
  let inline floatMs (v: float) : float<ms> =
    LanguagePrimitives.FloatWithMeasure<ms> v

  /// Strip measure to get raw int (for interop boundaries like Thread.Sleep).
  let inline rawMs (v: int<ms>) : int = int v

  /// Strip measure to get raw float (for interop boundaries).
  let inline rawMsf (v: float<ms>) : float = float v
