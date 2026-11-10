/// The release scripts' decisions (scripts/ReleaseRules.fs, linked into this project): the version bump
/// arithmetic behind bump-version.fsx, what the pre-push hook says about a pushed ref, and how local-gate.fsx
/// reads its arguments, names its checkout and filters the pipeline's output. The scripts only do the IO around
/// these functions, so this is where their rules get pinned.
module SageFs.Tests.ReleaseRulesTests

open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open ReleaseRules

let private repoRoot = RepoPaths.repoPathFull [||]
let private read (relative: string) = File.ReadAllText(Path.Combine(repoRoot, relative))

let private versionGen =
  gen {
    let! major = Gen.choose (0, 20)
    let! minor = Gen.choose (0, 200)
    let! patch = Gen.choose (0, 5000)
    return (major, minor, patch)
  }

let private show (major, minor, patch) = sprintf "%d.%d.%d" major minor patch

let private prop gen body = Prop.forAll (Arb.fromGen gen) body

let private sha40 (c: char) = String(c, 40)
let private sha1 = sha40 'a'
let private sha2 = sha40 'b'

/// What the hook knows, from a table: version per commit, the commits that have a gate pass, and how many
/// release commits the pushed range adds (the count that decides whether one push carries one release or
/// several, of which only the last can ship).
let private factsWithReleases (versions: (string * string) list) (passed: string list) (skip: bool) (releases: int) : PushFacts =
  { VersionAt = fun sha -> versions |> List.tryFind (fun (s, _) -> s = sha) |> Option.map snd
    HasPass = fun sha -> List.contains sha passed
    SkipGate = skip
    ReleaseCountInRange = fun _ _ -> releases }

/// The same facts for a push whose range carries one release, which is the normal case.
let private facts versions passed skip = factsWithReleases versions passed skip 0

let private master (local: string) (remote: string) : PushedRef =
  { LocalRef = "refs/heads/master"; LocalSha = local; RemoteRef = "refs/heads/master"; RemoteSha = remote }

[<Tests>]
let versionTests =
  testList "release rules: versions" [

    testCase "WHY — versionIn reads the first <Version> of a props file, and says nothing when there is none" <| fun _ ->
      versionIn "<Project><PropertyGroup><Version>0.6.882</Version></PropertyGroup></Project>"
      |> Expect.equal "the version" (Some "0.6.882")
      versionIn "<Project />" |> Expect.equal "no version" None

    testCase "WHY — 0.6.9 is lower than 0.6.10: parts compare as numbers, not as text (the sort -V rule the hook always used)" <| fun _ ->
      raisesVersion "0.6.10" "0.6.9" |> Expect.isTrue "10 is above 9"
      raisesVersion "0.6.9" "0.6.10" |> Expect.isFalse "9 is below 10"
      raisesVersion "0.10.0" "0.9.99" |> Expect.isTrue "minor compares as a number too"

    testCase "WHY — an equal version does not raise: NuGet --skip-duplicate would publish nothing and still go green" <| fun _ ->
      raisesVersion "0.6.882" "0.6.882" |> Expect.isFalse "a repeat is not a raise"

    testCase "WHY — the order matches what sort -V printed for the shapes a version can take" <| fun _ ->
      // printf '0.6.9\n0.6.10\n0.6.1\n0.6\n0.10.0\n0.6.9-local.5\n1.0.0\n' | sort -V
      let shuffled = [ "1.0.0"; "0.6.10"; "0.10.0"; "0.6.9-local.5"; "0.6"; "0.6.9"; "0.6.1" ]
      shuffled
      |> List.sortWith compareVersions
      |> Expect.equal "sort -V order" [ "0.6"; "0.6.1"; "0.6.9"; "0.6.9-local.5"; "0.6.10"; "0.10.0"; "1.0.0" ]

    testProperty "PROPERTY: for major.minor.patch, the order is the order of the three numbers" <|
      prop (Gen.zip versionGen versionGen) (fun (a, b) ->
        sign (compareVersions (show a) (show b)) = sign (compare a b))

    testProperty "PROPERTY: a bumped version always raises, and only the patch moved" <|
      prop versionGen (fun (major, minor, patch) ->
        match bumpPatch (show (major, minor, patch)) with
        | Ok next -> next = show (major, minor, patch + 1) && raisesVersion next (show (major, minor, patch))
        | Error _ -> false)

    testCase "WHY — a version that is not major.minor.patch with a numeric patch is refused, never guessed at" <| fun _ ->
      bumpPatch "0.6" |> Expect.equal "two parts" (Error(NotMajorMinorPatch "0.6"))
      bumpPatch "0.6.9-local.5" |> Expect.equal "a suffix" (Error(NotMajorMinorPatch "0.6.9-local.5"))
      bumpPatch "a.b.c" |> Expect.equal "words" (Error(NotMajorMinorPatch "a.b.c"))
      bumpPatch "1.2.3.4" |> Expect.equal "four parts" (Error(NotMajorMinorPatch "1.2.3.4"))

    testCase "WHY — bumping the props file changes the <Version> tag and nothing else in the file" <| fun _ ->
      let props = "<Project>\n  <Version>0.6.882</Version>\n  <Other>0.6.882</Other>\n</Project>\n"
      withPropsVersion "0.6.882" "0.6.883" props
      |> Expect.equal "only the tag moved" "<Project>\n  <Version>0.6.883</Version>\n  <Other>0.6.882</Other>\n</Project>\n"
      withPropsVersion "9.9.9" "9.9.10" props |> Expect.equal "an absent current version changes nothing" props

    testCase "WHY — bumping package.json sets the first version and leaves every other byte alone" <| fun _ ->
      let json = "{\n  \"name\": \"sagefs\",\n  \"version\": \"0.6.882\",\n  \"engines\": { \"vscode\": \"^1.90.0\" },\n  \"x\": { \"version\": \"1.0.0\" }\n}\n"
      withPackageVersion "0.6.883" json
      |> Expect.equal "the first version moved" "{\n  \"name\": \"sagefs\",\n  \"version\": \"0.6.883\",\n  \"engines\": { \"vscode\": \"^1.90.0\" },\n  \"x\": { \"version\": \"1.0.0\" }\n}\n"
  ]

