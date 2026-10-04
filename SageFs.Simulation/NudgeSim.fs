namespace SageFs.Simulation

open System
open System.Text
open SageFs.Features.Tweak
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.NudgeIo
open SageFs.Features.Tweak.Nudge
open SageFs.Simulation.NudgeDisk
open SageFs.Simulation.NudgeWorld

/// Deterministic simulation of the nudge door: a seeded schedule of nudges,
/// undos, redos, inspects and outside edits, each of which may meet a crash or a
/// failed disk step, folded through the REAL `Nudge.runWith` against the
/// in-memory disk. Nothing is reimplemented: the sim only schedules, injects and
/// observes. The same seed gives the same trace, so a failing seed replays.
///
/// Two families. `DoorOnly` has no outside edits, so after the schedule the
/// whole history can be undone and the original bytes must come back exactly.
/// `WithOutsideEdits` interleaves someone editing the same expressions and
/// reformatting the file, where what must hold is that nothing is overwritten
/// and nothing is torn.
module NudgeSim =

  [<RequireQualifiedAccess>]
  type Target =
    | Gravity
    | MaxHealth
    | JumpVelocityRight
    | Label

  [<RequireQualifiedAccess>]
  type Seen =
    /// The hash of what is there when the call is made.
    | Fresh
    /// The hash of what the first inspect would have shown, which goes stale
    /// once anything moves the expression.
    | Stale

  [<RequireQualifiedAccess>]
  type Op =
    | Nudge of target: Target * value: int * seen: Seen
    /// A set against a file the session does not own.
    | NudgeUnowned
    | Undo
    | Redo
    | Look
    /// Someone edits the expression directly, outside the door.
    | OutsideEdit of target: Target * value: int
    /// Someone reformats the file around the expressions.
    | OutsideReformat

  type Step =
    { Op: Op
      Faults: (int * Fault) list }

  [<RequireQualifiedAccess>]
  type Family =
    | DoorOnly
    | WithOutsideEdits

  type Scenario =
    { Seed: int
      Family: Family
      Steps: Step list }

  /// Whether the call is made with the hash the caller really saw (production),
  /// or with the hash rewritten to whatever is there (a door that skips the check).
  [<RequireQualifiedAccess>]
  type SeenPolicy =
    | AsGiven
    | SkipCheckTwin

  [<RequireQualifiedAccess>]
  type StepResult =
    | Crashed of FileOperation
    | Returned of Result<Ran, NudgeRefusal>

  type Observation =
    { Index: int
      Step: Step
      FileBefore: byte[]
      FileAfter: byte[]
      OtherBefore: byte[]
      OtherAfter: byte[]
      /// Every path on the disk after the step.
      PathsAfter: string list
      JournalBefore: byte[]
      JournalAfter: byte[]
      /// For a nudge: the hash the call carried, and the hash of what was there.
      SeenUsed: string
      ActualHash: string
      Result: StepResult }

  /// How the end-of-run recovery went: look once, then undo until nothing is left.
  [<RequireQualifiedAccess>]
  type Recovery =
    | Restored
    | Stuck of NudgeRefusal
    | Crashed of FileOperation

  type Trace =
    { Initial: byte[]
      Observations: Observation list
      Recovery: Recovery
      AfterRecovery: byte[] }

  let otherSource = "module Other\nlet y = 1\n"

  let addressOf (target: Target) : TweakAddress =
    match target with
    | Target.Gravity -> gravity
    | Target.MaxHealth -> maxHealth
    | Target.JumpVelocityRight -> jumpVelocityRight
    | Target.Label -> label

  let literalFor (target: Target) (n: int) : string =
    match target with
    | Target.Gravity -> sprintf "%d.5" n
    | Target.MaxHealth -> sprintf "%d" (n * 10)
    | Target.JumpVelocityRight -> sprintf "%d.25" n
    | Target.Label -> sprintf "L%d" n

  /// What goes in the source for a literal, as the file spells it.
  let spelledFor (target: Target) (n: int) : string =
    match target with
    | Target.Label -> sprintf "\"L%d\"" n
    | other -> literalFor other n

  let targets = [| Target.Gravity; Target.MaxHealth; Target.JumpVelocityRight; Target.Label |]

  // ── generation ──

  let faultOf (rnd: Random) : Fault =
    match rnd.Next 5 with
    | 0 -> Fault.CrashBefore
    | 1 -> Fault.CrashTorn(rnd.Next(1, 99))
    | 2 -> Fault.CrashAfter
    | 3 -> Fault.FailCleanly
    | _ -> Fault.CrashTorn 50

  /// A fault on about a third of the door's operations, at one of its first steps.
  let faultsFor (rnd: Random) : (int * Fault) list =
    match rnd.Next 3 with
    | 0 -> [ rnd.Next(1, 9), faultOf rnd ]
    | _ -> []

  let scenarioOf (seed: int) : Scenario =
    let rnd = Random(seed)
    let family = match seed % 2 with | 0 -> Family.DoorOnly | _ -> Family.WithOutsideEdits
    let count = rnd.Next(6, 15)
    let op () =
      let target = targets.[rnd.Next targets.Length]
      let value = rnd.Next(1, 10)
      match family, rnd.Next 100 with
      | _, n when n < 45 -> Op.Nudge(target, value, (match rnd.Next 5 with | 0 -> Seen.Stale | _ -> Seen.Fresh))
      | _, n when n < 60 -> Op.Undo
      | _, n when n < 68 -> Op.Redo
      | _, n when n < 76 -> Op.Look
      | _, n when n < 80 -> Op.NudgeUnowned
      | Family.WithOutsideEdits, n when n < 92 -> Op.OutsideEdit(target, value)
      | Family.WithOutsideEdits, _ -> Op.OutsideReformat
      | Family.DoorOnly, _ -> Op.Look
    let steps =
      [ for _ in 1 .. count ->
          let o = op ()
          match o with
          | Op.OutsideEdit _
          | Op.OutsideReformat -> { Op = o; Faults = [] }
          | _ -> { Op = o; Faults = faultsFor rnd } ]
    { Seed = seed; Family = family; Steps = steps }

  // ── running ──

  let utf8 = UTF8Encoding(false)

  let textAt (source: string) (target: Target) : string =
    match resolve source (addressOf target) with
    | Ok resolved -> resolved.Text
    | Error _ -> ""

  let editOutside (disk: Disk) (target: Target) (value: int) =
    let source = disk.TextOf sourcePath
    match resolve source (addressOf target) with
    | Ok resolved -> disk.PutText(sourcePath, replaceRange source resolved.Range (spelledFor target (value + 100)))
    | Error _ -> ()

  let reformat (disk: Disk) (counter: int) =
    let anchor = "module Game.Tuning\n"
    let source = disk.TextOf sourcePath
    match source.IndexOf anchor with
    | -1 -> ()
    | at -> disk.PutText(sourcePath, source.Substring(0, at + anchor.Length) + sprintf "// reformatted %d\n" counter + source.Substring(at + anchor.Length))

  let asRequest (world: World) (op: Op) (seenText: string) : Result<NudgeRequest, NudgeRefusal> =
    match op with
    | Op.Nudge(target, value, _) ->
      let literal = literalFor target value
      Ok(NudgeRequest.Set(file world, addressOf target, seenOf seenText, NudgeValue.LiteralText literal))
    | Op.NudgeUnowned ->
      parse owned world.Disk.Steps.KindOf
        { Action = "set"; File = otherPath; Address = "Other.y"; Seen = contentHash "1"; Literal = "2"; Expression = "" }
    | Op.Undo -> Ok(NudgeRequest.Undo(file world))
    | Op.Redo -> Ok(NudgeRequest.Redo(file world))
    | Op.Look -> Ok(NudgeRequest.Inspect(file world, InspectTarget.WholeFile))
    | Op.OutsideEdit _
    | Op.OutsideReformat -> failwith "an outside edit is not a door request"

  /// Run `scenario` under `strategy` and `seen`, and watch every step.
  let trace (strategy: Strategy) (seen: SeenPolicy) (scenario: Scenario) : Trace =
    let world = create ()
    world.Disk.PutText(otherPath, otherSource)
    let initial = world.Disk.BytesOf sourcePath
    let initialSource = utf8.GetString initial
    let journal = journalPath world
    let reformats = ref 0
    let observations =
      scenario.Steps
      |> List.mapi (fun index step ->
        let fileBefore = world.Disk.BytesOf sourcePath
        let otherBefore = world.Disk.BytesOf otherPath
        let journalBefore = world.Disk.BytesOf journal
        let currentSource = utf8.GetString fileBefore
        let seenText, actualText =
          match step.Op with
          | Op.Nudge(target, _, which) ->
            let actual = textAt currentSource target
            let given = match which with | Seen.Fresh -> actual | Seen.Stale -> textAt initialSource target
            (match seen with | SeenPolicy.AsGiven -> given | SeenPolicy.SkipCheckTwin -> actual), actual
          | _ -> "", ""
        let result =
          match step.Op with
          | Op.OutsideEdit(target, value) ->
            editOutside world.Disk target value
            StepResult.Returned(Ok { Outcome = NudgeOutcome.Unchanged(addressOf target, ""); Notes = [] })
          | Op.OutsideReformat ->
            reformats.Value <- reformats.Value + 1
            reformat world.Disk reformats.Value
            StepResult.Returned(Ok { Outcome = NudgeOutcome.Unchanged(gravity, ""); Notes = [] })
          | op ->
            world.Disk.Arm step.Faults
            let outcome =
              try
                match asRequest world op seenText with
                | Error refusal -> StepResult.Returned(Error refusal)
                | Ok request -> StepResult.Returned(runWith strategy world.Ports request)
              with SimulatedCrash(_, operation) -> StepResult.Crashed operation
            world.Disk.Disarm()
            outcome
        { Index = index
          Step = step
          FileBefore = fileBefore
          FileAfter = world.Disk.BytesOf sourcePath
          OtherBefore = otherBefore
          OtherAfter = world.Disk.BytesOf otherPath
          PathsAfter = world.Disk.Paths
          JournalBefore = journalBefore
          JournalAfter = world.Disk.BytesOf journal
          SeenUsed = (match seenText with | "" -> "" | text -> contentHash text)
          ActualHash = (match actualText with | "" -> "" | text -> contentHash text)
          Result = result })
    // Recovery: look once (which settles whatever a crash left), then undo everything.
    let recovery =
      try
        match runWith strategy world.Ports (NudgeRequest.Inspect(file world, InspectTarget.WholeFile)) with
        | Error refusal -> Recovery.Stuck refusal
        | Ok _ ->
          let rec loop remaining =
            match remaining with
            | 0 -> Recovery.Stuck NudgeRefusal.NothingToRedo
            | _ ->
              match runWith strategy world.Ports (NudgeRequest.Undo(file world)) with
              | Ok _ -> loop (remaining - 1)
              | Error NudgeRefusal.NothingToUndo -> Recovery.Restored
              | Error refusal -> Recovery.Stuck refusal
          loop 200
      with SimulatedCrash(_, operation) -> Recovery.Crashed operation
    { Initial = initial
      Observations = observations
      Recovery = recovery
      AfterRecovery = world.Disk.BytesOf sourcePath }

  /// The production door, the honest hash.
  let realTrace (scenario: Scenario) : Trace = trace production SeenPolicy.AsGiven scenario
