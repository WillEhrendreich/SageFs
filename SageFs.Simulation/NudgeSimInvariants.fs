namespace SageFs.Simulation

open System.Text
open SageFs.Features.Tweak
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.TweakLog
open SageFs.Features.Tweak.NudgeIo
open SageFs.Features.Tweak.Nudge
open SageFs.Simulation.NudgeWorld
open SageFs.Simulation.NudgeSim

/// Named invariants over `NudgeSim`'s trace. Each one is a property of the real
/// door, stated about what is on the disk and nothing else, so a twin that breaks
/// the door shows up as a violation here.
module NudgeSimInvariants =

  type Violation = { Index: int; Why: string }

  let utf8 = UTF8Encoding(false)

  let violation index why : Violation option = Some { Index = index; Why = why }

  let textOf (bytes: byte[]) = utf8.GetString bytes

  let isDoorOp (op: Op) =
    match op with
    | Op.OutsideEdit _
    | Op.OutsideReformat -> false
    | _ -> true

  /// How many whole events a journal holds. -1 when it does not decode at all.
  let eventCount (bytes: byte[]) : int =
    match bytes.Length with
    | 0 -> 0
    | _ ->
      match TweakLogFormat.decodeSegment (Journal.fingerprintFor sourcePath) bytes with
      | Ok decoded -> decoded.Events.Length
      | Error _ -> -1

  /// The span of the first and last character that differ between two texts, in
  /// each: (start, endBefore, endAfter), the common prefix and suffix excluded.
  let diffRegion (before: string) (after: string) : int * int * int =
    let shortest = min before.Length after.Length
    let mutable prefix = 0
    while prefix < shortest && before.[prefix] = after.[prefix] do
      prefix <- prefix + 1
    let mutable suffix = 0
    while suffix < shortest - prefix && before.[before.Length - 1 - suffix] = after.[after.Length - 1 - suffix] do
      suffix <- suffix + 1
    prefix, before.Length - suffix, after.Length - suffix

  /// Is `after` exactly `before` with one whole addressable expression replaced by a
  /// new expression that parses? Every byte outside that range is the same.
  let isOneExpressionReplaced (before: string) (after: string) : bool =
    let start, endBefore, _ = diffRegion before after
    match addressesOf before with
    | Error _ -> false
    | Ok addresses ->
      addresses
      |> List.exists (fun address ->
        match resolve before address with
        | Error _ -> false
        | Ok resolved ->
          let marker = '\u0000'
          let marked = replaceRange before resolved.Range (string marker)
          let rangeStart = marked.IndexOf marker
          let rangeEnd = rangeStart + resolved.Text.Length
          match rangeStart <= start && endBefore <= rangeEnd with
          | false -> false
          | true ->
            let newLength = after.Length - (before.Length - resolved.Text.Length)
            match newLength >= 0 && rangeStart + newLength <= after.Length with
            | false -> false
            | true ->
              let newText = after.Substring(rangeStart, newLength)
              replaceRange before resolved.Range newText = after && (parseExpr newText |> Result.isOk))

  /// The file is never half written: whatever a step does to it, afterwards it is
  /// the bytes it had or the bytes it had with one whole expression replaced, and
  /// it still parses.
  let fileNeverPartial (trace: Trace) : Violation list =
    trace.Observations
    |> List.choose (fun o ->
      match isDoorOp o.Step.Op, o.FileAfter = o.FileBefore with
      | false, _
      | _, true -> None
      | true, false ->
        let before = textOf o.FileBefore
        let after = textOf o.FileAfter
        match addressesOf after with
        | Error _ -> violation o.Index "the file no longer parses after the step"
        | Ok _ ->
          match isOneExpressionReplaced before after with
          | true -> None
          | false -> violation o.Index "the file changed in more than one whole expression, or to text that is not an expression")

  /// A refusal leaves the file byte-identical.
  let refusalLeavesFileIdentical (trace: Trace) : Violation list =
    trace.Observations
    |> List.choose (fun o ->
      match o.Result with
      | StepResult.Returned(Error refusal) when o.FileAfter <> o.FileBefore ->
        violation o.Index (sprintf "%s changed the file" (NudgeRefusal.token refusal))
      | _ -> None)

  /// The file only changes after a durable record says how to put it back: a step
  /// that changed the file added at least one event to the journal.
  let fileChangeIsJournaled (trace: Trace) : Violation list =
    trace.Observations
    |> List.choose (fun o ->
      match isDoorOp o.Step.Op, o.FileAfter = o.FileBefore with
      | false, _
      | _, true -> None
      | true, false ->
        match eventCount o.JournalAfter > eventCount o.JournalBefore with
        | true -> None
        | false -> violation o.Index "the file changed and the journal did not grow, so nothing can put it back")

  /// The journal always decodes, and after a call that came back it has no torn tail.
  let journalNeverYieldsAPartialEvent (trace: Trace) : Violation list =
    trace.Observations
    |> List.choose (fun o ->
      match o.JournalAfter.Length with
      | 0 -> None
      | _ ->
        match TweakLogFormat.decodeSegment (Journal.fingerprintFor sourcePath) o.JournalAfter with
        | Error reason -> violation o.Index (sprintf "the journal does not decode: %s" reason.CorruptionReason)
        | Ok decoded ->
          match o.Step.Op, o.Result, decoded.TornTail with
          | Op.NudgeUnowned, _, _ -> None
          | _, StepResult.Returned(Ok _), true -> violation o.Index "a call that came back left a torn journal tail"
          | _ -> None)

  /// A nudge made with a hash that is not what is there never lands.
  let staleSeenNeverWrites (trace: Trace) : Violation list =
    trace.Observations
    |> List.choose (fun o ->
      match o.Step.Op, o.Result with
      | Op.Nudge _, StepResult.Returned(Ok { Outcome = NudgeOutcome.Written _ }) when o.SeenUsed <> o.ActualHash ->
        violation o.Index "a write landed on an expression that no longer hashed to what the caller saw"
      | _ -> None)

  /// Nothing but the owned file, its journal and their temp files is ever on the disk, and
  /// the file the session does not own is never touched.
  let neverWritesOutsideOwnedFiles (trace: Trace) : Violation list =
    let allowed = [ sourcePath; otherPath; AtomicWrite.tempPathFor sourcePath ]
    trace.Observations
    |> List.choose (fun o ->
      let journalPaths =
        o.PathsAfter |> List.filter (fun p -> p.StartsWith(tweaksDir, System.StringComparison.Ordinal))
      let stray = o.PathsAfter |> List.filter (fun p -> not (List.contains p allowed) && not (List.contains p journalPaths))
      match stray, o.OtherAfter = o.OtherBefore with
      | [], true -> None
      | _ :: _, _ -> violation o.Index (sprintf "an unexpected path appeared: %A" stray)
      | [], false -> violation o.Index "a file the session does not own was changed")

  /// A write that came back as written is what the file holds.
  let writtenOutcomeMatchesTheFile (trace: Trace) : Violation list =
    trace.Observations
    |> List.choose (fun o ->
      match o.Result with
      | StepResult.Returned(Ok { Outcome = NudgeOutcome.Written receipt })
      | StepResult.Returned(Ok { Outcome = NudgeOutcome.Undone receipt })
      | StepResult.Returned(Ok { Outcome = NudgeOutcome.Redone receipt }) ->
        match resolve (textOf o.FileAfter) receipt.Address with
        | Ok resolved when resolved.Text = receipt.After -> None
        | _ -> violation o.Index "the receipt says one thing and the file holds another"
      | _ -> None)

  /// With no outside edits, whatever crashed and whatever failed, looking once and then
  /// undoing everything brings back the original bytes exactly. With outside edits the
  /// file must still parse, and recovery may stop at a named refusal, never a torn file.
  let recoveryRestoresTheOriginal (scenario: Scenario) (trace: Trace) : Violation list =
    match scenario.Family with
    | Family.DoorOnly ->
      match trace.Recovery, trace.AfterRecovery = trace.Initial with
      | Recovery.Restored, true -> []
      | recovery, _ -> [ { Index = -1; Why = sprintf "recovery ended %A and the original %s" recovery (match trace.AfterRecovery = trace.Initial with | true -> "is back" | false -> "is not back") } ]
    | Family.WithOutsideEdits ->
      match addressesOf (textOf trace.AfterRecovery) with
      | Ok _ -> []
      | Error _ -> [ { Index = -1; Why = "the file does not parse after recovery" } ]

  /// Every invariant by name, as a check that has not run yet.
  let named : (string * (Scenario -> Trace -> Violation list)) list =
    [ "FILE-NEVER-PARTIAL", (fun _ trace -> fileNeverPartial trace)
      "REFUSAL-LEAVES-FILE-IDENTICAL", (fun _ trace -> refusalLeavesFileIdentical trace)
      "FILE-CHANGE-IS-JOURNALED-FIRST", (fun _ trace -> fileChangeIsJournaled trace)
      "JOURNAL-NEVER-YIELDS-A-PARTIAL-EVENT", (fun _ trace -> journalNeverYieldsAPartialEvent trace)
      "STALE-SEEN-NEVER-WRITES", (fun _ trace -> staleSeenNeverWrites trace)
      "NEVER-WRITES-OUTSIDE-OWNED-FILES", (fun _ trace -> neverWritesOutsideOwnedFiles trace)
      "WRITTEN-OUTCOME-MATCHES-THE-FILE", (fun _ trace -> writtenOutcomeMatchesTheFile trace)
      "RECOVERY-RESTORES-THE-ORIGINAL", recoveryRestoresTheOriginal ]

  let all (scenario: Scenario) (trace: Trace) : (string * Violation list) list =
    named |> List.map (fun (name, check) -> name, check scenario trace)
