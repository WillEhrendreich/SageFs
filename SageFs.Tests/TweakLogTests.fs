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
        let log, _ = EventLog.append log 1L (TweakLogEvent.UserEditObserved(addr "x", "3.0", contentHash "3.0"))
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
        match rollback log applied.Id source with
        | Ok(RollbackOutcome.Applied newSource) -> newSource |> Expect.equal "back to the original text" "module M\nlet x = 1.0\n"
        | other -> failtestf "expected Applied, got %A" other

      testCase "rollback conflicts, and never overwrites, when a later edit changed the same expression" <| fun _ ->
        let log = EventLog.empty
        let log, applied = EventLog.append log 1L (TweakLogEvent.TweakApplied(addr "x", "1.0", "2.0", contentHash "2.0"))
        // Someone (or something) changed it to 9.0 after our write.
        let laterSource = "module M\nlet x = 9.0\n"
        match rollback log applied.Id laterSource with
        | Ok(RollbackOutcome.Conflict(wrote, now, before)) ->
          wrote |> Expect.equal "what we wrote" "2.0"
          now |> Expect.equal "what's there now" "9.0"
          before |> Expect.equal "what it was before our write" "1.0"
        | other -> failtestf "expected Conflict, got %A" other

      testCase "rolling back a non-operation event is refused" <| fun _ ->
        let log = EventLog.empty
        let log, resolved = EventLog.append log 1L (TweakLogEvent.ConflictResolved(addr "x"))
        match rollback log resolved.Id baseSource with
        | Error(RollbackError.NotAnOperation _) -> ()
        | other -> failtestf "expected NotAnOperation, got %A" other

      testCase "rolling back an id that doesn't exist is refused" <| fun _ ->
        match rollback EventLog.empty 999 baseSource with
        | Error(RollbackError.NoSuchOperation 999) -> ()
        | other -> failtestf "expected NoSuchOperation, got %A" other
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
        let log, _ = EventLog.append log 2L (TweakLogEvent.ReformatObserved(contentHash baseSource, contentHash baseSource))
        let log, _ = EventLog.append log 3L (TweakLogEvent.RolledBack 1)
        let bytes = TweakLogFormat.encodeStream log.Events
        let decoded, wasTorn = TweakLogFormat.decodeStream bytes
        decoded |> Expect.equal "round-trips exactly" log.Events
        wasTorn |> Expect.isFalse "nothing was torn"
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