[<Tests>]
let prePushTests =
  testList "release rules: the pre-push hook" [

    testCase "WHY — a push line is four fields, and anything else is not a push line" <| fun _ ->
      parsePushLine "refs/heads/master abc refs/heads/master def"
      |> Expect.equal "four fields" (Some { LocalRef = "refs/heads/master"; LocalSha = "abc"; RemoteRef = "refs/heads/master"; RemoteSha = "def" })
      parsePushLine "refs/heads/master abc" |> Expect.equal "two fields" None
      parsePushLine "" |> Expect.equal "empty" None

    testCase "WHY — only master is checked: a branch push passes ungated and without a version raise" <| fun _ ->
      let push = { master sha1 sha2 with RemoteRef = "refs/heads/feature" }
      judgePush (facts [] [] false) push |> Expect.equal "not master" NotMaster

    testCase "WHY — deleting master is not a release" <| fun _ ->
      judgePush (facts [] [] false) (master zeroSha sha2) |> Expect.equal "deleting" Deleting

    testCase "WHY — a gated commit that raises the version is let through" <| fun _ ->
      judgePush (facts [ sha1, "0.1.10"; sha2, "0.1.9" ] [ sha1 ] false) (master sha1 sha2)
      |> Expect.equal "gated and raised" AlreadyGated

    testCase "WHY — the first master push (no remote master yet) skips the version rule but still needs the gate" <| fun _ ->
      judgePush (facts [ sha1, "0.1.1" ] [] false) (master sha1 zeroSha) |> Expect.equal "needs the gate" (NotGated sha1)
      judgePush (facts [ sha1, "0.1.1" ] [ sha1 ] false) (master sha1 zeroSha) |> Expect.equal "gated" AlreadyGated

    testCase "WHY — a repeated version is refused even when the commit is gated, because the release would publish nothing" <| fun _ ->
      judgePush (facts [ sha1, "0.1.1"; sha2, "0.1.1" ] [ sha1 ] false) (master sha1 sha2)
      |> Expect.equal "repeat" (RepeatsVersion(sha1, "0.1.1", "0.1.1"))

    testCase "WHY — the skip-gate override never excuses a repeated version" <| fun _ ->
      judgePush (facts [ sha1, "0.1.1"; sha2, "0.1.1" ] [] true) (master sha1 sha2)
      |> Expect.equal "still a repeat" (RepeatsVersion(sha1, "0.1.1", "0.1.1"))

    testCase "WHY — the skip-gate override never excuses a stack of releases either: the runner can gate it, but it still publishes only the tip" <| fun _ ->
      judgePush (factsWithReleases [ sha1, "0.1.2"; sha2, "0.1.1" ] [] true 3) (master sha1 sha2)
      |> Expect.equal "still a stack" (StackedReleases(sha1, 3))

    testCase "WHY — an ungated commit is refused, and the override lets it through but says so" <| fun _ ->
      let known = [ sha1, "0.1.2"; sha2, "0.1.1" ]
      judgePush (facts known [] false) (master sha1 sha2) |> Expect.equal "refused" (NotGated sha1)
      judgePush (facts known [] true) (master sha1 sha2) |> Expect.equal "overridden" (SkippedGate sha1)

    testCase "WHY — a version git cannot read (an object this clone lacks) does not block the push on its own" <| fun _ ->
      judgePush (facts [] [ sha1 ] false) (master sha1 sha2) |> Expect.equal "unreadable versions skip the rule" AlreadyGated

    testCase "WHY — a push carrying two releases is refused: the pipeline publishes the version at the tip, so the first release would never reach NuGet" <| fun _ ->
      judgePush (factsWithReleases [ sha1, "0.1.2"; sha2, "0.1.1" ] [ sha1 ] false 2) (master sha1 sha2)
      |> Expect.equal "stacked" (StackedReleases(sha1, 2))

    testCase "WHY — one release in the range is the normal case and is not a refusal" <| fun _ ->
      judgePush (factsWithReleases [ sha1, "0.1.2"; sha2, "0.1.1" ] [ sha1 ] false 1) (master sha1 sha2)
      |> Expect.equal "gated and one release" AlreadyGated

    testCase "WHY — the first push to master has no range to count, so it is never called stacked" <| fun _ ->
      judgePush (factsWithReleases [ sha1, "0.1.1" ] [] false 9) (master sha1 zeroSha)
      |> Expect.equal "not stacked" (NotGated sha1)

    testCase "WHY — a range git cannot read is not a stack: only a count above one refuses" <| fun _ ->
      judgePush (factsWithReleases [ sha1, "0.1.2"; sha2, "0.1.1" ] [ sha1 ] false 0) (master sha1 sha2)
      |> Expect.equal "nothing to say about the range" AlreadyGated

    testCase "WHY — only a repeat and an ungated commit fail the push; the override and a pass do not" <| fun _ ->
      [ RepeatsVersion(sha1, "1", "1"); NotGated sha1; StackedReleases(sha1, 2) ] |> List.forall refuses |> Expect.isTrue "refusals"
      [ NotMaster; Deleting; AlreadyGated; SkippedGate sha1 ] |> List.exists refuses |> Expect.isFalse "no refusals"

    testCase "WHY — every refusal names the commands that fix it, and they are the F# scripts" <| fun _ ->
      let said = [ RepeatsVersion(sha1, "0.1.1", "0.1.1"); NotGated sha1; SkippedGate sha1; StackedReleases(sha1, 4) ] |> List.collect messagesFor |> String.concat "\n"
      said |> Expect.stringContains "ship" "dotnet fsi scripts/ship.fsx"
      said |> Expect.stringContains "gate" "dotnet fsi scripts/local-gate.fsx -- aaaaaaaa"
      said |> Expect.stringContains "bump" "dotnet fsi scripts/bump-version.fsx"
      said |> Expect.stringContains "short sha" "pre-push: aaaaaaaa has not passed the local gate."
      said |> Expect.stringContains "the stack says how many" "this push carries 4 releases"
      said |> Expect.stringContains "the stack says what happens to the earlier ones" "never reach NuGet"
      said
      |> Expect.stringContains "the stack says a plain ship deals with a bump a failed ship left behind" "A plain ship now reuses a single bump"
      messagesFor AlreadyGated |> Expect.isEmpty "a gated push says nothing"
  ]

