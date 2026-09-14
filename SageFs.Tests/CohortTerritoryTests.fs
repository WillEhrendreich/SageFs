/// Phase 2 item 16 of sagefs-multiagent-vision.md (§6.5 "Territory map"):
/// pure unit tests for `CohortTerritory` — no daemon, no FSI, no I/O, driven
/// entirely through `Cohort.decide`/`project` to build sample frames, the
/// same "pure core, no IO" discipline `CohortPanelTests.fs`/`CohortOwnerTests.fs`
/// use.
module SageFs.Tests.CohortTerritoryTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.Features
open SageFs.Features.CohortTerritory
open SageFs.Features.Treemap

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private alice = "alice"
let private bob = "bob"

/// Folds `decide` over a fixed command list from an empty state, then
/// projects the resulting ledger head into a `CohortFrame` exactly as
/// `CohortOwner.frameOf` does — mirrors `CohortPanelTests.fs`'s `frameAfter`.
let private frameAfter (commands: CohortCommand<string> list) : CohortFrame<string> =
  let finalState, seq =
    commands
    |> List.fold
      (fun (state, seq) cmd ->
        match decide epoch [| byte seq |] state cmd with
        | Ok(newState, _, _) -> newState, seq + 1L<ledgerSeq>
        | Error err -> failwithf "unexpected refusal building test frame: %A" err)
      (CohortState.empty (), 0L<ledgerSeq>)
  project { Seq = (if seq = 0L<ledgerSeq> then 0L<ledgerSeq> else seq - 1L<ledgerSeq>); State = finalState } [||]

let private emptyFrame : CohortFrame<string> = project (replayHead []) [||]

let private propConfig = { FsCheckConfig.defaultConfig with maxTest = 200 }

