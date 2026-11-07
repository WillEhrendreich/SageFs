namespace SageFs.Simulation

open System
open System.IO
open SageFs

/// Deterministic Simulation Testing for the settings layer file: seeded schedules of two browser tabs saving and
/// resetting three settings, a crash in the middle of a write, a corrupt file and a daemon restart, folded through the
/// REAL `SettingsCatalog.edit`, `SettingsStore.update`, `SettingsCatalog.resolve` and `SettingsCatalog.applyAtStart`
/// against a real temporary directory. The schedule is data: an op list that a seed produces, and replaying a seed
/// gives the same trace. The twin is the read-modify-write the store had before it took a lock, run as two halves with
/// two tabs' reads before either write, which the first invariant must catch.
module SettingsStoreSim =

  /// Two Live settings and one that needs a restart, so the restart check has both kinds to look at.
  let settings : SettingDescriptor list =
    [ SettingsCatalog.perTestTimeout; SettingsCatalog.globalTestRunTimeout; SettingsCatalog.mcpPort ]

  /// Values that parse for the setting at the same index in `settings`.
  let private values : string[][] =
    [| [| "5"; "12"; "30"; "90" |]; [| "60"; "120"; "300" |]; [| "40001"; "40002"; "40003" |] |]

  [<RequireQualifiedAccess>]
  type Op =
    | Edit of tab: int * setting: int * raw: string
    | Clear of tab: int * setting: int
    /// A writer died after it wrote its temporary file and before it moved it over the layer file.
    | CrashDuringWrite
    /// Someone saved the file with a typo, and then fixed it.
    | CorruptFile
    /// The daemon starts again: it applies what is saved to the subsystems that hold it.
    | Restart

  type Scenario = { Seed: int; Ops: Op list }

  [<RequireQualifiedAccess>]
  type Behavior =
    | Real
    /// TWIN: the read-modify-write without a lock. Two edits from different tabs next to each other in the schedule
    /// both read the file before either writes it.
    | UnlockedTwin

  [<RequireQualifiedAccess>]
  type FileState =
    | Valid of Map<string, string>
    | Unreadable of reason: string

  /// What a corrupt file did to resolution and to a write, observed while it was corrupt.
  type CorruptObservation =
    { Path: string
      Resolutions: (string * Result<Provenance, ConfigError>) list
      /// A save over the corrupt file left a readable layer.
      Repaired: bool
      /// And kept the corrupt bytes beside it.
      BytesKept: bool }

  type State =
    { Step: int
      Op: Op option
      /// What the file holds if every edit that was accepted was applied in schedule order.
      Expected: Map<string, string>
      File: FileState
      Corrupt: CorruptObservation option
      /// The keys of Live settings whose value in use after a restart differs from what the layers resolve to.
      RestartMismatches: string list option }

  let private garbage = "{ this is not a settings layer ]"

  let private readState (path: string) : FileState =
    match SettingsStore.readLayerChecked path with
    | SettingsStore.LayerRead.Layer layer -> FileState.Valid layer
    | SettingsStore.LayerRead.Unreadable reason -> FileState.Unreadable reason

  let private changeOf (op: Op) : (SettingDescriptor * SettingsStore.LayerChange) option =
    match op with
    | Op.Edit(_, index, raw) ->
      let d = settings.[index]
      match d.Parse raw with
      | Ok v -> Some(d, SettingsStore.SetValue(d.Key, d.Render v))
      | Error _ -> None
    | Op.Clear(_, index) ->
      let d = settings.[index]
      Some(d, SettingsStore.ClearValue d.Key)
    | Op.CrashDuringWrite | Op.CorruptFile | Op.Restart -> None

  let private tabOf (op: Op) : int option =
    match op with
    | Op.Edit(tab, _, _) | Op.Clear(tab, _) -> Some tab
    | Op.CrashDuringWrite | Op.CorruptFile | Op.Restart -> None

  /// What the timeouts held before the schedule ran, put back when it ends and at every restart: a restart is a new
  /// process, which starts from the defaults.
  let private baseline () = Timeouts.perTestDefault (), Timeouts.globalTestRun ()

  let private restore (perTest: TimeSpan, run: TimeSpan) =
    Timeouts.setPerTestTimeout perTest
    Timeouts.setGlobalTestRunTimeout run

  /// Run one scenario against a fresh directory and return every state, oldest first. `Behavior.Real` runs each edit
  /// through the catalog; `UnlockedTwin` runs a pair of neighbouring edits from different tabs as read, read, write,
  /// write.
  let trace (behavior: Behavior) (scenario: Scenario) : State list =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-settings-sim-%d-%s" scenario.Seed (Guid.NewGuid().ToString("N").[..5]))
    Directory.CreateDirectory dir |> ignore
    let held = baseline ()
    let path = SettingsStore.globalPath dir
    let paths : ConfigPaths = { GlobalDir = dir; Repo = NoRepoCheckout }
    let initial =
      { Step = 0; Op = None; Expected = Map.empty; File = FileState.Valid Map.empty; Corrupt = None; RestartMismatches = None }

    let accept (change: SettingsStore.LayerChange) (expected: Map<string, string>) = SettingsStore.applyChange change expected

    let runReal (state: State) (op: Op) : State =
      let expected =
        match op, changeOf op with
        | Op.Edit(_, _, raw), Some(d, change) ->
          match SettingsCatalog.edit paths LGlobal raw d with
          | Ok _ -> accept change state.Expected
          | Error _ -> state.Expected
        | Op.Clear _, Some(d, change) ->
          match SettingsCatalog.clear paths LGlobal d with
          | Ok _ -> accept change state.Expected
          | Error _ -> state.Expected
        | _ -> state.Expected
      { state with Expected = expected }

    /// The twin's pair: both tabs read the file, then both write what they read with their own change applied.
    let runTwinPair (state: State) (first: Op) (second: Op) : State =
      let readA = SettingsStore.readLayer path
      let readB = SettingsStore.readLayer path
      let apply (read: Map<string, string>) (op: Op) =
        match changeOf op with
        | Some(_, change) -> Some (SettingsStore.applyChange change read)
        | None -> None
      let expected =
        [ first; second ]
        |> List.fold (fun acc op -> match changeOf op with Some(_, change) -> accept change acc | None -> acc) state.Expected
      match apply readA first with
      | Some layer -> SettingsStore.commit path layer |> ignore
      | None -> ()
      match apply readB second with
      | Some layer -> SettingsStore.commit path layer |> ignore
      | None -> ()
      { state with Expected = expected }

    let corrupt (state: State) : State =
      let original = if File.Exists path then File.ReadAllText path else ""
      File.WriteAllText(path, garbage)
      let resolutions = settings |> List.map (fun d -> d.Key, SettingsCatalog.resolve paths d)
      // A save over it, as the page would do.
      let saved = SettingsStore.update path (SettingsStore.SetValue("x", "1"))
      let repaired =
        match saved, readState path with
        | Ok (), FileState.Valid _ -> true
        | _ -> false
      let kept =
        let copy = SettingsStore.corruptCopyPath path
        File.Exists copy && File.ReadAllText copy = garbage
      // The person puts back what was there.
      match original <> "" with
      | true -> File.WriteAllText(path, original)
      | false -> File.Delete path
      { state with Corrupt = Some { Path = path; Resolutions = resolutions; Repaired = repaired; BytesKept = kept } }

    let restart (state: State) : State =
      restore held
      SettingsCatalog.applyAtStart ExternalInputs.none paths settings
      let mismatches =
        settings
        |> List.filter (fun d -> d.Applicability = Live)
        |> List.choose (fun d ->
          match SettingsCatalog.resolve paths d with
          | Ok provenance ->
            match SettingsCatalog.inUse d provenance, provenance.Source with
            | InUseSameAsSaved, _ -> None
            | InUseDiffers _, LDefault -> None
            | InUseDiffers _, _ -> Some d.Key
          | Error _ -> Some d.Key)
      { state with RestartMismatches = Some mismatches }

    try
      let rec go (acc: State list) (ops: Op list) : State list =
        match ops with
        | [] -> List.rev acc
        | op :: rest ->
          let previous = List.head acc
          let step = { previous with Step = previous.Step + 1; Op = Some op; Corrupt = None; RestartMismatches = None }
          match behavior, rest with
          | Behavior.UnlockedTwin, next :: tail when (match tabOf op, tabOf next with Some a, Some b -> a <> b | _ -> false) ->
            let done1 = runTwinPair step op next
            let done2 = { done1 with Step = done1.Step + 1; Op = Some next }
            go ({ done2 with File = readState path } :: { done1 with File = readState path } :: acc) tail
          | _ ->
            let after =
              match op with
              | Op.Edit _ | Op.Clear _ ->
                match behavior with
                | Behavior.Real -> runReal step op
                | Behavior.UnlockedTwin ->
                  // A lone edit is the read and the write with nothing between them.
                  match changeOf op with
                  | Some(_, change) ->
                    SettingsStore.commit path (SettingsStore.applyChange change (SettingsStore.readLayer path)) |> ignore
                    { step with Expected = accept change step.Expected }
                  | None -> step
              | Op.CrashDuringWrite ->
                File.WriteAllText(path + ".tmp", garbage)
                step
              | Op.CorruptFile -> corrupt step
              | Op.Restart -> restart step
            go ({ after with File = readState path } :: acc) rest
      go [ initial ] scenario.Ops
    finally
      restore held
      try Directory.Delete(dir, true) with _ -> ()

  /// A pure function of `seed`: replaying a seed gives the identical op list.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let n = 12 + rng.Next 30
    let ops =
      [ for _ in 1 .. n ->
          match rng.Next 12 with
          | 0 | 1 | 2 | 3 | 4 | 5 ->
            let setting = rng.Next settings.Length
            Op.Edit(rng.Next 2, setting, values.[setting].[rng.Next values.[setting].Length])
          | 6 | 7 -> Op.Clear(rng.Next 2, rng.Next settings.Length)
          | 8 -> Op.CrashDuringWrite
          | 9 -> Op.CorruptFile
          | _ -> Op.Restart ]
    { Seed = seed; Ops = ops }