let private sha3 = sha40 'c'

/// A table of versions per commit, as the plan's `versionAt` reads them.
let private versionTable (versions: (string * string) list) : string -> VersionRead =
  fun sha ->
    match versions |> List.tryFind (fun (s, _) -> s = sha) with
    | Some(_, version) -> Read version
    | None -> Unreadable

let private isRefusal (plan: BumpPlan) : bool =
  match plan with
  | BumpAnew
  | ReuseBump _ -> false
  | RefuseStacked _
  | RefuseNotRaised _
  | RefuseUnreadable _ -> true

/// A log line the way `git log --format="%H %s"` prints it.
let private logLine (sha: string) (subject: string) = sha + " " + subject

[<Tests>]
let bumpPlanTests =
  testList "release rules: the bump plan behind a plain ship" [

    testCase "WHY — the marker has one home: the subject ship commits, the hook counts and the plan finds are the same text" <| fun _ ->
      releaseSubject "0.6.930" |> Expect.equal "the subject" "chore: release v0.6.930"
      isReleaseSubject (releaseSubject "0.6.930") |> Expect.isTrue "a subject ship wrote is recognised"
      isReleaseSubject "fix(release): chore: release v0.6.930 is mentioned" |> Expect.isFalse "a subject that only mentions it is not one"
      isReleaseSubject "chore: release notes" |> Expect.isFalse "the marker ends in v"

    testCase "WHY — release commits are read from the log by subject, in the order git printed them, and a body line that only looks like one is ignored" <| fun _ ->
      let log =
        [ logLine sha1 (releaseSubject "0.6.931")
          logLine sha2 "fix(release): the retry"
          logLine sha3 (releaseSubject "0.6.930")
          "" ]
        |> String.concat "\n"
      releaseShasIn log |> Expect.equal "newest first, as printed" [ sha1; sha3 ]
      releaseShasIn "" |> Expect.isEmpty "an empty range has none"
      releaseShasIn (sha1 + "\n") |> Expect.isEmpty "a line with no subject has none"

    testCase "WHY — nothing waiting means bump, exactly as ship always did" <| fun _ ->
      planBump [] (versionTable []) (Read "0.6.929") |> Expect.equal "bump anew" BumpAnew
      planBump [] (versionTable []) Unreadable |> Expect.equal "bump anew when master is unreadable too" BumpAnew

    testCase "WHY — one bump left behind by a failed gate is reused: a second one is what made the hook refuse after a whole new gate" <| fun _ ->
      planBump [ sha1 ] (versionTable [ sha1, "0.6.930" ]) (Read "0.6.929")
      |> Expect.equal "reuse it" (ReuseBump(sha1, "0.6.930"))

    testCase "WHY — a waiting bump that does not raise what master has is refused with both versions, since it would publish nothing" <| fun _ ->
      planBump [ sha1 ] (versionTable [ sha1, "0.6.929" ]) (Read "0.6.929")
      |> Expect.equal "equal" (RefuseNotRaised(sha1, "0.6.929", "0.6.929"))
      planBump [ sha1 ] (versionTable [ sha1, "0.6.9" ]) (Read "0.6.10")
      |> Expect.equal "lower, compared as numbers" (RefuseNotRaised(sha1, "0.6.9", "0.6.10"))

    testCase "WHY — a waiting bump whose version cannot be read is refused, never guessed at" <| fun _ ->
      planBump [ sha1 ] (versionTable []) (Read "0.6.929") |> Expect.equal "unreadable" (RefuseUnreadable sha1)

    testCase "WHY — an unreadable master version reuses the waiting bump, the way the hook only refuses a repeat when both versions are known" <| fun _ ->
      planBump [ sha1 ] (versionTable [ sha1, "0.6.930" ]) Unreadable |> Expect.equal "reuse" (ReuseBump(sha1, "0.6.930"))

    testCase "WHY — two or more waiting bumps are refused before any gate, whatever their versions" <| fun _ ->
      planBump [ sha1; sha2 ] (versionTable [ sha1, "0.6.930"; sha2, "0.6.931" ]) (Read "0.6.929")
      |> Expect.equal "stacked" (RefuseStacked [ sha1; sha2 ])
      planBump [ sha1; sha2; sha3 ] (versionTable []) Unreadable
      |> Expect.equal "stacked even when nothing is readable" (RefuseStacked [ sha1; sha2; sha3 ])

    testProperty "PROPERTY: the plan refuses exactly when the range has several bumps, or one that does not raise or cannot be read" <|
      prop (Gen.zip (Gen.choose (0, 4)) (Gen.zip versionGen versionGen)) (fun (count, (waiting, onMaster)) ->
        let shas = [ for i in 1 .. count -> sha40 (char (int 'a' + i)) ]
        let versionAt = fun _ -> Read(show waiting)
        let plan = planBump shas versionAt (Read(show onMaster))
        let expectedRefusal =
          match count with
          | 0 -> false
          | 1 -> not (raisesVersion (show waiting) (show onMaster))
          | _ -> true
        isRefusal plan = expectedRefusal)

    testProperty "PROPERTY: a plain ship adds a bump only when none is waiting" <|
      prop (Gen.choose (0, 4)) (fun count ->
        let shas = [ for i in 1 .. count -> sha40 (char (int 'a' + i)) ]
        let plan = planBump shas (fun _ -> Read "0.6.930") (Read "0.6.929")
        (plan = BumpAnew) = (count = 0))

    testCase "WHY — reusing a bump says which commit and which version, and adds no commands" <| fun _ ->
      let said = bumpPlanMessages (ReuseBump(sha1, "0.6.930")) |> String.concat "\n"
      said |> Expect.stringContains "the short sha" "ship: aaaaaaaa already carries the release bump to v0.6.930"
      said |> Expect.stringContains "no second bump" "not adding a second one"
      bumpPlanMessages BumpAnew |> Expect.isEmpty "a fresh bump says nothing"

    testCase "WHY — a stack names every commit and prints the command that drops each extra one, newest first, keeping the oldest" <| fun _ ->
      let said = bumpPlanMessages (RefuseStacked [ sha1; sha2; sha3 ])
      let text = String.concat "\n" said
      text |> Expect.stringContains "the count" "3 release commits are waiting"
      text |> Expect.stringContains "the oldest is kept" "keep the oldest, aaaaaaaa"
      text |> Expect.stringContains "the way back to the plain command" "dotnet fsi scripts/ship.fsx again"
      let drops = said |> List.filter (fun line -> line.Contains "git rebase --onto") |> List.map (fun line -> line.Trim())
      drops |> Expect.equal "newest first, never the oldest" [ dropCommitCommand sha3; dropCommitCommand sha2 ]
      dropCommitCommand sha2 |> Expect.equal "the command" (sprintf "git rebase --onto %s^ %s" sha2 sha2)

    testCase "WHY — the other refusals say why and print the one command that drops the commit" <| fun _ ->
      let notRaised = bumpPlanMessages (RefuseNotRaised(sha1, "0.6.929", "0.6.929")) |> String.concat "\n"
      notRaised |> Expect.stringContains "both versions" "bump to v0.6.929, and origin/master is already at v0.6.929"
      notRaised |> Expect.stringContains "the command" (dropCommitCommand sha1)
      let unreadable = bumpPlanMessages (RefuseUnreadable sha1) |> String.concat "\n"
      unreadable |> Expect.stringContains "the reason" "no readable <Version>"
      unreadable |> Expect.stringContains "the command" (dropCommitCommand sha1)

    testCase "WHY — every refusal says what to run next, and a plan that goes ahead never prints a command to run" <| fun _ ->
      let refusals = [ RefuseStacked [ sha1; sha2 ]; RefuseNotRaised(sha1, "1", "1"); RefuseUnreadable sha1 ]
      for refusal in refusals do
        bumpPlanMessages refusal |> String.concat "\n" |> Expect.stringContains "the way on" "dotnet fsi scripts/ship.fsx again"
      for going in [ BumpAnew; ReuseBump(sha1, "1") ] do
        bumpPlanMessages going |> String.concat "\n" |> fun text -> text.Contains "git rebase" |> Expect.isFalse "no drop command"
  ]

