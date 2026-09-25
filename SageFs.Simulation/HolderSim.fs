module SageFs.Simulation.HolderSim

/// WHY — the holder's safety property is "a refused migration changes
/// nothing, and a successful one is actually applied". Both are easy to state
/// and easy to get wrong: a refusal that still mutates the cell publishes a
/// value nobody derived, and a success that is dropped leaves the running app
/// serving the old value while the save claims it carried state.
///
/// This sim runs a seeded chain of carry/refuse attempts against one cell and
/// folds the REAL `Holder.swapIfMigrated`. The twins are the two wrong models
/// a real implementation would plausibly have, so the invariants are proven to
/// have teeth rather than passing vacuously. Pure: no IO, no ambient clock.

open SageFs
open SageFs.Holder

/// A fixed reference instant so scenarios read as small offsets.
let baseTimeUtc = System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc)

/// The cell's payload. `Version` is the migration's observable effect, so a
/// dropped or invented migration shows up as a wrong Version.
type Counted = { N: int; Version: int }

/// One step: attempt a migration that either carries or refuses.
type Step =
  | Carry of atOffset: float
  | Refuse of atOffset: float

/// The seeded scenario: a chain of attempts against one cell.
type Scenario =
  { Seed: int
    Steps: Step list }

/// What the sim observed. The trace is the replay evidence.
type Observation =
  { At: System.DateTime
    Refused: bool
    /// The value served AFTER this attempt, rendered so a wrong version is
    /// visible in a failure message.
    Served: string }

type Trace =
  { Scenario: Scenario
    Observations: Observation list }

let private hold0 = hold { N = 0; Version = 0 }

/// The migration the sim uses: bump both fields, so a carry is observable.
let private bump (old: Counted) : Migration<Counted, Counted> =
  Migration.Carried(old, { N = old.N + 1; Version = old.Version + 1 })

/// A migration with nothing to carry, which must fail closed.
let private refuse (_: Counted) : Migration<Counted, Counted> =
  Migration.Refused "nothing to carry"

let private serve (cell: Cell<Counted>) = sprintf "%d@v%d" (read cell).N (read cell).Version

/// The real behaviour: the production swap, unchanged.
let real (cell: Cell<Counted>) (migrate: Counted -> Migration<Counted, Counted>) : Swap<Counted, Counted> =
  swapIfMigrated cell migrate

/// TWIN — a refusal that still MUTATES the cell the app is already holding.
/// This is the bug the safety property exists to prevent: the verdict is
/// ignored and the live cell is overwritten, so the running app sees a value
/// no migration produced. It writes through the caller's cell rather than
/// returning a new one, because a real implementation that got this wrong
/// would corrupt the cell the app is reading.
let alwaysSwap (cell: Cell<Counted>) (migrate: Counted -> Migration<Counted, Counted>) : Swap<Counted, Counted> =
  let before = read cell

  match migrate before with
  | Migration.Carried(carried, into) -> Swap.Swapped(carried, into, hold into)
  // The bug: a refusal still WRITES THROUGH the live cell.
  | Migration.Refused _ ->
    let invented = { N = before.N + 1; Version = before.Version + 1 }
    cell.Value.Value <- invented
    Swap.Held("refused but the live cell was overwritten", cell)

/// TWIN — a success that is silently dropped, so a save can claim it carried
/// state while the running app keeps serving the old value. The migration
/// really produced a new value; this model just never adopts it.
let neverSwap (cell: Cell<Counted>) (migrate: Counted -> Migration<Counted, Counted>) : Swap<Counted, Counted> =
  let before = read cell

  match migrate before with
  | Migration.Carried(_, _) -> Swap.Held("dropped a carry", cell)
  | Migration.Refused why -> Swap.Held(why, cell)

/// The model under test, so the sim and the twins share one driver.
type Model = { Name: string; Swap: Cell<Counted> -> (Counted -> Migration<Counted, Counted>) -> Swap<Counted, Counted> }

let models =
  [ { Name = "real"; Swap = real }
    { Name = "always-swap"; Swap = alwaysSwap }
    { Name = "never-swap"; Swap = neverSwap } ]

let modelNamed (name: string) = models |> List.find (fun m -> m.Name = name)

/// Run a scenario under a model, deterministically.
///
/// Each step's cell is a FRESH cell holding the previous step's value, so a
/// model that mutates the cell it was given corrupts only that step. Sharing
/// one mutable cell across steps would let one step's damage masquerade as
/// the next step's verdict — which is how a sim ends up failing on a model
/// that is correct.
let runWith (model: Model) (scenario: Scenario) : Trace =
  let mutable current = hold0
  let observations = ResizeArray<Observation>()

  for step in scenario.Steps do
    let atOffset, refuseNow =
      match step with
      | Carry offset -> offset, false
      | Refuse offset -> offset, true

    let migrate =
      if refuseNow then
        fun c -> refuse c
      else
        bump

    // A fresh cell for this step, holding what the app currently serves.
    let cell = hold (read current)
    let swap = model.Swap cell migrate
    let served = cellOf swap |> read

    // Adopt the served value for the next step, whatever the model did.
    current <- hold served
    observations.Add { At = baseTimeUtc.AddSeconds atOffset; Refused = refuseNow; Served = sprintf "%d@v%d" served.N served.Version }

  { Scenario = scenario; Observations = List.ofSeq observations }

/// Run against the production swap.
let run (scenario: Scenario) : Trace = runWith (modelNamed "real") scenario

/// Run against a named twin.
let runModel (name: string) (scenario: Scenario) : Trace = runWith (modelNamed name) scenario

/// The seeded scenarios. Long enough that a refusal is followed by a carry,
/// which is the case a naive implementation gets wrong.
let scenarioOf (seed: int) : Scenario =
  let length = 3 + (seed % 6)

  { Seed = seed
    Steps =
      [ for i in 0 .. length - 1 do
          if (i + seed) % 3 = 0 then
            Refuse(float i * 2.0)
          else
            Carry(float i * 2.0) ] }
