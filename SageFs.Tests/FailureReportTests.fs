module SageFs.Tests.FailureReportTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Build
open SageFs.Build.FailureReport

let private checkout = "/home/will/.local/share/sagefs-gate/checkout-41322401"

/// A host-tier ERROR as Expecto printed it in a real gate log: the header ends in `[Expecto]`, the message opens with
/// ` - errors: [` and the frames follow. The frame in our code carries the checkout path.
let private erroredBlock =
  [ "[E] 2026-10-04T17:00:48.1453896+00:00: Integration (host).[Integration] hot-reload shape matrix: a startup-captured handler table errored in 00:00:15.3600000 [Expecto]"
    " - errors: [System.ArgumentOutOfRangeException: Index was out of range. (Parameter 'chunkLength')"
    "   at System.Text.StringBuilder.ToString()"
    sprintf "   at SageFs.Tests.WebAppHotReloadVerificationTests.webAppHotReloadVerificationTests@554-8.Invoke(Unit unitVar0) in %s/SageFs.Tests/WebAppHotReloadVerificationTests.fs:line 663" checkout
    "   at Expecto.Impl.execTestAsync@579-1.Invoke(Unit unitVar)"
    "   at Microsoft.FSharp.Control.AsyncPrimitives.CallThenInvoke[T,TResult](AsyncActivation`1 ctxt, TResult result1, FSharpFunc`2 part2) in D:\\a\\_work\\1\\s\\src\\fsharp\\src\\FSharp.Core\\async.fs:line 509"
    "   at Microsoft.FSharp.Control.Trampoline.Execute(FSharpFunc`2 firstAction) in D:\\a\\_work\\1\\s\\src\\fsharp\\src\\FSharp.Core\\async.fs:line 103]" ]

/// A FAILED case, as the mutation tier printed it: header ends in `failed in <t>. `, the message is on the next
/// line, and the first frame is an F# closure with no `in file:line`.
let private failedBlock =
  [ "[E] 2026-10-04T17:04:56.8965593+00:00: WHY — new_value_is_Restart_not_Patch — a new value cannot be patched in failed in 00:00:00.0230000. "
    "a new value must restart, not patch: [\"v\"]"
    "   at ReloadPlanningDecisionMutationTests.reloadPlanningDecisionMutationTests@52-8.Invoke(String msg)"
    "   at Expecto.Impl.execTestAsync@579-1.Invoke(Unit unitVar)"
    "   at Microsoft.FSharp.Control.Trampoline.Execute(FSharpFunc`2 firstAction) in D:\\a\\_work\\1\\s\\src\\fsharp\\src\\FSharp.Core\\async.fs:line 112 [Expecto]" ]

let private chatter =
  [ "info: SageFs.Daemon[0]"
    "      SageFs daemon v0.6.892.0 starting"
    "[I] 2026-10-04T17:04:56.8990654+00:00: EXPECTO! 1 tests run in 00:00:00.0405345 for something – 0 passed" ]