[<Tests>]
let gateTests =
  testList "release rules: the local gate" [

    testCase "WHY — the gate's arguments: a commit defaults to HEAD, --force and --promote are read first" <| fun _ ->
      parseGateArgs [] |> Expect.equal "default" (Gate("HEAD", false, StopAtFirstRed))
      parseGateArgs [ "abc123" ] |> Expect.equal "a commit" (Gate("abc123", false, StopAtFirstRed))
      parseGateArgs [ "--force" ] |> Expect.equal "force alone" (Gate("HEAD", true, StopAtFirstRed))
      parseGateArgs [ "--force"; "abc123" ] |> Expect.equal "force and a commit" (Gate("abc123", true, StopAtFirstRed))
      parseGateArgs [ "--promote"; "abc123"; "release" ] |> Expect.equal "promote" (Promote("abc123", "release"))

    testCase "WHY — --keep-going is read from anywhere, so one gate run can list every red tier instead of the first" <| fun _ ->
      parseGateArgs [ "--keep-going" ] |> Expect.equal "alone" (Gate("HEAD", false, KeepGoing))
      parseGateArgs [ "abc123"; "--keep-going" ] |> Expect.equal "after a commit" (Gate("abc123", false, KeepGoing))
      parseGateArgs [ "--keep-going"; "--force"; "abc123" ] |> Expect.equal "with force and a commit" (Gate("abc123", true, KeepGoing))
      parseGateArgs [ "--force"; "--keep-going" ] |> Expect.equal "force then keep-going, no commit" (Gate("HEAD", true, KeepGoing))

    testCase "WHY — --promote without both its arguments is a usage error, not a guess" <| fun _ ->
      match parseGateArgs [ "--promote"; "abc123" ] with
      | Usage _ -> ()
      | other -> failtestf "expected Usage, got %A" other
      match parseGateArgs [ "--promote" ] with
      | Usage _ -> ()
      | other -> failtestf "expected Usage, got %A" other

    testCase "WHY — elapsed time reads as minutes and seconds with no padding, the way the gate always printed it" <| fun _ ->
      formatElapsed (TimeSpan(0, 3, 7)) |> Expect.equal "minutes and seconds" "3m7s"
      formatElapsed TimeSpan.Zero |> Expect.equal "under a second" "0m0s"
      passRecord "2026-10-02T14:01:30-05:00" (TimeSpan(0, 1, 1)) |> Expect.equal "the ok file" "2026-10-02T14:01:30-05:00 in 1m1s"

    testCase "WHY — a checkout is named by the first eight hex of the SHA-1 of the repo path, so the warm ones on disk keep being reused" <| fun _ ->
      // printf '/home/will/Work/SageFs' | sha1sum | cut -c1-8   and   printf '/tmp/x y/é' | sha1sum | cut -c1-8
      checkoutName "/home/will/Work/SageFs" |> Expect.equal "the repo" "checkout-41322401"
      checkoutName "/tmp/x y/é" |> Expect.equal "spaces and non-ASCII hash their UTF-8 bytes" "checkout-e28e4afb"

    testCase "WHY — the owner record is repo, pid, time, one per line, which is what the reaper reads" <| fun _ ->
      ownerRecord "/work/repo" 4242 "2026-10-02T14:01:30-05:00" |> Expect.equal "three lines" "/work/repo\n4242\n2026-10-02T14:01:30-05:00\n"

    testCase "WHY — the console shows stage boundaries, finished tiers, the trust table and failures, and nothing else" <| fun _ ->
      for line in [ "── tier fast: 12s"; "── background vscode  step 5 passed after 31s"; "FAILURE case:    A.b"; "cleanup: tier x scope gone"; "STAGE #3 build"; "Tiers: 2 Trusted"; "Expected wall: 20m"; "trust report: /x"; "| tier | verdict |"; "something error here" ] do
        isProgressLine line |> Expect.isTrue (sprintf "shown: %s" line)
      for line in [ "restoring packages"; "  Passed! 12 tests"; "STAGE build"; "a | b" ] do
        isProgressLine line |> Expect.isFalse (sprintf "hidden: %s" line)

    testCase "WHY — the ^ anchors see the raw line, so a coloured tier line is hidden on the console exactly as it was under grep" <| fun _ ->
      isProgressLine "\u001b[32m── tier fast: 12s\u001b[0m" |> Expect.isFalse "colour before the anchor"
      stripAnsi "\u001b[32mok\u001b[0m \u001b[1;31mred\u001b[0m" |> Expect.equal "colour removed" "ok red"

    testCase "WHY — a failed gate repeats the lines that name the cause" <| fun _ ->
      for line in [ "── tier slow failed"; "FAILURE where:   SageFs.Tests/A.fs:3"; "  ── tier x"; "::error::boom"; "  ::error::boom"; "STAGE #4 failed"; "STAGE #4 finished"; "build error CS0001" ] do
        isFailureSummaryLine line |> Expect.isTrue (sprintf "summarised: %s" line)
      for line in [ "restoring packages"; "STAGE #4 started" ] do
        isFailureSummaryLine line |> Expect.isFalse (sprintf "not summarised: %s" line)

    testCase "WHY — the lease reply: granted carries its id, wait means ask again, everything else means gate anyway" <| fun _ ->
      parseLeaseReply """{"decision":"granted","leaseId":"L9"}""" |> Expect.equal "granted" (LeaseGranted "L9")
      parseLeaseReply """{"decision":"wait","retryAfterSeconds":12.0,"reason":"busy"}""" |> Expect.equal "wait" LeaseWait
      parseLeaseReply """{"decision":"refused","reason":"over budget"}""" |> Expect.equal "refused" LeaseOther
      parseLeaseReply "" |> Expect.equal "no daemon" LeaseOther
      parseLeaseReply "<html>502</html>" |> Expect.equal "not json" LeaseOther
  ]

