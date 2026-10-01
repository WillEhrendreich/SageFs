/// How much of a value the live-bindings pane may run to show it, as a per-session setting. The choice is a closed
/// union a config.fsx can write (`ValueWalk = WalkEverything`), it maps to the walk's own mode in one place, and the
/// host decides what a click on a "not evaluated" row means from that mode alone.
module SageFs.Tests.ValueWalkSettingTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.FsiHost.FsiProtocol

[<Tests>]
let valueWalkTests =
  testList "ValueWalk setting" [
    testCase "a config that says nothing walks in Safe mode" <| fun _ ->
      DirectoryConfigDefaults.empty.ValueWalk
      |> Expect.equal "the default is the mode that runs nothing of the user's" WalkSafe

    testCase "every choice maps to its own walk mode and back" <| fun _ ->
      ValueWalk.all
      |> List.iter (fun choice ->
        choice
        |> ValueWalk.toWalkMode
        |> ValueWalk.ofWalkMode
        |> Expect.equal (sprintf "%A survives the round trip" choice) choice)

    testCase "the choices cover every walk mode, one each" <| fun _ ->
      ValueWalk.all
      |> List.map ValueWalk.toWalkMode
      |> List.sortBy string
      |> Expect.equal
        "the three modes, no mode missing and none twice"
        ([ LiveValueTree.WalkMode.Safe; LiveValueTree.WalkMode.Everything; LiveValueTree.WalkMode.Off ] |> List.sortBy string)

    testCase "every choice has its own name and a consequence the pane can show" <| fun _ ->
      let names = ValueWalk.all |> List.map ValueWalk.name
      names |> List.distinct |> Expect.equal "names are unique" names
      ValueWalk.all
      |> List.iter (fun choice ->
        ValueWalk.consequence choice
        |> Expect.isNotEmpty (sprintf "%s says what it does" (ValueWalk.name choice)))

    testCase "the Everything consequence says that it runs the user's getters" <| fun _ ->
      ValueWalk.consequence WalkEverything
      |> Expect.stringContains "a user turning it on must be told" "getters"
  ]

[<Tests>]
let walkModeProtocolTests =
  testList "SetWalkMode and a click, by mode" [
    testCase "SetWalkMode and its answer travel the wire" <| fun _ ->
      LiveValueTree.WalkMode.Everything
      |> fun mode ->
        decodeRequest (encodeRequest (SetWalkMode(4L, mode)))
        |> Expect.equal "request" (Result.Ok(SetWalkMode(4L, mode)))
        decodeResponse (encodeResponse (WalkModeSet(4L, mode)))
        |> Expect.equal "response" (Result.Ok(WalkModeSet(4L, mode)))

    testCase "a click is meaningful in Safe mode only" <| fun _ ->
      MemberClick.judge LiveValueTree.WalkMode.Safe
      |> Expect.equal "Safe runs the click" (Result.Ok())

    testCase "in Everything the getters already ran, and the click is refused with that reason" <| fun _ ->
      MemberClick.judge LiveValueTree.WalkMode.Everything
      |> Expect.equal "refused" (Result.Error EveryGetterAlreadyRan)

    testCase "in Off a class is collapsed, and the click is refused with that reason" <| fun _ ->
      MemberClick.judge LiveValueTree.WalkMode.Off
      |> Expect.equal "refused" (Result.Error ClassesAreCollapsed)

    testCase "every refusal says why in words" <| fun _ ->
      [ EveryGetterAlreadyRan; ClassesAreCollapsed ]
      |> List.iter (fun refusal ->
        MemberClick.describeRefusal refusal
        |> Expect.isNotEmpty (sprintf "%A is explained" refusal))

    testCase "a refusal and an unavailable click travel the wire as outcomes" <| fun _ ->
      [ MemberRefused ClassesAreCollapsed
        MemberRefused EveryGetterAlreadyRan
        MemberUnavailable NoIsolatedHost
        MemberUnavailable(HostNotRunning "the host exited") ]
      |> List.iter (fun outcome ->
        decodeResponse (encodeResponse (MemberResult(9L, outcome)))
        |> Expect.equal (sprintf "%A" outcome) (Result.Ok(MemberResult(9L, outcome))))
  ]
