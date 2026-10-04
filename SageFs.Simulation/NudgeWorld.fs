namespace SageFs.Simulation

open System.Threading
open SageFs.Features.Tweak
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.NudgeIo
open SageFs.Features.Tweak.Nudge
open SageFs.Simulation.NudgeDisk

/// One session, one owned source file, one in-memory disk: the world the nudge
/// tests and the nudge simulation share, so a unit test and a seeded scenario
/// start from the same place.
module NudgeWorld =

  let repo = "/repo"
  let sourcePath = "/repo/src/Tuning.fs"
  let otherPath = "/repo/src/Other.fs"
  let tweaksDir = "/data/tweaks"
  let session = "s1"

  /// A tuning record with every kind of nudgeable value: a float, a formula, an
  /// integer, a union case and a string.
  let tuningSource =
    "module Game.Tuning\n\nlet gravity = 9.8\n\nlet tuning =\n  { JumpVelocity = gravity * 2.0\n    MaxHealth = 100\n    Difficulty = Easy\n    Label = \"A\" }\n"

  let owned : OwnedFiles =
    { Session = session; WorkingDirectory = repo; Paths = Set.ofList [ sourcePath ]; Watched = Set.ofList [ sourcePath ] }

  let gravity : TweakAddress = { ModulePath = [ "Game"; "Tuning" ]; BindingName = "gravity"; Path = [] }

  let tuningField (name: string) : TweakAddress =
    { ModulePath = [ "Game"; "Tuning" ]; BindingName = "tuning"; Path = [ PathStep.RecordField name ] }

  let jumpVelocity = tuningField "JumpVelocity"
  let jumpVelocityRight : TweakAddress = { jumpVelocity with Path = jumpVelocity.Path @ [ PathStep.BinOpRight ] }
  let maxHealth = tuningField "MaxHealth"
  let difficulty = tuningField "Difficulty"
  let label = tuningField "Label"

  type World =
    { Disk: Disk
      Ports: Ports
      Clock: int64 ref }

  let portsFor (disk: Disk) (clock: int64 ref) : Ports =
    { Files = disk.Steps
      Now = fun () -> Interlocked.Increment(&clock.contents)
      JournalPathOf = fun file -> NudgeFs.journalPathFor tweaksDir session (Ownership.pathOf file) }

  /// A fresh world holding `source` at the owned path.
  let createWith (source: string) : World =
    let disk = Disk()
    disk.PutText(sourcePath, source)
    let clock = ref 0L
    { Disk = disk; Ports = portsFor disk clock; Clock = clock }

  let create () : World = createWith tuningSource

  /// The owned file, built the only way it can be: by `Ownership.tryOwn`.
  let file (world: World) : OwnedFile =
    match Ownership.tryOwn owned world.Disk.Steps.KindOf sourcePath with
    | Ok owned -> owned
    | Error why -> failwithf "the world's own source file is not owned: %A" why

  let journalPath (world: World) : string = world.Ports.JournalPathOf(file world)

  let source (world: World) : string = world.Disk.TextOf sourcePath

  let inspectAll (world: World) : Result<Ran, NudgeRefusal> =
    runUnlocked world.Ports (NudgeRequest.Inspect(file world, InspectTarget.WholeFile))

  let seenOf (text: string) : SeenHash =
    match SeenHash.tryParse (contentHash text) with
    | Ok seen -> seen
    | Error fault -> failwithf "a content hash is a valid seen hash: %A" fault

  /// Set `address` to a literal, saying the caller last saw `currentText` there.
  let setLiteral (world: World) (address: TweakAddress) (currentText: string) (literal: string) : Result<Ran, NudgeRefusal> =
    runUnlocked world.Ports (NudgeRequest.Set(file world, address, seenOf currentText, NudgeValue.LiteralText literal))

  let setExpression (world: World) (address: TweakAddress) (currentText: string) (expression: string) : Result<Ran, NudgeRefusal> =
    runUnlocked world.Ports (NudgeRequest.Set(file world, address, seenOf currentText, NudgeValue.ExpressionText expression))

  let undo (world: World) : Result<Ran, NudgeRefusal> = runUnlocked world.Ports (NudgeRequest.Undo(file world))
  let redo (world: World) : Result<Ran, NudgeRefusal> = runUnlocked world.Ports (NudgeRequest.Redo(file world))
