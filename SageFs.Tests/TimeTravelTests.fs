module SageFs.Tests.TimeTravelTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Measures
open SageFs.TimeTravel

// ── Test model ──

type TModel = { Value: int; Label: string }

let mkModel v = { Value = v; Label = sprintf "v%d" v }

let mkState cap =
  create { Capacity = cap; Enabled = true }

let rec' label ms v (st: TimeTravelState<TModel>) =
  record label (floatMs ms) (mkModel v) st

// ── Tests ──

[<Tests>]
let timeTravelTests =
  testList "TimeTravel" [

    testList "create" [
      test "starts in live mode with empty ring" {
        let st: TimeTravelState<TModel> = mkState 50
        st |> isLive |> Expect.isTrue "should be live"
        st |> viewingAge |> Expect.equal "no viewing age" None
        st |> snapshotCount |> Expect.equal "empty" 0
      }
    ]

    testList "record" [
      test "records snapshots in live mode" {
        let st = mkState 10 |> rec' "A" 1.0 1 |> rec' "B" 2.0 2
        st |> snapshotCount |> Expect.equal "two snapshots" 2
        st |> isLive |> Expect.isTrue "still live"
      }

      test "does not record while viewing" {
        let st =
          mkState 10
          |> rec' "A" 1.0 1
          |> rec' "B" 2.0 2
          |> stepBack
          |> rec' "C" 3.0 3
        st |> snapshotCount |> Expect.equal "still 2" 2
      }

      test "respects capacity" {
        let st =
          mkState 3
          |> rec' "A" 1.0 1
          |> rec' "B" 2.0 2
          |> rec' "C" 3.0 3
          |> rec' "D" 4.0 4
        st |> snapshotCount |> Expect.equal "capped at 3" 3
      }
    ]

    testList "navigation" [
      test "stepBack from live goes to age 1" {
        let st = mkState 10 |> rec' "A" 1.0 1 |> rec' "B" 2.0 2 |> stepBack
        st |> isLive |> Expect.isFalse "should be viewing"
        st |> viewingAge |> Expect.equal "age 1" (Some 1)
      }

      test "stepBack increments age" {
        let st =
          mkState 10
          |> rec' "A" 1.0 1
          |> rec' "B" 2.0 2
          |> rec' "C" 3.0 3
          |> stepBack
          |> stepBack
        st |> viewingAge |> Expect.equal "age 2" (Some 2)
      }

      test "stepBack does nothing with no history" {
        let st = mkState 10 |> stepBack
        st |> isLive |> Expect.isTrue "still live"
      }

      test "stepBack does nothing with only one snapshot" {
        let st = mkState 10 |> rec' "A" 1.0 1 |> stepBack
        st |> isLive |> Expect.isTrue "still live — need 2+ for back"
      }

      test "stepBack stops at oldest snapshot" {
        let st =
          mkState 10
          |> rec' "A" 1.0 1
          |> rec' "B" 2.0 2
          |> rec' "C" 3.0 3
          |> stepBack |> stepBack |> stepBack
        st |> viewingAge |> Expect.equal "clamped at 2" (Some 2)
      }

      test "stepForward decrements age" {
        let st =
          mkState 10
          |> rec' "A" 1.0 1
          |> rec' "B" 2.0 2
          |> rec' "C" 3.0 3
          |> stepBack |> stepBack |> stepForward
        st |> viewingAge |> Expect.equal "age 1" (Some 1)
      }

      test "stepForward to age 0 returns to live" {
        let st =
          mkState 10
          |> rec' "A" 1.0 1
          |> rec' "B" 2.0 2
          |> stepBack |> stepForward
        st |> isLive |> Expect.isTrue "back to live"
      }

      test "stepForward from live is no-op" {
        let st = mkState 10 |> rec' "A" 1.0 1 |> stepForward
        st |> isLive |> Expect.isTrue "still live"
      }

      test "goLive returns to live from any age" {
        let st =
          mkState 10
          |> rec' "A" 1.0 1
          |> rec' "B" 2.0 2
          |> rec' "C" 3.0 3
          |> stepBack |> stepBack |> goLive
        st |> isLive |> Expect.isTrue "back to live"
        st |> viewingAge |> Expect.equal "no age" None
      }

      test "goLive from live is no-op" {
        let st = mkState 10 |> rec' "A" 1.0 1 |> goLive
        st |> isLive |> Expect.isTrue "still live"
      }
    ]

    testList "currentModel" [
      test "returns latest model in live mode" {
        let st = mkState 10 |> rec' "A" 1.0 1 |> rec' "B" 2.0 2
        let m = st |> currentModel |> Option.get
        m.Value |> Expect.equal "latest" 2
      }

      test "returns historical model when viewing" {
        let st =
          mkState 10
          |> rec' "A" 1.0 10
          |> rec' "B" 2.0 20
          |> rec' "C" 3.0 30
          |> stepBack |> stepBack
        let m = st |> currentModel |> Option.get
        m.Value |> Expect.equal "age 2 model" 10
      }

      test "returns None for empty ring" {
        let st: TimeTravelState<TModel> = mkState 10
        st |> currentModel |> Expect.equal "empty" None
      }
    ]

    testList "formatStatus" [
      test "empty ring returns None" {
        let st: TimeTravelState<TModel> = mkState 10
        st |> formatStatus |> Expect.equal "no status" None
      }

      test "live mode shows snapshot count" {
        let st = mkState 50 |> rec' "A" 1.0 1 |> rec' "B" 2.0 2
        let s = st |> formatStatus |> Option.get
        s |> Expect.stringContains "has count" "2"
        s |> Expect.stringContains "has icon" "⏱"
      }

      test "viewing mode shows age" {
        let st =
          mkState 10
          |> rec' "A" 1.0 1
          |> rec' "B" 2.0 2
          |> stepBack
        let s = st |> formatStatus |> Option.get
        s |> Expect.stringContains "has rewind icon" "⏮"
        s |> Expect.stringContains "has age" "-1"
      }
    ]

    testList "enable/disable" [
      test "disabled state does not record" {
        let st =
          mkState 10
          |> setEnabled false
          |> rec' "A" 1.0 1
        st |> snapshotCount |> Expect.equal "empty" 0
      }

      test "re-enabling resumes recording" {
        let st =
          mkState 10
          |> setEnabled false
          |> rec' "Ignored" 1.0 1
          |> setEnabled true
          |> rec' "Visible" 2.0 2
        st |> snapshotCount |> Expect.equal "one" 1
      }
    ]

    testList "recentLabels" [
      test "returns formatted recent labels" {
        let st =
          mkState 10
          |> rec' "Alpha" 1.0 1
          |> rec' "Beta" 2.5 2
        let labels = st |> recentLabels 5
        labels |> Expect.hasLength "two labels" 2
        labels.[0] |> Expect.stringContains "has Beta" "Beta"
      }
    ]

    testList "property tests" [
      testProperty "goLive always returns to live" (fun (steps: int list) ->
        let st =
          mkState 10
          |> rec' "A" 1.0 1
          |> rec' "B" 2.0 2
          |> rec' "C" 3.0 3
        let navigated =
          steps |> List.fold (fun s step ->
            match step % 3 with
            | 0 -> stepBack s
            | 1 -> stepForward s
            | _ -> s
          ) st
        navigated |> goLive |> isLive
      )

      testProperty "viewingAge is always None when live" (fun (n: PositiveInt) ->
        let st =
          [ 1 .. min n.Get 20 ]
          |> List.fold (fun s i -> rec' (sprintf "M%d" i) 1.0 i s) (mkState 50)
        isLive st && viewingAge st = None
      )

      testProperty "stepBack then stepForward is identity at boundary" (fun (n: PositiveInt) ->
        let count = min n.Get 10
        let st =
          [ 1 .. count ]
          |> List.fold (fun s i -> rec' (sprintf "M%d" i) 1.0 i s) (mkState 50)
        match count > 1 with
        | true ->
          let back = stepBack st
          let forth = stepForward back
          isLive forth
        | false -> true
      )

      testProperty "snapshotCount never exceeds capacity" (fun (cap: PositiveInt) (items: int list) ->
        let c = min cap.Get 20
        let st =
          items
          |> List.fold (fun s i -> rec' (sprintf "M%d" i) 1.0 i s) (mkState c)
        snapshotCount st <= c
      )

      testProperty "currentModel in live mode equals latest recorded" (fun (n: PositiveInt) ->
        let count = min n.Get 20
        let st =
          [ 1 .. count ]
          |> List.fold (fun s i -> rec' (sprintf "M%d" i) 1.0 i s) (mkState 50)
        match currentModel st with
        | Some m -> m.Value = count
        | None -> count = 0
      )

      testProperty "navigation never panics" (fun (ops: int list) ->
        let st =
          mkState 5
          |> rec' "A" 1.0 1
          |> rec' "B" 2.0 2
          |> rec' "C" 3.0 3
        let final =
          ops |> List.fold (fun s op ->
            match op % 4 with
            | 0 -> stepBack s
            | 1 -> stepForward s
            | 2 -> goLive s
            | _ -> rec' "X" 1.0 99 s
          ) st
        // Should never throw — just verify it completes
        snapshotCount final >= 0
      )
    ]

    testList "key bindings" [
      test "Alt+Left maps to TimeTravelBack" {
        let combo = KeyCombo.alt System.ConsoleKey.LeftArrow
        KeyMap.defaults |> Map.tryFind combo
        |> Expect.equal "Alt+Left → TimeTravelBack" (Some UiAction.TimeTravelBack)
      }
      test "Alt+Right maps to TimeTravelForward" {
        let combo = KeyCombo.alt System.ConsoleKey.RightArrow
        KeyMap.defaults |> Map.tryFind combo
        |> Expect.equal "Alt+Right → TimeTravelForward" (Some UiAction.TimeTravelForward)
      }
      test "Alt+Home maps to TimeTravelGoLive" {
        let combo = KeyCombo.alt System.ConsoleKey.Home
        KeyMap.defaults |> Map.tryFind combo
        |> Expect.equal "Alt+Home → TimeTravelGoLive" (Some UiAction.TimeTravelGoLive)
      }
    ]

    testList "UiAction parse" [
      test "TimeTravelBack parses" {
        UiAction.tryParse "TimeTravelBack"
        |> Expect.isSome "should parse TimeTravelBack"
      }
      test "TimeTravelForward parses" {
        UiAction.tryParse "TimeTravelForward"
        |> Expect.isSome "should parse TimeTravelForward"
      }
      test "TimeTravelGoLive parses" {
        UiAction.tryParse "TimeTravelGoLive"
        |> Expect.isSome "should parse TimeTravelGoLive"
      }
    ]

    testList "TerminalInput mapping" [
      test "Alt+Left → TerminalCommand.TimeTravelBack" {
        let ki = System.ConsoleKeyInfo('\000', System.ConsoleKey.LeftArrow, false, true, false)
        TerminalInput.mapKeyWith KeyMap.defaults ki
        |> Expect.equal "maps to TimeTravelBack" (Some TerminalCommand.TimeTravelBack)
      }
      test "Alt+Right → TerminalCommand.TimeTravelForward" {
        let ki = System.ConsoleKeyInfo('\000', System.ConsoleKey.RightArrow, false, true, false)
        TerminalInput.mapKeyWith KeyMap.defaults ki
        |> Expect.equal "maps to TimeTravelForward" (Some TerminalCommand.TimeTravelForward)
      }
      test "Alt+Home → TerminalCommand.TimeTravelGoLive" {
        let ki = System.ConsoleKeyInfo('\000', System.ConsoleKey.Home, false, true, false)
        TerminalInput.mapKeyWith KeyMap.defaults ki
        |> Expect.equal "maps to TimeTravelGoLive" (Some TerminalCommand.TimeTravelGoLive)
      }
    ]

    testList "RenderRegion time-travel" [
      test "record and navigate RenderRegion snapshots" {
        let mkRegion i : RenderRegion list =
          [{ Id = "output"; Flags = RegionFlags.None; Content = sprintf "frame %d" i
             Affordances = []; Cursor = None; Completions = None
             LineAnnotations = [||] }]
        let st =
          create { ModelSnapshot.Capacity = 10; ModelSnapshot.Enabled = true }
          |> record "ev1" 0.0<ms> (mkRegion 1)
          |> record "ev2" 0.0<ms> (mkRegion 2)
          |> record "ev3" 0.0<ms> (mkRegion 3)
        match currentModel st with
        | Some regions ->
          regions |> List.head |> fun r -> r.Content
          |> Expect.equal "should be frame 3" "frame 3"
        | None -> failtest "expected Some"
        let back = stepBack st
        match currentModel back with
        | Some regions ->
          regions |> List.head |> fun r -> r.Content
          |> Expect.equal "should be frame 2" "frame 2"
        | None -> failtest "expected Some"
      }

      test "formatStatus shows navigation hint when viewing" {
        let st =
          create { ModelSnapshot.Capacity = 10; ModelSnapshot.Enabled = true }
          |> record "ev1" 0.0<ms> "a"
          |> record "ev2" 0.0<ms> "b"
          |> stepBack
        match formatStatus st with
        | Some s ->
          s |> Expect.stringContains "should contain age" "⏮"
          s |> Expect.stringContains "should contain nav hint" "Alt+→"
        | None -> failtest "expected Some"
      }

      test "formatStatus shows snapshot count when live" {
        let st =
          create { ModelSnapshot.Capacity = 10; ModelSnapshot.Enabled = true }
          |> record "ev1" 0.0<ms> "a"
          |> record "ev2" 0.0<ms> "b"
        match formatStatus st with
        | Some s ->
          s |> Expect.stringContains "should contain count" "2 snapshots"
        | None -> failtest "expected Some"
      }
    ]
  ]
