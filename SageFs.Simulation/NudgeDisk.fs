namespace SageFs.Simulation

open System
open System.Collections.Generic
open System.Text
open SageFs.Features.Tweak.NudgeIo

/// An in-memory disk for the nudge door's `FileSteps`, with faults placed by
/// data. The real protocol code (`AtomicWrite`, `Journal`, `Nudge.runWith`) runs
/// against it, so a crash between two of its steps is something a scenario
/// SAYS, not something a test hopes for.
///
/// The mutating steps are numbered from 1 in the order the code under test takes
/// them (`WriteTemp`, `Rename`, `Append`, `Discard`). Reads are not numbered:
/// they change nothing, so a crash during one is not a different state.
module NudgeDisk =

  /// What happens at one numbered step.
  [<RequireQualifiedAccess>]
  type Fault =
    /// The process dies before the step does anything.
    | CrashBefore
    /// The process dies partway: a write keeps only its first `keptPercent` of
    /// bytes. `Rename` is atomic, so for it a torn crash is the same as `CrashBefore`.
    | CrashTorn of keptPercent: int
    /// The step completes and then the process dies, before anything after it.
    | CrashAfter
    /// The step reports an error and did nothing. The process lives.
    | FailCleanly

  /// The process died at this numbered step. Raised out of the step, so nothing
  /// after it in the code under test runs, exactly as a real crash.
  exception SimulatedCrash of step: int * operation: FileOperation

  let fault(operation: FileOperation) (path: string) : FileFault =
    { Operation = operation; Path = path; Reason = "simulated failure" }

  /// One disk. Not thread safe on purpose: scenarios run one operation at a time.
  type Disk() =
    let files = Dictionary<string, byte[]>(StringComparer.Ordinal)
    let links = HashSet<string>(StringComparer.Ordinal)
    let gate = obj ()
    let mutable plan : Map<int, Fault> = Map.empty
    let mutable step = 0

    let utf8 = UTF8Encoding(false)

    /// Count a mutating step and say what fault, if any, is planned for it.
    let enter () : int * Fault option =
      step <- step + 1
      step, Map.tryFind step plan

    member _.Put(path: string, bytes: byte[]) = files.[path] <- bytes
    member this.PutText(path: string, text: string) = this.Put(path, utf8.GetBytes text)
    member _.Remove(path: string) = files.Remove path |> ignore
    member _.Exists(path: string) = files.ContainsKey path
    member _.BytesOf(path: string) : byte[] =
      match files.TryGetValue path with
      | true, bytes -> bytes
      | false, _ -> Array.empty
    member this.TextOf(path: string) : string = utf8.GetString(this.BytesOf path)
    member _.Paths : string list = files.Keys |> Seq.sort |> List.ofSeq
    member _.Link(path: string) = links.Add path |> ignore

    /// Arm the faults for the next operation and restart the step count.
    member _.Arm(faults: (int * Fault) list) =
      plan <- Map.ofList faults
      step <- 0

    member _.Disarm() =
      plan <- Map.empty
      step <- 0

    /// How many mutating steps the last armed operation took.
    member _.StepsTaken : int = step

    /// A deep copy, so a scenario can run the same operation against the same state twice.
    member _.Clone() : Disk =
      let copy = Disk()
      for KeyValue(path, bytes) in files do copy.Put(path, Array.copy bytes)
      for link in links do copy.Link link
      copy

    /// The steps, serialized: the nudge door's own lock is what keeps two nudges apart,
    /// so the disk itself only has to survive being touched from two threads.
    member this.Steps : FileSteps =
      let raw = this.UnsafeSteps
      { ReadBytes = fun path -> lock gate (fun () -> raw.ReadBytes path)
        KindOf = fun path -> lock gate (fun () -> raw.KindOf path)
        MakeDirectory = fun path -> lock gate (fun () -> raw.MakeDirectory path)
        WriteTemp = fun path bytes -> lock gate (fun () -> raw.WriteTemp path bytes)
        Rename = fun source target -> lock gate (fun () -> raw.Rename source target)
        Append = fun path bytes -> lock gate (fun () -> raw.Append path bytes)
        Discard = fun path -> lock gate (fun () -> raw.Discard path) }

    member this.UnsafeSteps : FileSteps =
      let crash operation = raise (SimulatedCrash(step, operation))
      { ReadBytes =
          fun path ->
            match files.TryGetValue path with
            | true, bytes -> Ok(Array.copy bytes)
            | false, _ -> Error(fault FileOperation.Reading path)
        KindOf =
          fun path ->
            match links.Contains path, files.ContainsKey path with
            | true, _ -> FileKind.SymbolicLink
            | false, true -> FileKind.RegularFile
            | false, false -> FileKind.Missing
        // Directories are not modelled: a path exists when a file is at it.
        MakeDirectory = fun _ -> Ok()
        WriteTemp =
          fun path bytes ->
            let _, planned = enter ()
            match planned with
            | Some Fault.CrashBefore -> crash FileOperation.WritingTemp
            | Some(Fault.CrashTorn kept) ->
              files.[path] <- Array.sub bytes 0 (bytes.Length * kept / 100)
              crash FileOperation.WritingTemp
            | Some Fault.CrashAfter ->
              files.[path] <- Array.copy bytes
              crash FileOperation.WritingTemp
            | Some Fault.FailCleanly -> Error(fault FileOperation.WritingTemp path)
            | None ->
              files.[path] <- Array.copy bytes
              Ok()
        Rename =
          fun source target ->
            let _, planned = enter ()
            let move () =
              match files.TryGetValue source with
              | true, bytes ->
                files.[target] <- bytes
                files.Remove source |> ignore
                Ok()
              | false, _ -> Error(fault FileOperation.Renaming source)
            match planned with
            | Some Fault.CrashBefore
            | Some(Fault.CrashTorn _) -> crash FileOperation.Renaming
            | Some Fault.CrashAfter ->
              move () |> ignore
              crash FileOperation.Renaming
            | Some Fault.FailCleanly -> Error(fault FileOperation.Renaming source)
            | None -> move ()
        Append =
          fun path bytes ->
            let _, planned = enter ()
            let existing = this.BytesOf path
            match planned with
            | Some Fault.CrashBefore -> crash FileOperation.Appending
            | Some(Fault.CrashTorn kept) ->
              files.[path] <- Array.append existing (Array.sub bytes 0 (bytes.Length * kept / 100))
              crash FileOperation.Appending
            | Some Fault.CrashAfter ->
              files.[path] <- Array.append existing bytes
              crash FileOperation.Appending
            | Some Fault.FailCleanly -> Error(fault FileOperation.Appending path)
            | None ->
              files.[path] <- Array.append existing bytes
              Ok()
        Discard =
          fun path ->
            let _, planned = enter ()
            match planned with
            | Some Fault.CrashBefore
            | Some(Fault.CrashTorn _) -> crash FileOperation.Discarding
            | Some Fault.CrashAfter ->
              files.Remove path |> ignore
              crash FileOperation.Discarding
            | Some Fault.FailCleanly
            | None -> files.Remove path |> ignore }