let private passStart = DateTimeOffset.Parse "2026-10-18T20:00:00+00:00"

/// How far before or after the pass started a file was written. Only the sign matters to the rule.
let private halfHour = TimeSpan(0, 30, 0)

let private reportAt (offset: TimeSpan) (text: string) =
  ReportWrittenAt(passStart + offset, text)

/// The ship printed the PREVIOUS pass's "first failure" block when the current pass was cancelled by the time cap
/// (exit 130, 2026-10-18): the gate reuses one checkout, `git clean` keeps ignored files, and the pipeline only
/// writes the report in its last stage, so a cancelled pass left the old file in place and the gate read it.
[<Tests>]
let failureReportTests =
  testList "release rules: which failure report belongs to this pass" [

    testCase "WHY — no report file means no report" <| fun _ ->
      chooseFailureReport passStart NoReportFile |> Expect.equal "nothing on disk" NoReport

    testCase "WHY — a report written after the pass started is this pass's" <| fun _ ->
      chooseFailureReport passStart (reportAt halfHour "FAILURE x")
      |> Expect.equal "written during the pass" (ShowReport "FAILURE x")

    testCase "WHY — a report written before the pass started is stale and is never shown, whatever it says" <| fun _ ->
      let before = passStart - halfHour
      chooseFailureReport passStart (reportAt (-halfHour) "FAILURE the pass before")
      |> Expect.equal "left over from an earlier pass" (StaleReport before)

    testCase "WHY — a report written at the very start still counts" <| fun _ ->
      chooseFailureReport passStart (reportAt TimeSpan.Zero "FAILURE x") |> Expect.equal "the boundary" (ShowReport "FAILURE x")

    testProperty "WHY — a report is shown only if it was written at or after the pass started, for any file time" <|
      prop (Gen.choose (-100000, 100000)) (fun seconds ->
        match chooseFailureReport passStart (reportAt (TimeSpan.FromSeconds(float seconds)) "r") with
        | ShowReport _ -> seconds >= 0
        | StaleReport _ -> seconds < 0
        | NoReport -> false)

    testCase "WHY — a cancelled pass with a stale report says it wrote none and why, and never prints the old text" <| fun _ ->
      let notice = failureNotice 130 (StaleReport(passStart - halfHour))
      let text = String.concat "\n" notice
      text |> Expect.stringContains "it says no report was written" "wrote no failure report"
      text |> Expect.stringContains "it says the old one is not shown" "NOT shown"
      text |> Expect.stringContains "exit 130 is named as a cancel, not a failure" "cancelled or timed out"
      text.Contains "the pass before" |> Expect.isFalse "the stale text is not in the notice"

    testCase "WHY — exits 124, 130 and 143 are cancels and a test exit is not" <| fun _ ->
      for code in [ 124; 130; 143 ] do
        endedByCancel code |> Expect.isTrue (sprintf "exit %d is a stop" code)
      for code in [ 1; 2; 3 ] do
        endedByCancel code |> Expect.isFalse (sprintf "exit %d is a test result" code)

    testCase "WHY — this pass's own report is printed whole" <| fun _ ->
      failureNotice 2 (ShowReport "FAILURE first failure\nFAILURE case: A\n")
      |> Expect.equal "trimmed, nothing added" [ "FAILURE first failure\nFAILURE case: A" ]
  ]

