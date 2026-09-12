/// Pure per-character typing cadence (demo-gif-plan.md §4.3, §5). Seeded
/// log-normal delays (median 55 ms, longer after `.`/`,`/newline, a 120 ms
/// "think" before an identifier), deterministic by seed.
module SageFs.Demos.Cadence

open SageFs.Demos.Domain

/// §9: "typing median 55 ms/char".
[<Literal>]
let private MedianDelayMs = 55.0

/// §4.3: "longer after `.`/`,`/newline" — a multiplier on the median rather
/// than a fixed value, so it still varies with the seeded log-normal.
[<Literal>]
let private AfterPauseMultiplier = 2.5

/// §4.3: "a 120 ms 'think' before an identifier".
[<Literal>]
let private IdentifierThinkMs = 120.0

/// Log-normal shape parameter (sigma). Chosen to keep the spread visible
/// without producing wild outliers at this median.
[<Literal>]
let private Sigma = 0.35

let private isIdentifierChar (c: char) : bool =
  System.Char.IsLetterOrDigit c || c = '_'

let private endsAPause (c: char) : bool =
  c = '.' || c = ',' || c = '\n'

let private charToKey (c: char) : Key =
  match c with
  | '\n' -> Key.Return
  | '\t' -> Key.Tab
  | c -> Key.Char c

/// One log-normal sample around `medianMs`, via a Box-Muller transform of
/// two uniform draws from `rng`. Every character consumes exactly two draws
/// regardless of `medianMs`, so the underlying random sequence — and hence
/// the *ordering* between two runs with different medians but the same
/// seed and length — is stable.
let private logNormalSample (rng: System.Random) (medianMs: float) : float =
  let u1 = rng.NextDouble() |> max 1e-12
  let u2 = rng.NextDouble()
  let z = sqrt (-2.0 * log u1) * cos (2.0 * System.Math.PI * u2)
  medianMs * exp (Sigma * z)

let private baseMedianAt (chars: char[]) (i: int) : float =
  let precededByPause = i > 0 && endsAPause chars.[i - 1]
  let precededByNonIdentifier = i = 0 || not (isIdentifierChar chars.[i - 1])
  if precededByPause then MedianDelayMs * AfterPauseMultiplier
  elif isIdentifierChar chars.[i] && precededByNonIdentifier then IdentifierThinkMs
  else MedianDelayMs

/// The `(Key, Delay)` sequence for typing `text`, seeded so the cadence is
/// identical every run and different per scenario.
let keys (text: Text) (seed: Seed) : (Key * Delay) list =
  let chars = (Text.value text).ToCharArray()
  let (Seed seedValue) = seed
  let rng = System.Random(int (uint32 seedValue))

  chars
  |> Array.mapi (fun i c ->
    let medianMs = baseMedianAt chars i
    let delayMs = logNormalSample rng medianMs |> max 1.0 |> round |> int
    charToKey c, Delay delayMs)
  |> Array.toList