[<Tests>]
let tests =
  testList "FailureReport" [

    testCase "an errored host case gives its full name, the message and the first frame in our code" <| fun _ ->
      match parseAll (erroredBlock @ chatter) with
      | [ f ] ->
        f.Outcome |> Expect.equal "an exception is an error, not a failed assertion" Errored
        f.Name |> Expect.stringStarts "the name is the whole hierarchy as printed" "Integration (host).[Integration] hot-reload shape matrix"
        f.Name.Contains " errored in " |> Expect.isFalse "the timing suffix is not part of the name"
        f.Message |> Expect.hasLength "the message is the line before the first frame" 1
        topRepoFrame checkout f.Frames
        |> Expect.equal "the framework frame is skipped and the repo frame is named by repo-relative path" (Some { File = "SageFs.Tests/WebAppHotReloadVerificationTests.fs"; Line = 663 })
      | other -> failtestf "expected exactly one failure, got %d" other.Length

    testCase "a failed case with only closure frames reports no location rather than inventing one" <| fun _ ->
      match parseAll (failedBlock @ chatter) with
      | [ f ] ->
        f.Outcome |> Expect.equal "an assertion is a failure" Failed
        f.Message |> Expect.equal "the assertion message is kept verbatim" [ "a new value must restart, not patch: [\"v\"]" ]
        topRepoFrame checkout f.Frames |> Expect.isNone "no frame names a file, and none is made up"
      | other -> failtestf "expected exactly one failure, got %d" other.Length

    testCase "a block is closed by whatever starts the next thing: another failure, a log line, or the end of the log" <| fun _ ->
      parseAll (erroredBlock @ failedBlock) |> Expect.hasLength "a header ends the block before it" 2
      parseAll erroredBlock |> Expect.hasLength "the end of the log ends the block" 1
      parseAll (erroredBlock @ [ "info: SageFs.Daemon[0]" ]) |> Expect.hasLength "a daemon log line ends the block" 1
      parseAll chatter |> Expect.isEmpty "chatter alone is not a failure"

    testCase "ANSI colour around the header does not hide a failure" <| fun _ ->
      let coloured = erroredBlock |> List.mapi (fun i l -> match i with 0 -> "\x1b[31m" + l + "\x1b[0m" | _ -> l)
      parseAll coloured |> Expect.hasLength "the colour codes are stripped before matching" 1

    testProperty "the streaming reader and the whole-log reader agree wherever the failure sits in the noise" <|
      fun (before: NonEmptyString list) (after: NonEmptyString list) ->
        let noise (xs: NonEmptyString list) = xs |> List.map (fun s -> "      " + s.Get.Replace('\n', ' ').Replace('\r', ' '))
        let lines = noise before @ [ "info: SageFs.Daemon[0]" ] @ erroredBlock @ chatter @ noise after
        let streamed =
          lines
          |> List.fold (fun (state, found) line ->
            match step state line with
            | next, Some f -> next, f :: found
            | next, None -> next, found) (Idle, [])
          |> fun (state, found) -> (match flush state with Some f -> f :: found | None -> found) |> List.rev
        streamed = parseAll lines

    testCase "the rerun command drops the shard, keeps the tier's flags and quotes the name" <| fun _ ->
      reproduceCommand "SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll" "--integration-host --shard 1/5 --summary" "A.it's b"
      |> Expect.equal "the shard is gone, the apostrophe is escaped, --filter (not --filter-test-case) carries the name"
           "dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --integration-host --summary --filter 'A.it'\\''s b'"

    testCase "every line of the report carries the console prefix, and it names the case, the place and the rerun" <| fun _ ->
      let f = parseAll erroredBlock |> List.head
      let lines = render "--integration-host[1/5]" checkout "dotnet x --filter 'y'" NotRecorded f
      lines |> List.forall (fun l -> l.StartsWith linePrefix) |> Expect.isTrue "the pipeline's console filter shows exactly these lines"
      let text = String.concat "\n" lines
      text |> Expect.stringContains "the tier is named" "--integration-host[1/5]"
      text |> Expect.stringContains "the place is file:line" "SageFs.Tests/WebAppHotReloadVerificationTests.fs:663"
      text |> Expect.stringContains "the rerun is there" "dotnet x --filter 'y'"

    testCase "the mutation gate's own failing cases do not fail its tier, every other tier's do" <| fun _ ->
      TierPlan.caseFailureFailsTier (TierPlan.tier "--mutation-score") |> Expect.isFalse "its log is full of expected [E] lines"
      TierPlan.caseFailureFailsTier (TierPlan.tier "--integration-host --shard 1/5 --summary") |> Expect.isTrue "a host case that fails fails the tier"
      TierPlan.caseFailureFailsTier (TierPlan.tier "--summary") |> Expect.isTrue "so does a default case"
  ]

[<Tests>]
let costTests =
  testList "TierCost" [

    testCase "the counters the scope's epilogue writes parse into seconds and a memory peak" <| fun _ ->
      TierCost.parse "usage_usec 1500000\nuser_usec 1000000\nsystem_usec 500000\npeak_bytes 2147483648\n"
      |> Result.map (fun cost -> cost.UserSeconds, cost.SystemSeconds, cost.PeakBytes)
      |> Expect.equal "microseconds become seconds" (Result.Ok (1.0, 0.5, 2147483648L))

    testCase "a counters file missing a field is an error, never zeros" <| fun _ ->
      match TierCost.parse "user_usec 5\n" with
      | Result.Error _ -> ()
      | Result.Ok cost -> failtestf "expected an error, got %A" cost

    testCase "the accounted command runs the whole argv inside the scope and passes its exit code through" <| fun _ ->
      let argv = TierCost.accountedArgv "sagefs-tier-x" [ "echo"; "hi" ]
      argv |> List.take 5 |> Expect.equal "a transient user scope, quiet, collected" [ "systemd-run"; "--user"; "--scope"; "--quiet"; "--collect" ]
      argv |> List.rev |> List.take 2 |> Expect.equal "the original argv is the tail, untouched" [ "hi"; "echo" ]
      argv |> List.exists (fun a -> a.Contains "exit $rc") |> Expect.isTrue "the tier's exit code is what the wrapper exits with"
  ]
