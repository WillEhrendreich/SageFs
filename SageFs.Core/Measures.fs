namespace SageFs

/// Units of measure for type-safe timing and sizing.
/// Prevents mixing milliseconds with raw integers at compile time.
module Measures =

  /// Milliseconds — the primary timing unit throughout SageFs.
  [<Measure>] type ms

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