[<Tests>]
let cohortTerritoryTests =
  testList "CohortTerritory" [

    testList "ofFrame" [
      testCase "an empty frame projects no tiles" <| fun _ ->
        ofFrame emptyFrame |> Expect.equal "nothing claimed, nothing to project" []

      testCase "a Held claim projects one tile carrying its holder's member index" <| fun _ ->
        let frame =
          frameAfter
            [ CohortCommand.Join(alice, JoinableRole.Implementer, None)
              CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing") ]
        let tiles = ofFrame frame
        tiles |> List.length |> Expect.equal "one held claim, one tile" 1
        let tile = tiles.[0]
        tile.Label |> Expect.equal "label is the claimed path" "src/Foo.fs"
        tile.HolderIndex |> Expect.equal "holder index points at alice's row" (Array.findIndex ((=) alice) frame.MemberIds)
        (tile.HolderIndex >= 0) |> Expect.isTrue "a Held claim always has a live holder index"

      testCase "an Orphaned claim projects with HolderIndex = -1 (neutral, not dropped)" <| fun _ ->
        let frame =
          frameAfter
            [ CohortCommand.Join(alice, JoinableRole.Implementer, None)
              CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing")
              CohortCommand.Depart alice ]
        let tiles = ofFrame frame
        tiles |> List.length |> Expect.equal "the orphaned claim is still territory" 1
        tiles.[0].HolderIndex |> Expect.equal "no live holder -> neutral index" -1

      testCase "a Released claim is dropped — it is no longer anyone's territory" <| fun _ ->
        let frame =
          frameAfter
            [ CohortCommand.Join(alice, JoinableRole.Implementer, None)
              CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing") ]
        // Release it using the fence AcquireClaim actually produced.
        let claimId = frame.ClaimIds.[0]
        let fence = frame.ClaimFence.[0]
        let released =
          frameAfter
            [ CohortCommand.Join(alice, JoinableRole.Implementer, None)
              CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing")
              CohortCommand.ReleaseClaim(alice, claimId, fence) ]
        ofFrame released |> Expect.equal "released claims are not territory" []

      testCase "multiple members' held claims each keep their own holder index" <| fun _ ->
        let frame =
          frameAfter
            [ CohortCommand.Join(alice, JoinableRole.Implementer, None)
              CohortCommand.Join(bob, JoinableRole.Verifier, None)
              CohortCommand.AcquireClaim(alice, ClaimScope.File "src/A.fs", "a")
              CohortCommand.AcquireClaim(bob, ClaimScope.File "src/B.fs", "b") ]
        let tiles = ofFrame frame |> List.sortBy (fun t -> t.Label)
        let aliceIdx = Array.findIndex ((=) alice) frame.MemberIds
        let bobIdx = Array.findIndex ((=) bob) frame.MemberIds
        tiles.[0].HolderIndex |> Expect.equal "A.fs held by alice" aliceIdx
        tiles.[1].HolderIndex |> Expect.equal "B.fs held by bob" bobIdx
        (aliceIdx = bobIdx) |> Expect.isFalse "two distinct members never share a member index"
    ]

    testList "layout" [
      testCase "an empty tile list produces no rectangles" <| fun _ ->
        layout { X = 0.0; Y = 0.0; W = 100.0; H = 100.0 } [] |> Expect.equal "no tiles, no rects" []

      testCase "every tile appears exactly once, uniformly weighted (equal areas)" <| fun _ ->
        let tiles =
          [ { ClaimId = ClaimId "c1"; Scope = ClaimScope.File "a"; Label = "a"; HolderIndex = 0 }
            { ClaimId = ClaimId "c2"; Scope = ClaimScope.File "b"; Label = "b"; HolderIndex = 1 }
            { ClaimId = ClaimId "c3"; Scope = ClaimScope.File "c"; Label = "c"; HolderIndex = -1 } ]
        let rects = layout { X = 0.0; Y = 0.0; W = 300.0; H = 100.0 } tiles
        rects |> List.length |> Expect.equal "one rect per tile" 3
        let areas = rects |> List.map (fun (_, r) -> Rect.area r)
        areas |> List.iter (fun a -> a |> Expect.floatClose "uniform weight -> equal area" (Accuracy.medium) areas.[0])

      testPropertyWithConfig propConfig "tiles fully cover the bounds area, whatever the tile count" <|
        fun (n: PositiveInt) ->
          let (PositiveInt count) = n
          let count = min count 12
          let tiles =
            [ for i in 0 .. count - 1 ->
                { ClaimId = ClaimId(sprintf "c%d" i); Scope = ClaimScope.File(sprintf "f%d" i); Label = sprintf "f%d" i; HolderIndex = i % 3 } ]
          let bounds = { X = 0.0; Y = 0.0; W = 400.0; H = 300.0 }
          let rects = layout bounds tiles
          let totalArea = rects |> List.sumBy (fun (_, r) -> Rect.area r)
          abs (totalArea - Rect.area bounds) < 0.5
    ]

    testList "colorForHolder" [
      testCase "a negative holder index is always the neutral color" <| fun _ ->
        colorForHolder -1 |> Expect.equal "neutral" neutralColor

      testCase "member index 0 and member index (palette.Length) get the same color (wraps, never crashes)" <| fun _ ->
        colorForHolder 0 |> Expect.equal "palette wraps deterministically" (colorForHolder palette.Length)

      testCase "distinct small indices get distinct colors from the stable palette" <| fun _ ->
        colorForHolder 0 |> Expect.notEqual "index 0 differs from index 1" (colorForHolder 1)
    ]

    testList "toSvg" [
      testCase "an empty tile list still renders a valid, non-empty <svg> root" <| fun _ ->
        let svg = toSvg 300.0 200.0 []
        svg |> Expect.stringStarts "root element" "<svg "
        (svg.EndsWith "</svg>") |> Expect.isTrue "svg is closed"

      testCase "a held claim's label appears in the svg, XML-escaped" <| fun _ ->
        let tiles = [ { ClaimId = ClaimId "c1"; Scope = ClaimScope.File "src/Foo.fs"; Label = "src/Foo.fs"; HolderIndex = 0 } ]
        let svg = toSvg 300.0 200.0 tiles
        svg |> Expect.stringContains "the claimed path is embedded as a tooltip title" "src/Foo.fs"

      testCase "a held tile is filled with its holder's palette color, not the neutral color" <| fun _ ->
        let tiles = [ { ClaimId = ClaimId "c1"; Scope = ClaimScope.File "a"; Label = "a"; HolderIndex = 0 } ]
        let svg = toSvg 300.0 200.0 tiles
        svg |> Expect.stringContains "uses the palette color for holder 0" (sprintf "fill=\"%s\"" (colorForHolder 0))
        (svg.Contains(sprintf "fill=\"%s\"" neutralColor)) |> Expect.isFalse "a held tile is never neutral-colored"

      testCase "an orphaned (unclaimed) tile is filled with the neutral color and dashed" <| fun _ ->
        let tiles = [ { ClaimId = ClaimId "c1"; Scope = ClaimScope.File "a"; Label = "a"; HolderIndex = -1 } ]
        let svg = toSvg 300.0 200.0 tiles
        svg |> Expect.stringContains "orphaned tile is neutral-colored" (sprintf "fill=\"%s\"" neutralColor)
        svg |> Expect.stringContains "orphaned tile has a dashed stroke" "stroke-dasharray"

      testCase "determinism: the same tiles render byte-identical svg twice" <| fun _ ->
        let tiles =
          [ { ClaimId = ClaimId "c1"; Scope = ClaimScope.File "a"; Label = "a"; HolderIndex = 0 }
            { ClaimId = ClaimId "c2"; Scope = ClaimScope.Project "proj"; Label = "proj"; HolderIndex = -1 } ]
        toSvg 400.0 300.0 tiles |> Expect.equal "two calls, same string" (toSvg 400.0 300.0 tiles)

      testCase "WHY — a frame's tiles render end to end through ofFrame -> toSvg without throwing" <| fun _ ->
        let frame =
          frameAfter
            [ CohortCommand.Join(alice, JoinableRole.Implementer, None)
              CohortCommand.Join(bob, JoinableRole.Verifier, None)
              CohortCommand.AcquireClaim(alice, ClaimScope.File "src/A.fs", "a")
              CohortCommand.AcquireClaim(bob, ClaimScope.Project "SageFs.Tests/SageFs.Tests.fsproj", "b") ]
        let svg = ofFrame frame |> toSvg 400.0 240.0
        svg |> Expect.stringContains "carries alice's claimed file" "src/A.fs"
        svg |> Expect.stringContains "carries bob's claimed project" "SageFs.Tests/SageFs.Tests.fsproj"
    ]

    // `toSvg` is a pinned `Text.raw` trusted sink in DashboardEscapingTests.fs —
    // a claim's `Label` is a caller-supplied path (`AcquireClaim`'s scope), so
    // it is runtime, potentially-hostile input reaching live SVG markup. This
    // is the behavioral proof that sink's "Why" cites: the same hostile-payload
    // classes `DashboardEscapingTests.fs` uses for every other dashboard sink,
    // run through `toSvg` end to end.
    testList "toSvg — inert against hostile claim labels (the DashboardEscapingTests.fs trusted-sink proof)" [
      let payloads = [
        "\"><script>alert(1)</script>"
        "<img src=x onerror=alert(1)>"
        "x\" onmouseover=\"alert(1)"
        "' onfocus='alert(1)"
        "&amp;<b>bold</b>&"
      ]
      for payload in payloads do
        testCase (sprintf "hostile label %s never survives as live markup" payload) <| fun _ ->
          let tiles = [ { ClaimId = ClaimId "c1"; Scope = ClaimScope.File payload; Label = payload; HolderIndex = 0 } ]
          let svg = toSvg 300.0 200.0 tiles
          svg.Contains("<script", StringComparison.OrdinalIgnoreCase)
          |> Expect.isFalse "no live <script> element"
          svg.Contains("<img", StringComparison.OrdinalIgnoreCase)
          |> Expect.isFalse "no live <img> element"
          svg.Contains("<b>", StringComparison.Ordinal) |> Expect.isFalse "no injected <b> markup"
          svg.Contains("\" onmouseover=\"", StringComparison.Ordinal) |> Expect.isFalse "no double-quoted attribute breakout"
          svg.Contains("' onfocus='", StringComparison.Ordinal) |> Expect.isFalse "no single-quoted attribute breakout"
    ]
  ]
