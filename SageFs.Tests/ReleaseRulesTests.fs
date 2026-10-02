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

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
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

/// What the hook knows, from a table: version per commit and the commits that have a gate pass.
let private facts (versions: (string * string) list) (passed: string list) (skip: bool) : PushFacts =
  { VersionAt = fun sha -> versions |> List.tryFind (fun (s, _) -> s = sha) |> Option.map snd
    HasPass = fun sha -> List.contains sha passed
    SkipGate = skip }

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

    testCase "WHY — an ungated commit is refused, and the override lets it through but says so" <| fun _ ->
      let known = [ sha1, "0.1.2"; sha2, "0.1.1" ]
      judgePush (facts known [] false) (master sha1 sha2) |> Expect.equal "refused" (NotGated sha1)
      judgePush (facts known [] true) (master sha1 sha2) |> Expect.equal "overridden" (SkippedGate sha1)

    testCase "WHY — a version git cannot read (an object this clone lacks) does not block the push on its own" <| fun _ ->
      judgePush (facts [] [ sha1 ] false) (master sha1 sha2) |> Expect.equal "unreadable versions skip the rule" AlreadyGated

    testCase "WHY — only a repeat and an ungated commit fail the push; the override and a pass do not" <| fun _ ->
      [ RepeatsVersion(sha1, "1", "1"); NotGated sha1 ] |> List.forall refuses |> Expect.isTrue "refusals"
      [ NotMaster; Deleting; AlreadyGated; SkippedGate sha1 ] |> List.exists refuses |> Expect.isFalse "no refusals"

    testCase "WHY — every refusal names the commands that fix it, and they are the F# scripts" <| fun _ ->
      let said = [ RepeatsVersion(sha1, "0.1.1", "0.1.1"); NotGated sha1; SkippedGate sha1 ] |> List.collect messagesFor |> String.concat "\n"
      said |> Expect.stringContains "ship" "dotnet fsi scripts/ship.fsx"
      said |> Expect.stringContains "gate" "dotnet fsi scripts/local-gate.fsx -- aaaaaaaa"
      said |> Expect.stringContains "bump" "dotnet fsi scripts/bump-version.fsx"
      said |> Expect.stringContains "short sha" "pre-push: aaaaaaaa has not passed the local gate."
      messagesFor AlreadyGated |> Expect.isEmpty "a gated push says nothing"
  ]

[<Tests>]
let gateTests =
  testList "release rules: the local gate" [

    testCase "WHY — the gate's arguments: a commit defaults to HEAD, --force and --promote are read first" <| fun _ ->
      parseGateArgs [] |> Expect.equal "default" (Gate("HEAD", false))
      parseGateArgs [ "abc123" ] |> Expect.equal "a commit" (Gate("abc123", false))
      parseGateArgs [ "--force" ] |> Expect.equal "force alone" (Gate("HEAD", true))
      parseGateArgs [ "--force"; "abc123" ] |> Expect.equal "force and a commit" (Gate("abc123", true))
      parseGateArgs [ "--promote"; "abc123"; "release" ] |> Expect.equal "promote" (Promote("abc123", "release"))

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
      for line in [ "── tier fast: 12s"; "STAGE #3 build"; "Tiers: 2 Trusted"; "Expected wall: 20m"; "trust report: /x"; "| tier | verdict |"; "something error here" ] do
        isProgressLine line |> Expect.isTrue (sprintf "shown: %s" line)
      for line in [ "restoring packages"; "  Passed! 12 tests"; "STAGE build"; "a | b" ] do
        isProgressLine line |> Expect.isFalse (sprintf "hidden: %s" line)

    testCase "WHY — the ^ anchors see the raw line, so a coloured tier line is hidden on the console exactly as it was under grep" <| fun _ ->
      isProgressLine "\u001b[32m── tier fast: 12s\u001b[0m" |> Expect.isFalse "colour before the anchor"
      stripAnsi "\u001b[32mok\u001b[0m \u001b[1;31mred\u001b[0m" |> Expect.equal "colour removed" "ok red"

    testCase "WHY — a failed gate repeats the lines that name the cause" <| fun _ ->
      for line in [ "── tier slow failed"; "  ── tier x"; "::error::boom"; "  ::error::boom"; "STAGE #4 failed"; "STAGE #4 finished"; "build error CS0001" ] do
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
  ]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant wiringTests |> ignore
