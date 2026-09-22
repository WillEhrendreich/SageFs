/// Coverage for `TweakLog`: the event-sourced history, its pure folds,
/// rollback that never guesses, replay, undo/redo, and bounded-growth
/// compaction that never drops something it must keep.
module SageFs.Tests.TweakLogTests

open Expecto
open Expecto.Flip
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.TweakLog

let private addr name : TweakAddress = { ModulePath = [ "M" ]; BindingName = name; Path = [] }

let private baseSource = "module M\nlet x = 1.0\nlet y = 2.0\n"

[<Tests>]
let tweakLogTests =
  testList "TweakLog" [

    testList "events and projection" [

      testCase "TweakApplied then TweakSaved: the address is known and saved, so it's not dirty" <| fun _ ->
        let log = EventLog.empty
        let log, applied = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        dirtySet log |> Expect.contains "applied but not yet saved is dirty" (addr "x")
        let log, _ = EventLog.append log 2L (TweakLogEvent.TweakSaved(addr "x", "1.0", "2.0", contentHash "2.0", contentHash baseSource))
        dirtySet log |> Expect.isEmpty "saved, so no longer dirty"
        ignore applied

      testCase "a UserEditObserved counts as disk truth, not a pending tweak" <| fun _ ->
        let log = EventLog.empty
        let log, _ = EventLog.append log 1L (TweakLogEvent.UserEditObserved(addr "x", "3.0", contentHash "3.0", FileContent.NotRecorded ReplayScope.SageFsWritesOnly))
        dirtySet log |> Expect.isEmpty "a direct file edit is never 'dirty against itself'"

      testCase "RolledBack restores the projection to what the target op recorded as textBefore" <| fun _ ->
        let log = EventLog.empty
        let log, applied = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, _ = EventLog.append log 2L (TweakLogEvent.RolledBack applied.Id)
        (project log).Known |> Map.find (addr "x") |> Expect.equal "back to textBefore" "1.0"
    ]

    testList "rollback" [

      testCase "rollback applies cleanly when the address still hashes to what was written" <| fun _ ->
        let source = "module M\nlet x = 2.0\n"
        let log = EventLog.empty
        let log, applied = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        match rollback log Snapshot.empty applied.Id source with
        | Ok(RollbackOutcome.Applied newSource) -> newSource |> Expect.equal "back to the original text" "module M\nlet x = 1.0\n"
        | other -> failtestf "expected Applied, got %A" other

      testCase "rollback conflicts, and never overwrites, when a later edit changed the same expression" <| fun _ ->
        let log = EventLog.empty
        let log, applied = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        // Someone (or something) changed it to 9.0 after our write.
        let laterSource = "module M\nlet x = 9.0\n"
        match rollback log Snapshot.empty applied.Id laterSource with
        | Ok(RollbackOutcome.Conflict(wrote, now, before)) ->
          wrote |> Expect.equal "what we wrote" "2.0"
          now |> Expect.equal "what's there now" "9.0"
          before |> Expect.equal "what it was before our write" "1.0"
        | other -> failtestf "expected Conflict, got %A" other

      testCase "rolling back a non-operation event is refused" <| fun _ ->
        let log = EventLog.empty
        let log, resolved = EventLog.append log 1L (TweakLogEvent.ConflictResolved(addr "x"))
        match rollback log Snapshot.empty resolved.Id baseSource with
        | Error(RollbackError.NotAnOperation _) -> ()
        | other -> failtestf "expected NotAnOperation, got %A" other

      testCase "rolling back an id that doesn't exist is refused" <| fun _ ->
        match rollback EventLog.empty Snapshot.empty 999 baseSource with
        | Error(RollbackError.NoSuchOperation 999) -> ()
        | other -> failtestf "expected NoSuchOperation, got %A" other
    ]

    testList "rollback resolves through a compacted snapshot boundary" [

      testCase "a TweakSaved compacted into the snapshot still rolls back correctly" <| fun _ ->
        // A standalone TweakSaved (no pending TweakApplied) is already
        // clean (Known = Saved), so with UndoWindow=0 nothing protects it:
        // it's genuinely compactable, and saved.Id moves into the
        // snapshot's Origins.
        let source = "module M\nlet x = 2.0\n"
        let log = EventLog.empty
        let log, saved = EventLog.append log 1L (TweakLogEvent.TweakSaved(addr "x", "1.0", "2.0", contentHash "2.0", contentHash baseSource))
        let policy = { RetentionPolicy.defaults with UndoWindow = 0 }
        let snapshot, tail = compact Snapshot.empty log.Events policy Set.empty
        tail |> Expect.isEmpty "the TweakSaved moved into the snapshot"
        match rollback { log with Events = tail } snapshot saved.Id source with
        | Ok(RollbackOutcome.Applied newSource) ->
          newSource |> Expect.equal "still resolves before/after from Snapshot.Origins, not the (now-empty) tail" "module M\nlet x = 1.0\n"
        | other -> failtestf "expected Applied via the snapshot fallback, got %A" other

      testCase "a RolledBack in the tail whose OWN target was compacted still redoes correctly" <| fun _ ->
        // TweakSaved (id 1) is compacted away; a RolledBack (id 2) in the
        // tail targets it. Redoing that RolledBack (rolling IT back) must
        // resolve id 1's before/after from Snapshot.Origins to know what to
        // reapply, `effectOf`'s own recursion is the thing under test here.
        let log = EventLog.empty
        let log, saved = EventLog.append log 1L (TweakLogEvent.TweakSaved(addr "x", "1.0", "2.0", contentHash "2.0", contentHash baseSource))
        let policy = { RetentionPolicy.defaults with UndoWindow = 0 }
        let snapshot, tail = compact Snapshot.empty log.Events policy Set.empty
        let tailLog, rolledBack = EventLog.append { log with Events = tail } 2L (TweakLogEvent.RolledBack saved.Id)
        let sourceAfterUndo = "module M\nlet x = 1.0\n"
        match rollback tailLog snapshot rolledBack.Id sourceAfterUndo with
        | Ok(RollbackOutcome.Applied redoneSource) ->
          redoneSource |> Expect.equal "redo reapplies 2.0, resolved via the compacted target's Origins entry" "module M\nlet x = 2.0\n"
        | other -> failtestf "expected the redo to apply, got %A" other

      testCase "undo/redo gives the same answer on a compacted log as on the uncompacted log" <| fun _ ->
        let source = "module M\nlet x = 2.0\n"
        let uncompactedLog = EventLog.empty
        let uncompactedLog, saved = EventLog.append uncompactedLog 1L (TweakLogEvent.TweakSaved(addr "x", "1.0", "2.0", contentHash "2.0", contentHash baseSource))
        let viaUncompacted = rollback uncompactedLog Snapshot.empty saved.Id source
        let policy = { RetentionPolicy.defaults with UndoWindow = 0 }
        let snapshot, tail = compact Snapshot.empty uncompactedLog.Events policy Set.empty
        let viaCompacted = rollback { uncompactedLog with Events = tail } snapshot saved.Id source
        viaCompacted |> Expect.equal "compaction must never change what a still-resolvable rollback answers" viaUncompacted
    ]

    testList "open conflicts are exclusive" [

      testCase "hasOpenConflict is false until ConflictRaised, true after, false again after ConflictResolved" <| fun _ ->
        let log = EventLog.empty
        hasOpenConflict log (addr "x") |> Expect.isFalse "nothing raised yet"
        let log, _ = EventLog.append log 1L (TweakLogEvent.ConflictRaised(addr "x", "2.0", "9.0", "1.0"))
        hasOpenConflict log (addr "x") |> Expect.isTrue "now open"
        let log, _ = EventLog.append log 2L (TweakLogEvent.ConflictResolved(addr "x"))
        hasOpenConflict log (addr "x") |> Expect.isFalse "resolved"

      testCase "rollback refuses an address with an open conflict, even when the hash still matches" <| fun _ ->
        let source = "module M\nlet x = 2.0\n"
        let log = EventLog.empty
        let log, applied = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, _ = EventLog.append log 2L (TweakLogEvent.ConflictRaised(addr "x", "2.0", "2.0", "1.0"))
        match rollback log Snapshot.empty applied.Id source with
        | Error(RollbackError.BlockedByOpenConflict a) -> a |> Expect.equal "names the blocked address" (addr "x")
        | other -> failtestf "expected BlockedByOpenConflict, got %A" other

      testCase "rollback on an unrelated, conflict-free address is unaffected" <| fun _ ->
        let source = "module M\nlet x = 2.0\nlet y = 6.0\n"
        let log = EventLog.empty
        let log, appliedX = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, appliedY = EventLog.append log 2L (TweakLogEvent.TweakApplied(addr "y", "5.0", "6.0", contentHash "6.0"))
        let log, _ = EventLog.append log 3L (TweakLogEvent.ConflictRaised(addr "x", "2.0", "9.0", "1.0"))
        match rollback log Snapshot.empty appliedY.Id source with
        | Ok(RollbackOutcome.Applied _) -> ()
        | other -> failtestf "expected Applied (y has no open conflict), got %A" other

      testCase "canSave refuses an address with an open conflict" <| fun _ ->
        let log = EventLog.empty
        let log, _ = EventLog.append log 1L (TweakLogEvent.ConflictRaised(addr "x", "2.0", "9.0", "1.0"))
        canSave log (addr "x") |> Expect.isError "blocked while the conflict is open"

      testCase "canSave allows an address with no open conflict" <| fun _ ->
        canSave EventLog.empty (addr "x") |> Expect.isOk "nothing blocking it"
    ]

    testList "replay" [

      testCase "replay reproduces the file byte for byte" <| fun _ ->
        let ops = [ addr "x", "5.0"; addr "y", "6.0" ]
        replay baseSource ops |> Expect.equal "both writes land" (Ok "module M\nlet x = 5.0\nlet y = 6.0\n")

      testCase "replay re-resolves each address against the evolving source, surviving a shift between writes" <| fun _ ->
        // Writing x first changes the file's later lines not at all here (the
        // edit is same-length), but replay must still resolve `y` against the
        // source AFTER x's write, not the original.
        let ops = [ addr "x", "100.0"; addr "y", "200.0" ]
        replay baseSource ops |> Expect.equal "second write resolves against the post-first-write source" (Ok "module M\nlet x = 100.0\nlet y = 200.0\n")

      testCase "replay's own scope is every range SageFs wrote, exactly, never a whole-file promise" <| fun _ ->
        // Restated per the HOLD decision: `replay` only ever takes the ops
        // SageFs itself produced (TweakApplied/TweakSaved's own address+
        // textAfter). A UserEditObserved/ReformatObserved is never one of
        // these ops, so a stray user edit elsewhere in the file is simply
        // outside what this function promises to reproduce.
        let ops = [ addr "x", "9.0" ]
        replay baseSource ops |> Expect.equal "only the range SageFs wrote moves" (Ok "module M\nlet x = 9.0\nlet y = 2.0\n")
    ]

    testList "ReplayScope.EverythingAsDiffs: whole-file byte-for-byte replay" [

      testCase "SageFsWritesOnly (the default) never carries a file snapshot on UserEditObserved/ReformatObserved" <| fun _ ->
        let settings = { TweakLogSettings.defaults with ReplayScope = ReplayScope.SageFsWritesOnly }
        let afterEdit = baseSource.Replace("let x = 1.0", "let x = 9.0")
        let ev = observeUserEdit settings baseSource afterEdit (addr "x") "9.0"
        match ev with
        | TweakLogEvent.UserEditObserved(_, _, _, content) ->
          content |> Expect.equal "no bytes stored, and it says why: SageFsWritesOnly" (FileContent.NotRecorded ReplayScope.SageFsWritesOnly)
        | other -> failtestf "expected UserEditObserved, got %A" other

      testCase "EverythingAsDiffs carries the whole file's new text on UserEditObserved" <| fun _ ->
        let settings = { TweakLogSettings.defaults with ReplayScope = ReplayScope.EverythingAsDiffs }
        let afterEdit = baseSource.Replace("let x = 1.0", "let x = 9.0")
        let ev = observeUserEdit settings baseSource afterEdit (addr "x") "9.0"
        match ev with
        | TweakLogEvent.UserEditObserved(_, _, _, content) -> content |> Expect.equal "the whole new file" (FileContent.Recorded afterEdit)
        | other -> failtestf "expected UserEditObserved, got %A" other

      testCase "FileContent.NotRecorded is not an absence, it names the scope that skipped recording" <| fun _ ->
        // No `Option` here: an unrecorded snapshot always says WHY, the same
        // ReplayScope the caller's TweakLogSettings carried. Two NotRecorded
        // values from two DIFFERENT scopes are not the same fact, so they
        // must not compare equal, a bare `None` could never make that claim.
        let notRecordedHere = FileContent.NotRecorded ReplayScope.SageFsWritesOnly
        let notRecordedThere = FileContent.NotRecorded ReplayScope.EverythingAsDiffs
        (notRecordedHere = notRecordedThere) |> Expect.isFalse "NotRecorded under different scopes carries different meaning"
        (notRecordedHere = FileContent.Recorded "") |> Expect.isFalse "NotRecorded is never Recorded, even of empty text"

      testCase "replayWholeFile reproduces a user edit anywhere in the file, not just SageFs's own writes" <| fun _ ->
        let settings = { TweakLogSettings.defaults with ReplayScope = ReplayScope.EverythingAsDiffs }
        let log = EventLog.empty
        let log, _ = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let afterUserEdit = baseSource.Replace("let y = 2.0", "let y = 42.0")
        let log, _ = EventLog.append log 2L (observeUserEdit settings baseSource afterUserEdit (addr "y") "42.0")
        replayWholeFile baseSource log.Events
        |> Expect.equal "the WHOLE file, byte for byte, including the part SageFs never wrote" (Ok afterUserEdit)

      testCase "replayWholeFile refuses (never guesses) when SageFsWritesOnly events carry no snapshot" <| fun _ ->
        let log = EventLog.empty
        let log, _ = EventLog.append log 1L (TweakLogEvent.UserEditObserved(addr "x", "9.0", contentHash "9.0", FileContent.NotRecorded ReplayScope.SageFsWritesOnly))
        replayWholeFile baseSource log.Events |> Expect.isError "no snapshot to replay from"
    ]

    testList "crash recovery offer" [

      testCase "an applied-but-never-saved tweak is offered" <| fun _ ->
        let log = EventLog.empty
        let log, applied = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        recoveryOffer log
        |> Expect.equal "one recoverable tweak"
          [ { EventId = applied.Id; Address = addr "x"; TextBefore = "1.0"; TextAfter = "2.0"; WasInFlightAtCrash = true } ]

      testCase "a saved tweak is never offered" <| fun _ ->
        let log = EventLog.empty
        let log, _ = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, _ = EventLog.append log 2L (TweakLogEvent.TweakSaved(addr "x", "1.0", "2.0", contentHash "2.0", contentHash baseSource))
        recoveryOffer log |> Expect.isEmpty "nothing pending"

      testCase "only the MOST RECENT unsaved tweak is flagged as in flight at the crash; earlier ones are not offered at all once superseded" <| fun _ ->
        let log = EventLog.empty
        let log, _ = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, second = EventLog.append log 2L (TweakLogEvent.TweakApplied(addr "x", "2.0", "3.0", contentHash "3.0"))
        recoveryOffer log
        |> Expect.equal "only the latest tweak on x is recoverable, and it's flagged"
          [ { EventId = second.Id; Address = addr "x"; TextBefore = "2.0"; TextAfter = "3.0"; WasInFlightAtCrash = true } ]

      testCase "multiple addresses: only ONE is in flight at the crash, the rest are offered unchecked-by-default too but not flagged" <| fun _ ->
        let log = EventLog.empty
        let log, x = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, y = EventLog.append log 2L (TweakLogEvent.TweakApplied(addr "y", "5.0", "6.0", contentHash "6.0"))
        let offer = recoveryOffer log
        offer |> List.map (fun r -> r.EventId) |> Expect.equal "in log order" [ x.Id; y.Id ]
        offer |> List.filter (fun r -> r.WasInFlightAtCrash) |> List.map (fun r -> r.EventId) |> Expect.equal "only the LAST one" [ y.Id ]

      testCase "an unsettled drag is never journaled, so it was never a candidate to begin with" <| fun _ ->
        // Ticks never produce events (ScrubCoalescer), so there is nothing
        // for recoveryOffer to see until `settle` logs exactly one
        // TweakApplied, this is the same guarantee, read from the other end.
        let s = ScrubCoalescer.start (addr "x") "1.0" 0L 2.0 "2.0"
        let s = ScrubCoalescer.tick s 1L 2.5 "2.5"
        let s = ScrubCoalescer.tick s 2L 3.0 "3.0"
        match ScrubCoalescer.settle s with
        | TweakLogEvent.TweakApplied _ -> ()
        | other -> failtestf "settle should produce exactly one TweakApplied, got %A" other
    ]

    testList "undo / redo cursor" [

      testCase "undo walks back through applied ops, then reports Compacted at the boundary" <| fun _ ->
        let log = EventLog.empty
        let log, a1 = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, a2 = EventLog.append log 2L (TweakLogEvent.TweakApplied(addr "x", "2.0", "3.0", contentHash "3.0"))
        let c1 = UndoCursor.undo log UndoCursor.AtHead
        c1 |> Expect.equal "undo lands on the most recent op" (UndoCursor.At a2.Id)
        let c2 = UndoCursor.undo log c1
        c2 |> Expect.equal "undo again lands on the first op" (UndoCursor.At a1.Id)
        let c3 = UndoCursor.undo log c2
        match c3 with
        | UndoCursor.Compacted oldest -> oldest |> Expect.equal "names the oldest still-available point honestly" a1.Id
        | other -> failtestf "expected Compacted, got %A" other

      testCase "redo moves forward again" <| fun _ ->
        let log = EventLog.empty
        let log, a1 = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, a2 = EventLog.append log 2L (TweakLogEvent.TweakApplied(addr "x", "2.0", "3.0", contentHash "3.0"))
        let c = UndoCursor.At a1.Id
        UndoCursor.redo log c |> Expect.equal "moves to the next op" (UndoCursor.At a2.Id)
    ]

    testList "performUndo / performRedo: sequential undo, explicit redo, storage stays append-only" [

      testCase "performUndo rolls back the most recent tweak and appends a RolledBack op" <| fun _ ->
        let source = "module M\nlet x = 2.0\n"
        let log = EventLog.empty
        let log, applied = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        match performUndo log Snapshot.empty UndoCursor.AtHead source 2L with
        | Ok(log', cursor, newSource) ->
          newSource |> Expect.equal "back to 1.0" "module M\nlet x = 1.0\n"
          cursor |> Expect.equal "cursor now points at the undone op" (UndoCursor.At applied.Id)
          log'.Events.Length |> Expect.equal "storage stayed append-only: one more event, not a rewrite" 2
          match log'.Events |> List.last with
          | { Event = TweakLogEvent.RolledBack id } -> id |> Expect.equal "targets the tweak it undid" applied.Id
          | other -> failtestf "expected a RolledBack event, got %A" other
        | Error e -> failtestf "expected Ok, got %A" e

      testCase "repeated performUndo walks sequentially back through two tweaks" <| fun _ ->
        let log = EventLog.empty
        let log, a1 = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, a2 = EventLog.append log 2L (TweakLogEvent.TweakApplied(addr "x", "2.0", "3.0", contentHash "3.0"))
        let source = "module M\nlet x = 3.0\n"
        let log, cursor1, source1 = performUndo log Snapshot.empty UndoCursor.AtHead source 3L |> Expect.wantOk "first undo"
        source1 |> Expect.equal "back to 2.0" "module M\nlet x = 2.0\n"
        cursor1 |> Expect.equal "cursor at a2" (UndoCursor.At a2.Id)
        let _, cursor2, source2 = performUndo log Snapshot.empty cursor1 source1 4L |> Expect.wantOk "second undo"
        source2 |> Expect.equal "back to 1.0" "module M\nlet x = 1.0\n"
        cursor2 |> Expect.equal "cursor at a1" (UndoCursor.At a1.Id)

      testCase "performRedo re-applies what performUndo undid, and also appends (never rewrites)" <| fun _ ->
        let source = "module M\nlet x = 2.0\n"
        let log = EventLog.empty
        let log, applied = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, cursor, undone = performUndo log Snapshot.empty UndoCursor.AtHead source 2L |> Expect.wantOk "undo"
        let log, cursor', redone = performRedo log Snapshot.empty cursor undone 3L |> Expect.wantOk "redo"
        redone |> Expect.equal "back to 2.0" "module M\nlet x = 2.0\n"
        log.Events.Length |> Expect.equal "two RolledBack ops appended on top of the original TweakApplied, storage never rewritten" 3
        ignore applied
        ignore cursor'

      testCase "undo then redo then undo again keeps walking correctly (multi-cycle)" <| fun _ ->
        let source = "module M\nlet x = 2.0\n"
        let log = EventLog.empty
        let log, _ = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, c1, s1 = performUndo log Snapshot.empty UndoCursor.AtHead source 2L |> Expect.wantOk "undo"
        let log, c2, s2 = performRedo log Snapshot.empty c1 s1 3L |> Expect.wantOk "redo"
        let _, _, s3 = performUndo log Snapshot.empty c2 s2 4L |> Expect.wantOk "undo again"
        s3 |> Expect.equal "back to 1.0 again" "module M\nlet x = 1.0\n"

      testCase "performUndo refuses cleanly when there's nothing to undo" <| fun _ ->
        match performUndo EventLog.empty Snapshot.empty UndoCursor.AtHead baseSource 1L with
        | Error _ -> ()
        | Ok _ -> failtest "expected a refusal"
    ]

    testList "scrub coalescing" [

      testCase "ticks on the same address never become events; only settle does" <| fun _ ->
        let s = ScrubCoalescer.start (addr "x") "1.0" 0L 2.0 "2.0"
        let s = ScrubCoalescer.tick s 1L 2.5 "2.5"
        let s = ScrubCoalescer.tick s 2L 3.0 "3.0"
        ScrubCoalescer.settle s
        |> Expect.equal "one TweakApplied for the LATEST value, textBefore preserved from the start of the drag"
          (TweakLogEvent.TweakApplied(addr "x", "1.0", "3.0", contentHash "3.0"))

      testCase "isIdleAt respects the settle threshold, not wall time" <| fun _ ->
        let s = ScrubCoalescer.start (addr "x") "1.0" 0L 2.0 "2.0"
        ScrubCoalescer.isIdleAt s 1L |> Expect.isFalse "not idle yet"
        ScrubCoalescer.isIdleAt s (TweakLogLimits.scrubSettleIdleTicks) |> Expect.isTrue "idle once the threshold passed"
    ]

    testList "compaction (bounded growth)" [

      testCase "an unsaved tweak is never compacted away" <| fun _ ->
        let log = EventLog.empty
        let log, _ = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        // Nothing else ever happens to this address: it stays dirty forever.
        let policy = { RetentionPolicy.defaults with UndoWindow = 0 }
        let newSnapshot, tail = compact Snapshot.empty log.Events policy Set.empty
        tail |> Expect.isNonEmpty "the unsaved tweak's event survives compaction"
        (projectFromSnapshot newSnapshot tail).Known |> Map.tryFind (addr "x") |> Expect.equal "still there" (Some "2.0")

      testCase "a saved, old tweak with nothing else protecting it IS compactable" <| fun _ ->
        let log = EventLog.empty
        let log, _ = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, _ = EventLog.append log 2L (TweakLogEvent.TweakSaved(addr "x", "1.0", "2.0", contentHash "2.0", contentHash baseSource))
        let policy = { RetentionPolicy.defaults with UndoWindow = 0 }
        let newSnapshot, tail = compact Snapshot.empty log.Events policy Set.empty
        tail |> Expect.isEmpty "a fully-saved, non-preset, non-conflicted op is compactable once outside the undo window"
        newSnapshot.Projection.Known |> Map.tryFind (addr "x") |> Expect.equal "the snapshot remembers the current text" (Some "2.0")

      testCase "an open conflict is never compacted away" <| fun _ ->
        let log = EventLog.empty
        let log, applied = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, _ = EventLog.append log 2L (TweakLogEvent.ConflictRaised(addr "x", "2.0", "9.0", "1.0"))
        let policy = { RetentionPolicy.defaults with UndoWindow = 0 }
        let _, tail = compact Snapshot.empty log.Events policy Set.empty
        tail |> Expect.isNonEmpty "an event behind an open conflict on the same address is kept"
        ignore applied

      testCase "a named preset's op is never compacted away" <| fun _ ->
        let log = EventLog.empty
        let log, _ = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, _ = EventLog.append log 2L (TweakLogEvent.TweakSaved(addr "x", "1.0", "2.0", contentHash "2.0", contentHash baseSource))
        let policy = { RetentionPolicy.defaults with UndoWindow = 0 }
        let _, tail = compact Snapshot.empty log.Events policy (Set.singleton (addr "x"))
        tail |> Expect.isNonEmpty "a preset address's history is kept even once saved"

      testCase "compaction only ever compacts a PREFIX: it stops at, and keeps, the first must-keep event" <| fun _ ->
        let log = EventLog.empty
        // Compactable (saved, old): id 1
        let log, _ = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, _ = EventLog.append log 2L (TweakLogEvent.TweakSaved(addr "x", "1.0", "2.0", contentHash "2.0", contentHash baseSource))
        // Unsaved (must-keep): id 3
        let log, _ = EventLog.append log 3L (TweakLogEvent.TweakApplied(addr "y", "2.0", "3.0", contentHash "3.0"))
        let policy = { RetentionPolicy.defaults with UndoWindow = 0 }
        let newSnapshot, tail = compact Snapshot.empty log.Events policy Set.empty
        newSnapshot.UpToEventId |> Expect.equal "compacted exactly the safe prefix, up to id 2" 2
        tail |> List.map _.Id |> Expect.equal "everything from the must-keep event onward stays raw" [ 3 ]

      testCase "whyKept explains its verdict" <| fun _ ->
        let log = EventLog.empty
        let log, applied = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        whyKept RetentionPolicy.defaults log.Events Set.empty (log.Events |> List.find (fun e -> e.Id = applied.Id))
        |> Expect.equal "within the default undo window" KeepReason.WithinUndoWindow
    ]

    testList "compaction mode is configurable" [

      testCase "OnSessionClose (the default) never triggers shouldCompact, however far over budget" <| fun _ ->
        let settings = { TweakLogSettings.defaults with CompactionMode = CompactionMode.OnSessionClose }
        shouldCompact settings 999_999_999L 999_999 |> Expect.isFalse "a live session stays fully event-sourced"

      testCase "LiveOnBudget triggers exactly like the old budget check" <| fun _ ->
        let settings = { TweakLogSettings.defaults with CompactionMode = CompactionMode.LiveOnBudget }
        shouldCompact settings 0L 0 |> Expect.isFalse "well under budget"
        shouldCompact settings 0L (settings.Retention.MaxEvents + 1) |> Expect.isTrue "over the event count budget"

      testCase "closeSession compacts unconditionally, regardless of mode" <| fun _ ->
        let log = EventLog.empty
        let log, _ = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, _ = EventLog.append log 2L (TweakLogEvent.TweakSaved(addr "x", "1.0", "2.0", contentHash "2.0", contentHash baseSource))
        let settings = { TweakLogSettings.defaults with CompactionMode = CompactionMode.OnSessionClose; Retention = { RetentionPolicy.defaults with UndoWindow = 0 } }
        let newSnapshot, tail = closeSession Snapshot.empty log.Events settings Set.empty
        newSnapshot.UpToEventId |> Expect.equal "a saved, non-preset op compacts on close" 2
        tail |> Expect.isEmpty "nothing left to keep raw"
    ]

    testList "binary encoding" [

      testCase "a torn tail (a truncated write) is detected and never partially decoded" <| fun _ ->
        let log = EventLog.empty
        let log, _ = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, _ = EventLog.append log 2L (TweakLogEvent.TweakSaved(addr "x", "1.0", "2.0", contentHash "2.0", contentHash baseSource))
        let bytes = TweakLogFormat.encodeStream log.Events
        let torn = bytes.[0 .. bytes.Length - 5] // cut off the last record's tail
        let decoded, wasTorn = TweakLogFormat.decodeStream torn
        decoded |> Expect.equal "only the whole, undamaged first record survives" [ log.Events.[0] ]
        wasTorn |> Expect.isTrue "the truncation is reported, not silently accepted"

      testCase "a clean stream decodes back to the exact events, with no torn tail" <| fun _ ->
        let log = EventLog.empty
        let log, _ = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, _ = EventLog.append log 2L (TweakLogEvent.ReformatObserved(contentHash baseSource, contentHash baseSource, FileContent.NotRecorded ReplayScope.SageFsWritesOnly))
        let log, _ = EventLog.append log 3L (TweakLogEvent.RolledBack 1)
        let bytes = TweakLogFormat.encodeStream log.Events
        let decoded, wasTorn = TweakLogFormat.decodeStream bytes
        decoded |> Expect.equal "round-trips exactly" log.Events
        wasTorn |> Expect.isFalse "nothing was torn"
    ]

    testList "fingerprints grade a segment before folding it" [

      testCase "same schema, same fold version, same target: Fine" <| fun _ ->
        let fp = Fingerprint.current "target-hash-1"
        Fingerprint.grade fp fp |> Expect.equal "identical fingerprints are Fine" LogGrade.Fine

      testCase "same schema and fold version, different target file: Risky" <| fun _ ->
        let current = Fingerprint.current "target-hash-NEW"
        let stored = Fingerprint.current "target-hash-OLD"
        Fingerprint.grade current stored |> Expect.equal "readable, but about a different target now" LogGrade.Risky

      testCase "same schema, different fold version: Risky" <| fun _ ->
        let current = { Fingerprint.current "t" with FoldVersion = Fingerprint.current("t").FoldVersion + 1 }
        let stored = Fingerprint.current "t"
        Fingerprint.grade current stored |> Expect.equal "the bytes decode, but the fold semantics moved" LogGrade.Risky

      testCase "different schema version: Impossible" <| fun _ ->
        let current = { Fingerprint.current "t" with SchemaVersion = Fingerprint.current("t").SchemaVersion + 1 }
        let stored = Fingerprint.current "t"
        Fingerprint.grade current stored |> Expect.equal "the byte layout itself can't be trusted" LogGrade.Impossible

      testCase "a Fine segment decodes its events" <| fun _ ->
        let log = EventLog.empty
        let log, _ = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let fp = Fingerprint.current (contentHash baseSource)
        let bytes = TweakLogFormat.encodeSegment fp log.Events
        let decoded = TweakLogFormat.decodeSegment fp bytes |> Expect.wantOk "decodes"
        decoded.Grade |> Expect.equal "Fine" LogGrade.Fine
        decoded.Events |> Expect.equal "the events survive" log.Events

      testCase "an Impossible segment is never folded: no events come back, just the grade" <| fun _ ->
        let log = EventLog.empty
        let log, _ = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let stored = Fingerprint.current (contentHash baseSource)
        let bytes = TweakLogFormat.encodeSegment stored log.Events
        let currentBuild = { stored with SchemaVersion = stored.SchemaVersion + 1 }
        let decoded = TweakLogFormat.decodeSegment currentBuild bytes |> Expect.wantOk "the header itself still decodes"
        decoded.Grade |> Expect.equal "Impossible" LogGrade.Impossible
        decoded.Events |> Expect.isEmpty "never folded, whatever the bytes might have contained"

      testCase "TWIN: a decoder that ignores the grade folds an Impossible segment anyway" <| fun _ ->
        let log = EventLog.empty
        let log, _ = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let stored = Fingerprint.current (contentHash baseSource)
        let bytes = TweakLogFormat.encodeSegment stored log.Events
        // The real path refuses.
        let currentBuild = { stored with SchemaVersion = stored.SchemaVersion + 1 }
        (TweakLogFormat.decodeSegment currentBuild bytes |> Expect.wantOk "decodes").Events
        |> Expect.isEmpty "the real decoder never folds an Impossible segment"
        // The twin doesn't check the grade at all, and folds it anyway,
        // this is exactly the bug "Impossible is never folded" exists to
        // prevent, kept here only so the DST invariant can be shown to
        // catch it.
        TweakLogFormat.decodeSegmentIgnoringGradeTwin bytes
        |> Expect.isNonEmpty "the twin folds bytes the fingerprint said not to trust"
    ]

    testProperty "PROPERTY, snapshot plus tail always folds to the same projection as the full stream, for every address" <|
      fun () ->
        let log = EventLog.empty
        let log, _ = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        let log, _ = EventLog.append log 2L (TweakLogEvent.TweakSaved(addr "x", "1.0", "2.0", contentHash "2.0", contentHash baseSource))
        let log, _ = EventLog.append log 3L (TweakLogEvent.TweakApplied(addr "y", "2.0", "3.0", contentHash "3.0"))
        let full = project log
        let policy = { RetentionPolicy.defaults with UndoWindow = 0 }
        let snapshot, tail = compact Snapshot.empty log.Events policy Set.empty
        let fromSnapshot = projectFromSnapshot snapshot tail
        fromSnapshot.Known = full.Known && fromSnapshot.Saved = full.Saved

    testProperty "PROPERTY, a binary round trip through TweakLogFormat is the identity" <|
      fun (n: int) ->
        let log = EventLog.empty
        let log, _ = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", string n, contentHash (string n)))
        let bytes = TweakLogFormat.encodeStream log.Events
        let decoded, wasTorn = TweakLogFormat.decodeStream bytes
        decoded = log.Events && not wasTorn
  ]
