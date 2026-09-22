namespace SageFs.Simulation

open System
open SageFs.Middleware.ValueReads
open SageFs.Simulation.ValueReadSim

/// Deterministic simulation of rule 2's reflection reads, in every
/// `ReflectionReadMode`, including switching modes while the app runs.
///
/// A reflective read has no read of the value in anyone's IL, so the probes
/// never see it. The runtime (ValueReadTracking.fs) catches it at the
/// reflection entry points, and in probe-callers names the caller from a
/// [<ThreadStatic>] slot that a rewired caller sets for exactly one call. That
/// slot is the dangerous part: the first reflection entry a call reaches has
/// to use it up, tracked value or not, or a reflective read INSIDE the target
/// (the target's own code reading the value) gets pinned on the outer caller.
///
/// What's real and what's modelled, as in ValueReadSim:
///   * REAL: the classifier (`fateAt`, over IL for each call-site shape), the
///     ledger (`Ledger.step`/`Ledger.evidence`, including `ReflectiveRead`),
///     and the save decision (`verdictOf` + `checkSave`, asked around the
///     patch the way WorkerMain asks).
///   * MODELLED: the runtime's watch: slots per thread, which callers are
///     rewired, what a stack walk finds (the true caller), the startup
///     window's getter watch.
///
/// Ground truth is kept apart and never reads any of that: who really read,
/// on which thread, what version, and whether the call site's shape can hold
/// a copy (`canHold`, a table, not the classifier).
///
/// Reads hop threads (an async continuation runs its next read wherever), new
/// callers turn up mid-run, modes switch mid-run, and saves land anywhere.
module ReflectionReadSim =

  /// How a reflective reader reaches the value.
  [<RequireQualifiedAccess>]
  type Via =
    /// It calls `GetValue` on the value's property itself.
    | Direct
    /// Its read happens inside another caller's reflective call: `outer`
    /// reflects over an untracked member whose code then reads the value.
    /// With `outer` rewired, `outer`'s slot is set when the read happens.
    | InsideCallOf of outer: string

  type Reader =
    { Id: string
      Value: string
      /// What the reader does with what its reflection call returned.
      Shape: Shape
      Via: Via }

  [<RequireQualifiedAccess>]
  type Intent =
    | Read of reader: string
    | SwitchMode of mode: ReflectionReadMode
    | CloseWindow
    | Save of value: string

  type Scenario =
    { Seed: int
      Values: string list
      Readers: Reader list
      StartMode: ReflectionReadMode
      Intents: Intent list }

  [<RequireQualifiedAccess>]
  type Step =
    /// A rewired caller's call site arms the slot on `thread`: it's about
    /// to reach `target` (a value, or the untracked member it reflects over).
    | SetSlot of thread: int * caller: string * target: string
    /// The outer call reaches the reflection entry for an untracked member.
    | UntrackedEntry of thread: int
    /// The reflection entry for the tracked value, on behalf of `reader`.
    | Entry of thread: int * reader: string
    /// The getter runs and the reader gets the value.
    | Read of thread: int * reader: string
    /// The call site's `finally`.
    | ClearSlot of thread: int
    | Switch of mode: ReflectionReadMode
    | EndWindow
    | SaveQuery of save: int * value: string
    | SaveApply of save: int * value: string
    | SaveRecheck of save: int * value: string

  [<RequireQualifiedAccess>]
  type SaveResult =
    | RefusedBefore of verdict: ValueVerdict
    | Patched
    | RefusedAfterPatch of verdict: ValueVerdict

  type SaveObservation =
    { Save: int
      Value: string
      Mode: ReflectionReadMode
      Result: SaveResult
      /// Ground truth: readers holding a copy older than what the getter returns now.
      StaleHolders: string list }

  /// Every `AtSite` the ledger was told, against who really read.
  type Attribution =
    { TrueReader: string
      NamedReader: string }

  type Trace =
    { Scenario: Scenario
      Saves: SaveObservation list
      Attributions: Attribution list
      SlotHits: int
      Walks: int
      NestedReads: int
      ModeSwitches: int
      Steps: Step list }

  /// How the slot is trusted, so a twin can put the naive version back.
  [<RequireQualifiedAccess>]
  type SlotUse =
    /// The first reflection entry a call reaches uses the slot up, tracked
    /// value or not, and it only names a read of the exact member it was
    /// armed for. What ReflectionHooks.Dispatch does.
    | ConsumedAtFirstEntry
    /// The slot lives as long as the caller's call (set on the way in, cleared
    /// in its `finally`), an entry for an untracked member leaves it alone,
    /// and whatever read finds it trusts it. The prefix/finalizer-on-the-
    /// caller design.
    | HeldForTheWholeCall

  type Wiring =
    { Slot: SlotUse }

  let real : Wiring = { Slot = SlotUse.ConsumedAtFirstEntry }

  type private World =
    { Ledger: Ledger
      Window: StartupWindow
      Mode: ReflectionReadMode
      Slots: Map<int, string * string>
      Rewired: Set<string>
      Marked: Set<string>
      Version: Map<string, int>
      Held: (string * string * int) list
      Saves: SaveObservation list
      Attributions: Attribution list
      SlotHits: int
      Walks: int
      NestedReads: int
      ModeSwitches: int
      Log: Step list }

  let private fateOf (shape: Shape) = realClassify (ilOf shape)

  /// Run one scenario. Deterministic in (wiring, scenario).
  let runWith (wiring: Wiring) (scenario: Scenario) : Trace =
    let rng = Random(scenario.Seed * 104729 + 7)
    let readerNamed id = scenario.Readers |> List.find (fun r -> r.Id = id)
    let mutable world =
      { Ledger = scenario.Values |> List.map LedgerEvent.ValueTracked |> List.fold Ledger.step Ledger.empty
        Window = StartupWindow.Open
        Mode = scenario.StartMode
        Slots = Map.empty
        Rewired = Set.empty
        Marked = Set.empty
        Version = scenario.Values |> List.map (fun v -> v, 0) |> Map.ofList
        Held = []
        Saves = []
        Attributions = []
        SlotHits = 0
        Walks = 0
        NestedReads = 0
        ModeSwitches = 0
        Log = [] }
    let pending = Collections.Generic.List<Step>()
    let file (event: LedgerEvent) = world <- { world with Ledger = Ledger.step world.Ledger event }
    let slotOn thread = Map.tryFind thread world.Slots
    let setSlot thread caller target = world <- { world with Slots = Map.add thread (caller, target) world.Slots }
    let clearSlot thread = world <- { world with Slots = Map.remove thread world.Slots }
    /// The getter carries its own watch: during startup in every mode, and
    /// after it in exact-every-read.
    let getterWatched () =
      match world.Window, world.Mode with
      | StartupWindow.Open, _ -> true
      | StartupWindow.Closed, ReflectionReadMode.ExactEveryRead -> true
      | StartupWindow.Closed, ReflectionReadMode.MarkOnReflect
      | StartupWindow.Closed, ReflectionReadMode.ProbeCallers -> false
    let attribute (trueReader: Reader) (named: Reader) =
      world <-
        { world with
            Attributions = world.Attributions @ [ { TrueReader = trueReader.Id; NamedReader = named.Id } ] }
      file (LedgerEvent.ReflectiveRead(trueReader.Value, ReflectiveCaller.AtSite(named.Id, 0, fateOf named.Shape)))
    let decide (value: string) = checkSave [ value, Ledger.evidence world.Ledger value |> verdictOf ]
    let staleHolders (value: string) =
      let now = Map.find value world.Version
      world.Held
      |> List.filter (fun (_, v, version) -> v = value && version < now)
      |> List.map (fun (reader, _, _) -> reader)
      |> List.distinct
    let record save value result =
      world <-
        { world with
            Saves = world.Saves @ [ { Save = save; Value = value; Mode = world.Mode; Result = result; StaleHolders = staleHolders value } ] }
    let perform (step: Step) =
      world <- { world with Log = step :: world.Log }
      match step with
      | Step.SetSlot(thread, caller, target) -> setSlot thread caller target
      | Step.UntrackedEntry thread ->
        match wiring.Slot with
        | SlotUse.ConsumedAtFirstEntry -> clearSlot thread
        | SlotUse.HeldForTheWholeCall -> ()
      | Step.Entry(thread, id) ->
        let r = readerNamed id
        let slot =
          match slotOn thread, wiring.Slot with
          | Some(caller, target), SlotUse.ConsumedAtFirstEntry when target = r.Value -> Some caller
          | Some _, SlotUse.ConsumedAtFirstEntry -> None
          | Some(caller, _), SlotUse.HeldForTheWholeCall -> Some caller
          | None, _ -> None
        // Every entry uses the slot up, whatever it's looking at.
        clearSlot thread
        match getterWatched () with
        | true ->
          match world.Window with
          // The startup window's watch files an unknown caller: a copy.
          | StartupWindow.Open -> file (LedgerEvent.GetterRead(r.Value, Caller.Unknown r.Id))
          // Exact-every-read: the getter's watch walks to the true caller.
          | StartupWindow.Closed ->
            world <- { world with Walks = world.Walks + 1 }
            attribute r r
        | false ->
          match world.Mode with
          | ReflectionReadMode.MarkOnReflect ->
            match world.Marked.Contains r.Value with
            | true -> ()
            | false ->
              world <- { world with Marked = world.Marked.Add r.Value }
              file (LedgerEvent.ReflectiveRead(r.Value, ReflectiveCaller.Unattributed "mark-on-reflect"))
          | ReflectionReadMode.ExactEveryRead ->
            world <- { world with Walks = world.Walks + 1 }
            attribute r r
          | ReflectionReadMode.ProbeCallers ->
            match slot with
            | Some caller ->
              world <- { world with SlotHits = world.SlotHits + 1 }
              attribute r (readerNamed caller)
            | None ->
              // Walk: the true caller, and it gets rewired.
              world <- { world with Walks = world.Walks + 1; Rewired = world.Rewired.Add r.Id }
              attribute r r
        pending.Add(Step.Read(thread, id))
      | Step.Read(_, id) ->
        let r = readerNamed id
        let version = Map.find r.Value world.Version
        world <-
          { world with
              Held =
                match canHold r.Shape with
                | true -> world.Held @ [ r.Id, r.Value, version ]
                | false -> world.Held }
      | Step.ClearSlot thread -> clearSlot thread
      | Step.Switch mode -> world <- { world with Mode = mode; ModeSwitches = world.ModeSwitches + 1 }
      | Step.EndWindow ->
        match world.Window with
        | StartupWindow.Open ->
          file LedgerEvent.StartupEnded
          world <- { world with Window = StartupWindow.Closed }
        | StartupWindow.Closed -> ()
      | Step.SaveQuery(save, value) ->
        // Asking for evidence closes the window, as Tracker.Evidence does.
        match world.Window with
        | StartupWindow.Open ->
          file LedgerEvent.StartupEnded
          world <- { world with Window = StartupWindow.Closed }
        | StartupWindow.Closed -> ()
        match decide value with
        | SaveCheck.Refused((_, verdict), _) -> record save value (SaveResult.RefusedBefore verdict)
        | SaveCheck.AllSafe -> pending.Add(Step.SaveApply(save, value))
      | Step.SaveApply(save, value) ->
        world <- { world with Version = Map.add value (Map.find value world.Version + 1) world.Version }
        pending.Add(Step.SaveRecheck(save, value))
      | Step.SaveRecheck(save, value) ->
        match decide value with
        | SaveCheck.Refused((_, verdict), _) -> record save value (SaveResult.RefusedAfterPatch verdict)
        | SaveCheck.AllSafe -> record save value SaveResult.Patched
    /// One reflective read. Its steps run in order on one thread, and a thread
    /// runs one call at a time (a call is synchronous): two reads on the same
    /// thread never interleave. Everything on other threads does. Chains with
    /// no thread (a mode switch, the window closing, a save) are their own.
    let chains = Collections.Generic.List<int * Step list>()
    let noThread = -1
    let expand (intent: Intent) (saveNo: int) =
      match intent with
      | Intent.Read id ->
        let r = readerNamed id
        // Where this read runs: an async continuation hops threads.
        let thread = rng.Next 3
        let chain =
          match r.Via with
          | Via.Direct ->
            match world.Rewired.Contains r.Id with
            | true -> [ Step.SetSlot(thread, r.Id, r.Value); Step.Entry(thread, r.Id); Step.ClearSlot thread ]
            | false -> [ Step.Entry(thread, r.Id) ]
          | Via.InsideCallOf outer ->
            world <- { world with NestedReads = world.NestedReads + 1 }
            match world.Rewired.Contains outer with
            | true -> [ Step.SetSlot(thread, outer, "relay"); Step.UntrackedEntry thread; Step.Entry(thread, r.Id); Step.ClearSlot thread ]
            | false -> [ Step.UntrackedEntry thread; Step.Entry(thread, r.Id) ]
        chains.Add(thread, chain)
      | Intent.SwitchMode mode -> chains.Add(noThread, [ Step.Switch mode ])
      | Intent.CloseWindow -> chains.Add(noThread, [ Step.EndWindow ])
      | Intent.Save value -> chains.Add(noThread, [ Step.SaveQuery(saveNo, value) ])
    let mutable intents = scenario.Intents
    let mutable saves = 0
    let busy () = chains.Count > 0 || pending.Count > 0
    while not (List.isEmpty intents) || busy () do
      match intents, busy () && (List.isEmpty intents || rng.Next 3 > 0) with
      | _, true ->
        // Pick any runnable thing: the next step of the oldest chain on some
        // thread, or a pending step.
        let runnable =
          [ for i in 0 .. chains.Count - 1 do
              let thread = fst chains.[i]
              let earlier = [ 0 .. i - 1 ] |> List.exists (fun j -> thread <> noThread && fst chains.[j] = thread)
              match earlier with
              | true -> ()
              | false -> yield i ]
        let total = runnable.Length + pending.Count
        let pick = rng.Next total
        match pick < runnable.Length with
        | true ->
          let at = runnable.[pick]
          let thread, steps = chains.[at]
          match steps with
          | [] -> chains.RemoveAt at
          | step :: rest ->
            match rest with
            | [] -> chains.RemoveAt at
            | _ -> chains.[at] <- (thread, rest)
            perform step
        | false ->
          let index = pick - runnable.Length
          let step = pending.[index]
          pending.RemoveAt index
          perform step
      | intent :: rest, false ->
        intents <- rest
        match intent with
        | Intent.Save _ -> saves <- saves + 1
        | _ -> ()
        expand intent saves
      | [], false -> ()
    { Scenario = scenario
      Saves = world.Saves
      Attributions = world.Attributions
      SlotHits = world.SlotHits
      Walks = world.Walks
      NestedReads = world.NestedReads
      ModeSwitches = world.ModeSwitches
      Steps = List.rev world.Log }

  let run (scenario: Scenario) : Trace = runWith real scenario

  let private pick (rng: Random) (xs: 'a list) = xs.[rng.Next xs.Length]

  /// A seeded app: a few values, direct reflective readers of each (every
  /// shape), readers whose reads happen inside another reader's reflective
  /// call, and intents that read, switch modes, close the window and save.
  /// Readers that first read late are the "new callers mid-run".
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let values = [ for i in 0 .. rng.Next(1, 3) -> sprintf "App.State.r%d" i ]
    let direct =
      [ for i in 0 .. rng.Next(2, 6) ->
          { Id = sprintf "direct%d" i; Value = pick rng values; Shape = pick rng allShapes; Via = Via.Direct } ]
    let nested =
      [ for i in 0 .. rng.Next(0, 3) ->
          let outer = pick rng direct
          { Id = sprintf "inside%d" i; Value = pick rng values; Shape = pick rng allShapes; Via = Via.InsideCallOf outer.Id } ]
    let readers = direct @ nested
    let mode () = pick rng ReflectionReadMode.all
    let body =
      [ for _ in 1 .. rng.Next(10, 40) ->
          match rng.Next 12 with
          | 0 | 1 -> Intent.Save(pick rng values)
          | 2 -> Intent.SwitchMode(mode ())
          | _ -> Intent.Read (pick rng readers).Id ]
    let closeAt = rng.Next(0, List.length body + 1)
    { Seed = seed
      Values = values
      Readers = readers
      StartMode = mode ()
      Intents = List.take closeAt body @ [ Intent.CloseWindow ] @ List.skip closeAt body }

/// Invariants over `ReflectionReadSim`, and the twin that proves the slot one bites.
module ReflectionReadInvariants =

  open ReflectionReadSim

  type Violation =
    { Seed: int
      Why: string }

  /// Patched means nothing holds the old value, in every mode.
  let neverPatchedOverAStaleCopy (t: Trace) : Violation list =
    t.Saves
    |> List.choose (fun o ->
      match o.Result, o.StaleHolders with
      | SaveResult.Patched, (_ :: _ as holders) ->
        Some
          { Seed = t.Scenario.Seed
            Why = sprintf "save %d of %s (%A) said Patched while %s held the old value" o.Save o.Value o.Mode (String.concat ", " holders) }
      | _ -> None)

  /// The ledger is only ever told a read was at a caller that really made it.
  let slotNeverNamesTheWrongCaller (t: Trace) : Violation list =
    t.Attributions
    |> List.filter (fun a -> a.TrueReader <> a.NamedReader)
    |> List.map (fun a ->
      { Seed = t.Scenario.Seed
        Why = sprintf "%s's read was filed as %s's" a.TrueReader a.NamedReader })

  let all (t: Trace) = neverPatchedOverAStaleCopy t @ slotNeverNamesTheWrongCaller t

  /// The slot held for the caller's whole call, left alone by an entry for an
  /// untracked member: a read inside the target inherits the outer caller.
  let staleSlotTwin : Wiring = { Slot = SlotUse.HeldForTheWholeCall }