/// The ported scripts are wired to each other by name, and a stale name is a script that fails when someone runs it.
[<Tests>]
let wiringTests =
  testList "release scripts: wiring" [

    testCase "the release scripts are F# (.fsx), and the bash and PowerShell versions are gone" <| fun _ ->
      for name in [ "local-gate"; "bump-version"; "pre-push"; "install-hooks"; "smoke-test"; "record-demos"; "reinstall-vscode-ext"; "start-sagefs-otel"; "machine-bench-tiers" ] do
        File.Exists(Path.Combine(repoRoot, "scripts", name + ".fsx")) |> Expect.isTrue (sprintf "scripts/%s.fsx exists" name)
      for gone in [ "scripts/local-gate"; "scripts/bump-version"; "scripts/launch_daemon_detached.py"; "hooks/pre-push"; "hooks/pre-push.ps1"; "hooks/install-hooks.ps1"; "fix-async.ps1"
                    "scripts/smoke-test.ps1"; "scripts/record-demos.ps1"; "scripts/reinstall-vscode-ext.ps1"; "scripts/make-demo-gif.ps1"; "scripts/record-vscode-demo.ps1"
                    "scripts/machine-bench-tiers.sh"; "start-sagefs-otel.bat"; "configure-vscode.sh"; "configure-vscode.ps1"
                    "tools/agent-hooks/sagefs-repl-guard.fsx" ] do
        File.Exists(Path.Combine(repoRoot, gone)) |> Expect.isFalse (sprintf "%s is gone" gone)

    testCase "scripts/pre-push is only the shebang shim for pre-push.fsx, because git runs a hook by its extensionless name and dotnet fsi runs only .fsx files" <| fun _ ->
      let shim = read "scripts/pre-push"
      shim.Split('\n').[0] |> Expect.equal "the shebang names the script" "#!/usr/bin/env -S dotnet fsi scripts/pre-push.fsx"

    testCase "the self-hosted job, ship and the gate call the F# scripts by their .fsx names" <| fun _ ->
      let workflow = read ".github/workflows/main.yml"
      workflow |> Expect.stringContains "gate step" "dotnet fsi scripts/local-gate.fsx -- \"$GITHUB_SHA\""
      workflow |> Expect.stringContains "promote step" "dotnet fsi scripts/local-gate.fsx -- --promote \"$GITHUB_SHA\" release"
      let ship = read "scripts/ship.fsx"
      ship |> Expect.stringContains "ship runs the gate script" "\"local-gate.fsx\""
      ship |> Expect.stringContains "ship runs the bump script" "\"bump-version.fsx\""

    testCase "WHY - install-local starts the daemon in a directory that outlives the checkout it runs from" <| fun _ ->
      // The ship runs from a throwaway worktree, and the daemon it starts kept that worktree as its working
      // directory. When the worktree was removed, the daemon could no longer start `dotnet build` (a hard reset with
      // rebuild failed with "Unable to find the specified file"). The daemon starts in the install state directory.
      let startLine =
        read "scripts/install-local.fsx"
        |> fun text -> text.Split('\n')
        |> Array.tryFind (fun line -> line.Contains "\"setsid\"" && line.Contains "exec sagefs")
      match startLine with
      | None -> failtest "scripts/install-local.fsx no longer starts the daemon with setsid"
      | Some line ->
        line.TrimEnd().EndsWith "markDir |> ignore" |> Expect.isTrue (sprintf "the daemon starts in markDir, not in the repo: %s" (line.Trim()))

    testCase "no script, workflow or instruction file still names the deleted bash scripts" <| fun _ ->
      let stale = Regex(@"scripts/(local-gate|bump-version|ship|install-local)(?![\w.-])", RegexOptions.Compiled)
      let files =
        [ yield! Directory.EnumerateFiles(Path.Combine(repoRoot, ".github"), "*.yml", SearchOption.AllDirectories)
          yield! Directory.EnumerateFiles(Path.Combine(repoRoot, "scripts"), "*.fsx")
          yield Path.Combine(repoRoot, "AGENTS.md")
          yield Path.Combine(repoRoot, "ci-pipeline.fsx") ]
      let offenders =
        files
        |> List.collect (fun file ->
          File.ReadAllLines file
          |> Array.toList
          |> List.indexed
          |> List.filter (fun (_, line) -> stale.IsMatch line)
          |> List.map (fun (i, line) -> sprintf "%s:%d %s" (Path.GetRelativePath(repoRoot, file)) (i + 1) (line.Trim())))
      // The one deliberate mention: the test that says the bash scripts are gone lives in RatchetRegistryTests.
      offenders |> Expect.isEmpty "every reference carries the .fsx name"

    testCase "WHY — the gate prints only a failure report this pass wrote: it removes the old one first, notes when the pass began, and asks the rule" <| fun _ ->
      let gate = read "scripts/local-gate.fsx"
      let at (marker: string) = gate.IndexOf marker
      (at "File.Delete reportFile", -1) |> Expect.isGreaterThan "the checkout's old report is removed"
      (at "File.Delete reportFile", at "runPipeline wt sha log") |> Expect.isLessThan "removed before the pipeline runs"
      (at "let passStartedAt", at "runPipeline wt sha log") |> Expect.isLessThan "the start time is taken before the pipeline runs"
      gate |> Expect.stringContains "the rule decides which report to show" "ReleaseRules.chooseFailureReport passStartedAt onDisk"
      gate |> Expect.stringContains "the notice is the pure one" "ReleaseRules.failureNotice failed choice"

    testCase "WHY — ship, the hook and the plan share one marker: neither script spells the subject out, and ship asks the plan before it bumps" <| fun _ ->
      let code (relative: string) =
        read relative
        |> fun text -> text.Split('\n')
        |> Array.filter (fun line -> not (line.TrimStart().StartsWith "//"))
        |> String.concat "\n"
      for script in [ "scripts/ship.fsx"; "scripts/pre-push.fsx" ] do
        (code script).Contains releaseSubjectPrefix |> Expect.isFalse (sprintf "%s writes the marker only through ReleaseRules" script)
      let ship = code "scripts/ship.fsx"
      ship |> Expect.stringContains "the commit message comes from the shared subject" "ReleaseRules.releaseSubject version"
      ship |> Expect.stringContains "ship asks the plan" "ReleaseRules.planBump"
      ship |> Expect.stringContains "ship prints the plan" "ReleaseRules.bumpPlanMessages"
      (ship.IndexOf "let plan = planTheBump ()", ship.IndexOf "mustRun \"the roadmap refresh\"") |> Expect.isLessThan "the plan is made before the roadmap refresh, the ratchets or any gate"
      (ship.IndexOf "let plan = planTheBump ()", -1) |> Expect.isGreaterThan "and it is made"
      (ship.IndexOf "let plan = planTheBump ()", ship.IndexOf "| ReleaseRules.BumpAnew -> bumpAndCommit ()") |> Expect.isLessThan "and before the bump"
      (ship.IndexOf "| ReleaseRules.BumpAnew -> bumpAndCommit ()", -1) |> Expect.isGreaterThan "a bump happens only on the BumpAnew plan"
      code "scripts/pre-push.fsx" |> Expect.stringContains "the hook counts through the same reader" "ReleaseRules.releaseShasIn"

    testCase "WHY — the pipeline itself starts without the report of the run before it" <| fun _ ->
      let pipeline = read "ci-pipeline.fsx"
      pipeline |> Expect.stringContains "the stale report is deleted at start" "if File.Exists staleFailureReport then File.Delete staleFailureReport"
      let firstStage = Regex.Match(pipeline, "^  stage \"", RegexOptions.Multiline).Index
      (pipeline.IndexOf "File.Delete staleFailureReport", firstStage) |> Expect.isLessThan "before any stage can run"
  ]

