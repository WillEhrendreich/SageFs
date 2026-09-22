namespace SageFs.Simulation

open System
open System.Reflection.Emit
open SageFs.Middleware.ValueReads

/// Deterministic simulation of rule 2's one promise: a redefined module value
/// is reported Patched ONLY when nothing in the running app can still hold a
/// copy of the old value.
///
/// Real hosts show one interleaving per 20-second run. The races that matter
/// here are between threads: a request reading the value while the startup
/// window closes, a lazy forced between the evidence check and the patch, a
/// probe being removed while its reader runs again. This folds the REAL code
/// through every one of those orders, a few thousand times a second:
///
///   * the REAL classifier (`ValueReads.fateAt`) over IL synthesized for each
///     read shape, so "dead local" and "stored, then captured" are told apart
///     by the code that tells them apart in production;
///   * the REAL ledger (`Ledger.step`/`Ledger.evidence`), fed the same events
///     the runtime tracker files (`ValueReadTracking`): a probe firing, the
///     startup window's getter watch seeing a caller, the window closing;
///   * the REAL save decision (`verdictOf` + `checkSave`), asked twice around
///     the patch exactly like WorkerMain asks it.
///
/// Ground truth is kept separately and never reads any of that: which reads
/// happened, of which version of the value, and whether their shape can hold
/// a copy (a table of shapes, not the classifier). Chaos is data: a seed picks
/// the readers, their shapes, and the order everything lands in. Same seed,
/// same trace.
///
/// Scope, stated because it's the one place this is weaker than "any code at
/// all": a read through reflection (no read of the value in the reader's own
/// IL) is only seen while the startup window's getter watch is on. The model
/// makes those reads anyway and files the copies they leave as out of scope,
/// counted but kept out of the invariant. docs/hot-reload.md says the same.
module ValueReadSim =

  /// How a reader uses the value it read. Each one becomes real IL below.
  [<RequireQualifiedAccess>]
  type Shape =
    /// `call get_v; pop`
    | Popped
    /// `call get_v; stloc.0` and local 0 is never read: how F#'s startup code
    /// computes a literal value it doesn't use.
    | DeadLocal
    /// `call get_v; stloc.0; ldloc.0; newobj Closure`: the fixture's `banner`.
    | LocalIntoClosure
    /// `call get_v; stsfld Sink`
    | StoredStatic
    /// `call get_v; call Consume`
    | Argument
    /// `call get_v; ret`: `let greet () = greeting`, or `lazy greeting`.
    | Returned
    /// `call get_v; ldstr "!"; call Concat; ret`: `lazy (greeting + "!")`.
    | ConcatThenReturn
    /// `call get_v; ldc.i4.1; brtrue; pop`: thrown away in fact, but past a
    /// branch the classifier doesn't follow. Here to show the classifier errs
    /// toward "escaped" (a restart), never toward "thrown away".
    | BranchFirst

  let allShapes =
    [ Shape.Popped; Shape.DeadLocal; Shape.LocalIntoClosure; Shape.StoredStatic
      Shape.Argument; Shape.Returned; Shape.ConcatThenReturn; Shape.BranchFirst ]

  /// GROUND TRUTH, independent of the classifier: can a read of this shape
  /// leave a copy anywhere that outlives the read? Returned counts: whoever
  /// called the reader may keep it (a lazy does).
  let canHold (shape: Shape) : bool =
    match shape with
    | Shape.Popped
    | Shape.DeadLocal
    | Shape.BranchFirst -> false
    | Shape.LocalIntoClosure
    | Shape.StoredStatic
    | Shape.Argument
    | Shape.Returned
    | Shape.ConcatThenReturn -> true

  // ── synthesized IL, classified by the real fateAt ──────────────────────────

  let private getterToken = 1
  let private closureCtorToken = 2
  let private consumeToken = 3
  let private concatToken = 4
  let private sinkToken = 5

  /// Token resolution for the synthesized bodies.
  let tokens : Tokens =
    { Method =
        fun token ->
          match token with
          | 1 -> Ok { Name = "App.State.get_v"; DeclaringType = "App.State"; Parameters = 0; Receiver = Receiver.Static; Returns = Returns.AValue }
          | 2 -> Ok { Name = "App.State+handlers@78-9..ctor"; DeclaringType = "App.State+handlers@78-9"; Parameters = 1; Receiver = Receiver.Instance; Returns = Returns.Nothing }
          | 3 -> Ok { Name = "App.Log.Consume"; DeclaringType = "App.Log"; Parameters = 1; Receiver = Receiver.Static; Returns = Returns.Nothing }
          | 4 -> Ok { Name = "System.String.Concat"; DeclaringType = "System.String"; Parameters = 2; Receiver = Receiver.Static; Returns = Returns.AValue }
          | other -> Error(sprintf "no token %d" other)
      Member =
        fun token ->
          match token with
          | 5 -> "App.State.Sink"
          | other -> sprintf "token %d" other }

  let private sizeOf (op: OpCode, operand: Operand) =
    op.Size
    + (match operand with
       | Operand.Token _ -> 4
       | Operand.Target _ when op = OpCodes.Brtrue_S -> 1
       | Operand.Literal when op = OpCodes.Ldstr -> 4
       | _ -> 0)

  let private assemble (ops: (OpCode * Operand) list) : Instr[] =
    ops
    |> List.mapFold (fun offset (op, operand) -> { Offset = offset; Op = op; Operand = operand }, offset + sizeOf (op, operand)) 0
    |> fst
    |> List.toArray

  /// The body each shape compiles to. The read is always instruction 0.
  let ilOf (shape: Shape) : Instr[] =
    let read = OpCodes.Call, Operand.Token getterToken
    match shape with
    | Shape.Popped -> assemble [ read; OpCodes.Pop, Operand.NoOperand; OpCodes.Ret, Operand.NoOperand ]
    | Shape.DeadLocal -> assemble [ read; OpCodes.Stloc_0, Operand.Variable 0; OpCodes.Ret, Operand.NoOperand ]
    | Shape.LocalIntoClosure ->
      assemble
        [ read
          OpCodes.Stloc_0, Operand.Variable 0
          OpCodes.Ldloc_0, Operand.Variable 0
          OpCodes.Newobj, Operand.Token closureCtorToken
          OpCodes.Pop, Operand.NoOperand
          OpCodes.Ret, Operand.NoOperand ]
    | Shape.StoredStatic -> assemble [ read; OpCodes.Stsfld, Operand.Token sinkToken; OpCodes.Ret, Operand.NoOperand ]
    | Shape.Argument -> assemble [ read; OpCodes.Call, Operand.Token consumeToken; OpCodes.Ret, Operand.NoOperand ]
    | Shape.Returned -> assemble [ read; OpCodes.Ret, Operand.NoOperand ]
    | Shape.ConcatThenReturn ->
      assemble [ read; OpCodes.Ldstr, Operand.Literal; OpCodes.Call, Operand.Token concatToken; OpCodes.Ret, Operand.NoOperand ]
    | Shape.BranchFirst ->
      assemble
        [ read
          OpCodes.Ldc_I4_1, Operand.NoOperand
          OpCodes.Brtrue_S, Operand.Target 8
          OpCodes.Pop, Operand.NoOperand
          OpCodes.Ret, Operand.NoOperand ]

  /// A classifier: the real one, or a twin.
  type Classify = Instr[] -> ReadFate

  let realClassify : Classify = fun instrs -> fateAt tokens instrs 0

  // ── the app being simulated ────────────────────────────────────────────────

  [<RequireQualifiedAccess>]
  type Kind =
    /// A module's static initializer: runs once, while the app starts.
    | StartupCode
    /// A request handler: runs any number of times once the app is up.
    | PerRequest
    /// A `lazy` thunk: runs once, whenever something first forces it.
    | LazyThunk
    /// Reads through reflection: no read of the value in its own IL.
    | Reflection

  /// Whether SageFs can put a one-shot probe on the reader.
  [<RequireQualifiedAccess>]
  type Watch =
    | Probed
    | CannotProbe of why: string

  type SimReader =
    { Id: string
      Kind: Kind
      Value: string
      Shape: Shape
      Watch: Watch }

  [<RequireQualifiedAccess>]
  type Intent =
    | RunReader of reader: string
    | CloseWindow
    | Save of value: string

  type Scenario =
    { Seed: int
      Values: string list
      Readers: SimReader list
      Intents: Intent list }

  /// The value whose only escaping reader is the banner-shaped startup code.
  [<Literal>]
  let Banner = "App.State.banner"

  // ── micro-steps: one thread's worth of progress, interleaved by the seed ──

  [<RequireQualifiedAccess>]
  type Step =
    /// The reader's method is entered: a probe still in place fires.
    | Enter of reader: string
    /// The reader calls the getter. The window's watch, if it's still on the
    /// getter, sees the call go in.
    | EnterGetter of reader: string
    /// The getter body runs and the reader gets the value (whatever version
    /// the getter returns right now). `watched` is whether the watch saw the
    /// call go in.
    | ReadGetter of reader: string * watched: bool
    /// A watch that reports AFTER the read (the twin's postfix) lands here, in
    /// its own step, so anything can happen in between.
    | DeliverGetterRecord of value: string * caller: Caller
    | RemoveProbe of reader: string
    | EndWindowInLedger
    | RemoveWatch of value: string
    | SaveQuery of save: int * value: string
    | SaveApply of save: int * value: string
    | SaveRecheck of save: int * value: string

  [<RequireQualifiedAccess>]
  type SaveResult =
    /// Evidence said no before anything was patched.
    | RefusedBefore of verdict: ValueVerdict
    /// Patched, and the recheck found nothing that raced it.
    | Patched
    /// Patched, but a read raced the patch: restart needed.
    | RefusedAfterPatch of verdict: ValueVerdict

  type SaveObservation =
    { Save: int
      Value: string
      Result: SaveResult
      /// Ground truth at the moment of the verdict: readers holding a copy of
      /// a version older than the one the getter now returns.
      StaleHolders: string list
      /// Readers holding a stale copy that no mechanism could have seen: a
      /// reflection read whose getter call landed after the window's watch was
      /// gone. Outside what SageFs claims, so kept apart and counted, never
      /// silently folded into the invariant.
      OutOfScopeHolders: string list
      /// Did the banner-shaped startup reader run before this save's check?
      BannerCaptured: bool }

  type Trace =
    { Scenario: Scenario
      Saves: SaveObservation list
      /// Getter-watch reports filed after the ledger's window had closed: a
      /// read that went in while the watch was being taken off.
      LateWatchReports: int
      /// Every step, in the order it ran. A replay of the seed gives the same list.
      Steps: Step list }

  /// When the startup window's getter watch files the caller.
  [<RequireQualifiedAccess>]
  type WatchTiming =
    /// A Harmony prefix: filed as the call goes in, before any value exists.
    /// What ValueReadTracking does.
    | BeforeTheRead
    /// A postfix: filed after the getter returned, so a save can slip in
    /// between the read and the record.
    | AfterTheRead

  /// How the save pipeline is wired, so twins can swap one piece.
  type Wiring =
    { Classify: Classify
      LedgerStep: Ledger -> LedgerEvent -> Ledger
      /// Ask the evidence again after the patch (WorkerMain does).
      Recheck: bool
      Watch: WatchTiming }

  let real : Wiring = { Classify = realClassify; LedgerStep = Ledger.step; Recheck = true; Watch = WatchTiming.BeforeTheRead }

  [<RequireQualifiedAccess>]
  type private ProbeState =
    | Armed
    | Removed
    | Never

  /// Whether a held copy is one SageFs claims to see.
  [<RequireQualifiedAccess>]
  type Scope =
    | InScope
    | OutOfScope

  type private World =
    { Ledger: Ledger
      WindowOpen: bool
      Watches: Set<string>
      Probes: Map<string, ProbeState>
      Version: Map<string, int>
      /// (reader, value, version) copies, only for shapes that can hold.
      Held: (string * string * int * Scope) list
      RanOnce: Set<string>
      Saves: SaveObservation list
      LateWatchReports: int
      Log: Step list }

  let private readerNamed (s: Scenario) (id: string) = s.Readers |> List.find (fun r -> r.Id = id)

  let private ledgerReader (wiring: Wiring) (r: SimReader) : Reader =
    { Id = r.Id; Name = r.Id; Reads = [ r.Value, 0, wiring.Classify (ilOf r.Shape) ] }

  let private verdicts (w: World) (value: string) =
    [ value, Ledger.evidence w.Ledger value |> verdictOf ]

  /// Run one scenario through a wiring. Deterministic in (wiring, scenario).
  let runWith (wiring: Wiring) (scenario: Scenario) : Trace =
    let rng = Random(scenario.Seed * 7919 + 17)
    let initialLedger =
      let tracked = scenario.Values |> List.map LedgerEvent.ValueTracked
      let found =
        scenario.Readers
        |> List.filter (fun r -> r.Kind <> Kind.Reflection)
        |> List.map (fun r ->
          let status =
            match r.Watch with
            | Watch.Probed -> ReaderStatus.Armed
            | Watch.CannotProbe why -> ReaderStatus.Unwatchable why
          LedgerEvent.ReaderFound(ledgerReader wiring r, status))
      tracked @ found |> List.fold wiring.LedgerStep Ledger.empty
    let mutable world =
      { Ledger = initialLedger
        WindowOpen = true
        Watches = Set.ofList scenario.Values
        Probes =
          scenario.Readers
          |> List.map (fun r ->
            r.Id,
            match r.Kind, r.Watch with
            | Kind.Reflection, _ -> ProbeState.Never
            | _, Watch.Probed -> ProbeState.Armed
            | _, Watch.CannotProbe _ -> ProbeState.Never)
          |> Map.ofList
        Version = scenario.Values |> List.map (fun v -> v, 0) |> Map.ofList
        Held = []
        RanOnce = Set.empty
        Saves = []
        LateWatchReports = 0
        Log = [] }
    let pending = Collections.Generic.List<Step>()
    let file (event: LedgerEvent) =
      let late =
        match event, world.Ledger.Window with
        | LedgerEvent.GetterRead _, StartupWindow.Closed -> 1
        | _ -> 0
      world <- { world with Ledger = wiring.LedgerStep world.Ledger event; LateWatchReports = world.LateWatchReports + late }
    let log (step: Step) = world <- { world with Log = step :: world.Log }
    let closeWindowNow () =
      // The evidence request closes the window itself, before it answers.
      match world.WindowOpen with
      | false -> ()
      | true ->
        file LedgerEvent.StartupEnded
        world <- { world with WindowOpen = false; Watches = Set.empty }
    let staleHolders (scope: Scope) (value: string) =
      let now = Map.find value world.Version
      world.Held
      |> List.filter (fun (_, v, version, s) -> v = value && version < now && s = scope)
      |> List.map (fun (reader, _, _, _) -> reader)
      |> List.distinct
    let bannerRan () =
      scenario.Readers
      |> List.exists (fun r -> r.Value = Banner && r.Shape = Shape.LocalIntoClosure && world.RanOnce.Contains r.Id)
    let decide (value: string) = checkSave (verdicts world value)
    let record (save: int) (value: string) (result: SaveResult) (bannerCaptured: bool) =
      world <-
        { world with
            Saves =
              world.Saves
              @ [ { Save = save
                    Value = value
                    Result = result
                    StaleHolders = staleHolders Scope.InScope value
                    OutOfScopeHolders = staleHolders Scope.OutOfScope value
                    BannerCaptured = bannerCaptured } ] }
    let callerOf (r: SimReader) =
      match r.Kind with
      | Kind.Reflection -> Caller.Unknown r.Id
      | _ -> Caller.Known r.Id
    let perform (step: Step) =
      log step
      match step with
      | Step.Enter id ->
        match Map.find id world.Probes with
        | ProbeState.Armed ->
          file (LedgerEvent.ReaderRan id)
          pending.Add(Step.RemoveProbe id)
        | ProbeState.Removed
        | ProbeState.Never -> ()
        pending.Add(Step.EnterGetter id)
      | Step.EnterGetter id ->
        let r = readerNamed scenario id
        let watched = world.Watches.Contains r.Value
        match watched, wiring.Watch with
        | true, WatchTiming.BeforeTheRead -> file (LedgerEvent.GetterRead(r.Value, callerOf r))
        | true, WatchTiming.AfterTheRead
        | false, _ -> ()
        pending.Add(Step.ReadGetter(id, watched))
      | Step.ReadGetter(id, watched) ->
        let r = readerNamed scenario id
        let version = Map.find r.Value world.Version
        let holds, scope =
          match r.Kind, watched with
          | Kind.Reflection, true -> true, Scope.InScope
          | Kind.Reflection, false -> true, Scope.OutOfScope
          | _, _ -> canHold r.Shape, Scope.InScope
        world <-
          { world with
              RanOnce = world.RanOnce.Add id
              Held =
                match holds with
                | true -> world.Held @ [ id, r.Value, version, scope ]
                | false -> world.Held }
        match watched, wiring.Watch with
        | true, WatchTiming.AfterTheRead -> pending.Add(Step.DeliverGetterRecord(r.Value, callerOf r))
        | true, WatchTiming.BeforeTheRead
        | false, _ -> ()
      | Step.DeliverGetterRecord(value, caller) -> file (LedgerEvent.GetterRead(value, caller))
      | Step.RemoveProbe id -> world <- { world with Probes = Map.add id ProbeState.Removed world.Probes }
      | Step.EndWindowInLedger ->
        match world.WindowOpen with
        | true ->
          file LedgerEvent.StartupEnded
          world <- { world with WindowOpen = false }
          for v in scenario.Values do
            pending.Add(Step.RemoveWatch v)
        | false -> ()
      | Step.RemoveWatch value -> world <- { world with Watches = world.Watches.Remove value }
      | Step.SaveQuery(save, value) ->
        closeWindowNow ()
        let banner = bannerRan ()
        match decide value with
        | SaveCheck.Refused((_, verdict), _) -> record save value (SaveResult.RefusedBefore verdict) banner
        | SaveCheck.AllSafe -> pending.Add(Step.SaveApply(save, value))
      | Step.SaveApply(save, value) ->
        world <- { world with Version = Map.add value (Map.find value world.Version + 1) world.Version }
        match wiring.Recheck with
        | true -> pending.Add(Step.SaveRecheck(save, value))
        | false -> record save value SaveResult.Patched (bannerRan ())
      | Step.SaveRecheck(save, value) ->
        match decide value with
        | SaveCheck.Refused((_, verdict), _) -> record save value (SaveResult.RefusedAfterPatch verdict) (bannerRan ())
        | SaveCheck.AllSafe -> record save value SaveResult.Patched (bannerRan ())
    let expand (intent: Intent) (saveNo: int) =
      match intent with
      | Intent.RunReader id ->
        let r = readerNamed scenario id
        match r.Kind, world.RanOnce.Contains id with
        // Startup code and a lazy thunk run once, ever.
        | Kind.StartupCode, true
        | Kind.LazyThunk, true -> ()
        | _ -> pending.Add(Step.Enter id)
      | Intent.CloseWindow -> pending.Add Step.EndWindowInLedger
      | Intent.Save value -> pending.Add(Step.SaveQuery(saveNo, value))
    let mutable intents = scenario.Intents
    let mutable saves = 0
    while not (List.isEmpty intents && pending.Count = 0) do
      match intents, pending.Count > 0 && (List.isEmpty intents || rng.Next 3 > 0) with
      | _, true ->
        let index = rng.Next pending.Count
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
      LateWatchReports = world.LateWatchReports
      Steps = List.rev world.Log }

  let run (scenario: Scenario) : Trace = runWith real scenario

  // ── scenarios from a seed ──────────────────────────────────────────────────

  let private pick (rng: Random) (xs: 'a list) = xs.[rng.Next xs.Length]

  /// A seeded app: the banner (startup code capturing it in a closure, plus a
  /// dead-local read of it), and a few other values with readers of every
  /// kind and shape. Intents start the app, then interleave requests, lazy
  /// forces, saves (some while it's still starting) and the window closing.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let others = [ for i in 0 .. rng.Next(1, 4) -> sprintf "App.State.v%d" i ]
    let values = Banner :: others
    let watch () =
      match rng.Next 5 with
      | 0 -> Watch.CannotProbe "a static initializer"
      | _ -> Watch.Probed
    let bannerReaders =
      [ { Id = "startup.banner.closure"; Kind = Kind.StartupCode; Value = Banner; Shape = Shape.LocalIntoClosure; Watch = watch () }
        { Id = "startup.banner.dead"; Kind = Kind.StartupCode; Value = Banner; Shape = Shape.DeadLocal; Watch = watch () } ]
    let otherReaders =
      others
      |> List.collect (fun v ->
        [ for i in 0 .. rng.Next(1, 4) ->
            let kind = pick rng [ Kind.StartupCode; Kind.PerRequest; Kind.PerRequest; Kind.LazyThunk; Kind.LazyThunk; Kind.Reflection ]
            { Id = sprintf "%s.%A.%d" v kind i
              Kind = kind
              Value = v
              Shape = pick rng allShapes
              Watch =
                match kind with
                | Kind.StartupCode -> watch ()
                | _ -> Watch.Probed } ])
    let readers = bannerReaders @ otherReaders
    let startup =
      readers
      |> List.filter (fun r -> r.Kind = Kind.StartupCode || r.Kind = Kind.Reflection)
      |> List.map (fun r -> Intent.RunReader r.Id)
    let later =
      [ for _ in 1 .. rng.Next(8, 24) ->
          match rng.Next 10 with
          | 0 | 1 -> Intent.Save(pick rng values)
          | _ -> Intent.RunReader (pick rng readers).Id ]
    // The window closes somewhere in the run: before startup is done, at the
    // boundary, or late. Some saves land before it (a save during startup).
    let all = startup @ later
    let closeAt = rng.Next(0, List.length all + 1)
    let intents = List.take closeAt all @ [ Intent.CloseWindow ] @ List.skip closeAt all
    // Maybe a save right at the start, while the app is still starting.
    let intents =
      match rng.Next 4 with
      | 0 -> Intent.Save(pick rng values) :: intents
      | _ -> intents
    { Seed = seed
      Values = values
      Readers = readers
      Intents = intents }