/// Runs git in a directory and answers its exit code and its output.
let private git (dir: string) (args: string list) : int * string =
  let info = Diagnostics.ProcessStartInfo("git")
  info.WorkingDirectory <- dir
  info.RedirectStandardOutput <- true
  info.RedirectStandardError <- true
  info.UseShellExecute <- false
  for arg in [ "-c"; "user.name=gate-test"; "-c"; "user.email=gate-test@example.invalid"; "-c"; "commit.gpgsign=false" ] @ args do
    info.ArgumentList.Add arg
  use proc = Diagnostics.Process.Start info
  let output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd()
  proc.WaitForExit()
  proc.ExitCode, output

let private mustGit (dir: string) (args: string list) : string =
  match git dir args with
  | 0, output -> output.Trim()
  | code, output -> failwithf "git %s in %s exited %d: %s" (String.concat " " args) dir code output

[<Tests>]
let syncTests =
  testList "gate checkout sync" [

    testCase "WHY — a checkout whose index has lost files its HEAD still has reaches the next commit and ends clean, because the gate owns that checkout and nobody's work lives in it" <| fun _ ->
      let root = Path.Combine(Path.GetTempPath(), "sagefs-gate-sync-" + Guid.NewGuid().ToString "N")
      try
        let repo = Path.Combine(root, "repo")
        let checkout = Path.Combine(root, "gate-checkout")
        Directory.CreateDirectory repo |> ignore
        mustGit repo [ "init"; "-q" ] |> ignore
        Directory.CreateDirectory(Path.Combine(repo, "docs")) |> ignore
        File.WriteAllText(Path.Combine(repo, "docs", "plan.md"), "plan v1")
        File.WriteAllText(Path.Combine(repo, "code.fs"), "code v1")
        mustGit repo [ "add"; "." ] |> ignore
        mustGit repo [ "commit"; "-q"; "-m"; "one" ] |> ignore
        mustGit repo [ "worktree"; "add"; "--detach"; "-q"; checkout; "HEAD" ] |> ignore
        File.WriteAllText(Path.Combine(repo, "docs", "plan.md"), "plan v2")
        File.WriteAllText(Path.Combine(repo, "code.fs"), "code v2")
        mustGit repo [ "commit"; "-q"; "-a"; "-m"; "two" ] |> ignore
        let next = mustGit repo [ "rev-parse"; "HEAD" ]
        // What a gate checkout showed: the plan file is in the working tree and out of the index, so it is also untracked.
        mustGit checkout [ "rm"; "--cached"; "-q"; "docs/plan.md" ] |> ignore
        // The twin: the plain checkout the gate used to run refuses in this state, so the state is the real one.
        git checkout [ "checkout"; "--detach"; "-q"; next ]
        |> fst
        |> Expect.notEqual "the old plain checkout refuses in this state" 0
        let sync = syncCheckoutSteps next
        mustGit checkout sync.Checkout |> ignore
        for step in sync.Tidy do
          git checkout step |> ignore
        mustGit checkout [ "rev-parse"; "HEAD" ] |> Expect.equal "the checkout is at the commit" next
        mustGit checkout [ "status"; "--porcelain" ] |> Expect.equal "nothing is left over" ""
        File.ReadAllText(Path.Combine(checkout, "docs", "plan.md")) |> Expect.equal "the plan is the next commit's" "plan v2"
      finally
        try Directory.Delete(root, true) with _ -> ()

    testCase "WHY — the steps check out first, forced, and then tidy, so a failed checkout is never papered over by a tidy that ran on the old commit" <| fun _ ->
      let sync = syncCheckoutSteps (String('c', 40))
      sync.Checkout |> List.head |> Expect.equal "the first step is a checkout" "checkout"
      sync.Checkout |> List.contains "-f" |> Expect.isTrue "the checkout is forced"
      sync.Tidy |> List.map List.head |> Expect.equal "then it resets and cleans" [ "reset"; "clean" ]
  ]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant wiringTests |> ignore
