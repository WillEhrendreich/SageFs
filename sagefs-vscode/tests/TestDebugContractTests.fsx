// WHY — debugging a failing test from the editor is the one place the extension has to hand work to ANOTHER extension (a
// .NET debugger), and every way it can go wrong is silent from here: no debugger installed, a daemon status the client does
// not know, a command that the lens names and nothing registers, a hover link that runs nothing. None of it can be seen
// without an Electron host, so this pins the decisions (TestDebugPure.fs) and the wiring (the sources and package.json, read
// as data, the way CommandContractTests.fsx does).
//
// It also pins the client against the DAEMON: the statuses the daemon can send are read out of
// SageFs.Core/DebugTestRequest.fs, and every one must be a status this client reads, so a new daemon status fails here
// instead of reaching a person as a blank dialog.
//
// Runs under plain `dotnet fsi` (no Fable, no VS Code), mirroring AppRunContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"
#r "nuget: Expecto.FsCheck, 11.0.0-alpha8"
#r "nuget: FsCheck, 3.3.2"
#load "../src/LiveTestingTypes.fs"
#load "../src/TestDebugPure.fs"

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Microsoft.FSharp.Reflection
open SageFs.Vscode.LiveTestingTypes
open SageFs.Vscode.TestDebugPure

let private root = Path.Combine(__SOURCE_DIRECTORY__, "..")
let private repoRoot = Path.Combine(root, "..")
let private read (relative: string) = File.ReadAllText(Path.Combine(root, relative))

let private view : DebugAnswerView =
  { Status = ""
    Message = "the daemon's own words"
    Pid = 4242
    Ticket = "debug-4242-1"
    TestId = "ABCD1234"
    TestName = "suite adds"
    Symbols = "compiled"
    SymbolsNote = ""
    Access = "open"
    AccessNote = ""
    HoldMs = 120000
    Outcome = ""
    Detail = ""
    DurationMs = 0.0 }

let private withStatus (status: string) = { view with Status = status }

/// Every status string the daemon can send, read out of `DebugStatus.wire` in its source.
let private daemonStatuses : string list =
  let source = File.ReadAllText(Path.Combine(repoRoot, "SageFs.Core", "DebugTestRequest.fs"))
  let start = source.IndexOf "module DebugStatus ="
  let stop = source.IndexOf("/// What a finished test run came to", start)
  let section = source.Substring(start, stop - start)
  [ for m in Regex.Matches(section, "->\\s*\"([a-z_]+)\"") -> m.Groups.[1].Value ]

let private clientStatusCases : DebugStatus list =
  FSharpType.GetUnionCases typeof<DebugStatus>
  |> Array.filter (fun c -> c.Name <> "Unrecognised")
  |> Array.map (fun c -> FSharpValue.MakeUnion(c, [||]) :?> DebugStatus)
  |> Array.toList

let private isUnrecognised (status: DebugStatus) =
  match status with
  | DebugStatus.Unrecognised _ -> true
  | _ -> false

let private ext (id: string) (types: string list) : ExtensionDebuggers = { ExtensionId = id; DebuggerTypes = types }

let tests =
  testList "VS Code debug-a-test contract - pure logic and wiring" [

    testList "the daemon and the client agree on the statuses" [

      testCase "WHY - the daemon's statuses were found in its source, so this guard is not vacuous" <| fun _ ->
        (List.length daemonStatuses > 8) |> Expect.isTrue "the daemon spells several statuses"

      testCase "WHY - every status the daemon can send is one the client reads, never Unrecognised" <| fun _ ->
        for wire in daemonStatuses do
          statusOfWire wire |> isUnrecognised |> Expect.isFalse (sprintf "the client reads '%s'" wire)

      testCase "WHY - the client reads no status the daemon never sends, so a dead branch cannot hide a rename" <| fun _ ->
        // Each client status case maps from exactly one wire string; together they are the daemon's set.
        let reachable = daemonStatuses |> List.map statusOfWire |> List.filter (isUnrecognised >> not) |> List.distinct
        reachable |> List.length |> Expect.equal "one client case per daemon status" clientStatusCases.Length

      testCase "WHY - a status from a newer daemon is carried with its wire text, not dropped" <| fun _ ->
        statusOfWire "something_new" |> Expect.equal "carried" (DebugStatus.Unrecognised "something_new")
        match beginStep (withStatus "something_new") with
        | BeginStep.Refuse notice -> notice.Text |> Expect.equal "the daemon's own message" view.Message
        | other -> failtestf "expected a refusal carrying the message, got %A" other
    ]

    testList "is there a debugger" [

      testCase "WHY - nothing that contributes coreclr means missing" <| fun _ ->
        chooseDebugger [] |> Expect.equal "no extensions" DebuggerAvailability.Missing
        chooseDebugger [ ext "ionide.ionide-fsharp" []; ext "x.python" [ "python" ] ]
        |> Expect.equal "none with coreclr" DebuggerAvailability.Missing

      testCase "WHY - an extension counts by what it contributes, whatever it is called" <| fun _ ->
        chooseDebugger [ ext "someone.netcoredbg-thing" [ "coreclr" ] ]
        |> Expect.equal "installed" (DebuggerAvailability.Installed "someone.netcoredbg-thing")

      testCase "WHY - the recommended extension wins when several provide it" <| fun _ ->
        chooseDebugger [ ext "a.other" [ "coreclr" ]; ext RecommendedExtensionId [ "coreclr"; "clr" ] ]
        |> Expect.equal "recommended" (DebuggerAvailability.Installed RecommendedExtensionId)

      testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 } "WHY - the choice does not depend on the order VS Code lists extensions in"
      <| fun (seed: int) ->
        let providers = [ ext "b.two" [ "coreclr" ]; ext "a.one" [ "coreclr" ]; ext "c.three" [ "coreclr" ]; ext "d.none" [] ]
        let rng = Random seed
        let shuffled = providers |> List.sortBy (fun _ -> rng.Next())
        chooseDebugger shuffled = chooseDebugger providers

      testCase "WHY - missing says what to install and how, and names the debug type" <| fun _ ->
        missingDebuggerNotice.Severity |> Expect.equal "an error" Severity.Error
        missingDebuggerNotice.Text |> Expect.stringContains "names the extension" RecommendedExtensionId
        missingDebuggerNotice.Text |> Expect.stringContains "gives the command" "code --install-extension"
        missingDebuggerNotice.Text |> Expect.stringContains "names the debug type" CoreclrDebuggerType
        missingDebuggerNotice.Text.Contains "—" |> Expect.isFalse "no em dash"
    ]

    testList "what to attach to" [

      testCase "WHY - attach to the process the daemon named, with the coreclr debugger, as an attach request" <| fun _ ->
        let config = attachConfiguration (withStatus "held")
        config.Type |> Expect.equal "coreclr" "coreclr"
        config.Request |> Expect.equal "attach" "attach"
        config.ProcessId |> Expect.equal "the daemon's pid" 4242

      testCase "WHY - the session name carries the test and the ticket, so it can be told from any other session" <| fun _ ->
        let name = (attachConfiguration (withStatus "held")).Name
        name |> Expect.stringContains "test" "suite adds"
        name |> Expect.stringContains "ticket" "debug-4242-1"
    ]

    testList "beginning" [

      testCase "WHY - a held test whose code is compiled attaches with nothing to warn about" <| fun _ ->
        match beginStep (withStatus "held") with
        | BeginStep.AttachDebugger(config, notices) ->
          config.ProcessId |> Expect.equal "pid" 4242
          notices |> Expect.isEmpty "no warnings"
        | other -> failtestf "expected AttachDebugger, got %A" other

      testCase "WHY - a test an eval defined attaches, and says breakpoints in it will not bind" <| fun _ ->
        let held = { view with Status = "held"; Symbols = "eval"; SymbolsNote = "no debug symbols" }
        match beginStep held with
        | BeginStep.AttachDebugger(_, [ notice ]) ->
          notice.Severity |> Expect.equal "a warning, not a refusal" Severity.Warning
          notice.Text |> Expect.equal "the daemon's note" "no debug symbols"
        | other -> failtestf "expected AttachDebugger with one warning, got %A" other

      testCase "WHY - a blocked attach still tries, and warns with the reason" <| fun _ ->
        let held = { view with Status = "held"; Access = "blocked"; AccessNote = "ptrace_scope is 2" }
        match beginStep held with
        | BeginStep.AttachDebugger(_, [ notice ]) -> notice.Text |> Expect.stringContains "carries the reason" "ptrace_scope is 2"
        | other -> failtestf "expected AttachDebugger with one warning, got %A" other

      testCase "WHY - every refusal carries the daemon's own message, and the ones a person can fix are warnings" <| fun _ ->
        for status in [ "not_discovered"; "no_test_matched"; "ambiguous_test"; "hold_already_open" ] do
          match beginStep (withStatus status) with
          | BeginStep.Refuse notice ->
            notice.Text |> Expect.equal (sprintf "%s: the daemon's words" status) view.Message
            notice.Severity |> Expect.equal (sprintf "%s: fixable" status) Severity.Warning
          | other -> failtestf "%s should refuse, got %A" status other
        for status in [ "no_session"; "no_worker"; "host_unavailable"; "worker_failed"; "bad_request" ] do
          match beginStep (withStatus status) with
          | BeginStep.Refuse notice -> notice.Severity |> Expect.equal (sprintf "%s: an error" status) Severity.Error
          | other -> failtestf "%s should refuse, got %A" status other

      testCase "WHY - the debugger not attaching says which process and what to do next" <| fun _ ->
        let notice = attachFailedNotice (withStatus "held")
        notice.Text |> Expect.stringContains "the process" "4242"
        notice.Text |> Expect.stringContains "the test" "suite adds"
        notice.Text |> Expect.stringContains "where to look" "Debug Console"
    ]

    testList "continuing" [

      testCase "WHY - still running means ask again" <| fun _ ->
        continueStep "t" (withStatus "still_running") |> Expect.equal "keep waiting" ContinueStep.KeepWaiting

      testCase "WHY - a finished test says how it ended, with its failure message" <| fun _ ->
        let ended outcome detail = { view with Status = "attached"; Outcome = outcome; Detail = detail; DurationMs = 12.0 }
        match continueStep "suite adds" (ended "passed" "") with
        | ContinueStep.Finished notice ->
          notice.Severity |> Expect.equal "info" Severity.Info
          notice.Text |> Expect.stringContains "says passed" "passed"
        | other -> failtestf "expected Finished, got %A" other
        match continueStep "suite adds" (ended "failed" "expected 3 but got 4") with
        | ContinueStep.Finished notice ->
          notice.Severity |> Expect.equal "a failure is a warning" Severity.Warning
          notice.Text |> Expect.stringContains "carries the assertion" "expected 3 but got 4"
        | other -> failtestf "expected Finished, got %A" other

      testCase "WHY - a test that did not run, or a host that went away, ends the wait with the daemon's words" <| fun _ ->
        for status in [ "no_debugger_within"; "released_without_debugger"; "no_such_hold"; "host_lost"; "worker_failed" ] do
          match continueStep "t" (withStatus status) with
          | ContinueStep.Abandon notice -> notice.Text |> Expect.equal (sprintf "%s: the daemon's words" status) view.Message
          | other -> failtestf "%s should end the wait, got %A" status other

      testCase "WHY - no status ends the wait silently: every one but still_running either finishes or abandons" <| fun _ ->
        for status in daemonStatuses |> List.filter (fun s -> s <> "still_running") do
          match continueStep "t" { withStatus status with Outcome = "passed" } with
          | ContinueStep.KeepWaiting -> failtestf "%s must not keep waiting" status
          | ContinueStep.Finished _
          | ContinueStep.Abandon _ -> ()

      testCase "WHY - a session that ended under a running test says the test carries on, and none of this uses an em dash" <| fun _ ->
        (sessionEndedNotice "suite adds").Text |> Expect.stringContains "says it finishes" "finishes it"
        (unreachableNotice "ECONNREFUSED").Text |> Expect.stringContains "carries the detail" "ECONNREFUSED"
        [ (sessionEndedNotice "t").Text; (unreachableNotice "x").Text; (attachFailedNotice view).Text; missingDebuggerNotice.Text ]
        |> List.exists (fun text -> text.Contains "—")
        |> Expect.isFalse "no em dash"
    ]

    testList "where the Debug action appears" [

      testCase "WHY - only a failed or errored test offers Debug beside its result" <| fun _ ->
        let offered =
          [ VscTestOutcome.Passed
            VscTestOutcome.Failed "x"
            VscTestOutcome.Skipped "x"
            VscTestOutcome.Running
            VscTestOutcome.Errored "x"
            VscTestOutcome.Stale
            VscTestOutcome.PolicyDisabled
            VscTestOutcome.NotYetRun ]
          |> List.filter (fun o -> debugOfferFor o = DebugOffer.Offered)
        offered |> Expect.equal "exactly the two failures" [ VscTestOutcome.Failed "x"; VscTestOutcome.Errored "x" ]

      testCase "WHY - the hover link runs the debug command with the test id as the one argument" <| fun _ ->
        let link = debugCommandLink "AB12"
        link |> Expect.stringContains "the command" "command:sagefs.debugTest?"
        let encoded = link.Substring(link.IndexOf '?' + 1).TrimEnd(')')
        Uri.UnescapeDataString encoded |> Expect.equal "a JSON array of the id" "[\"AB12\"]"

      testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 } "WHY - whatever the id holds, the link's argument is that id and nothing else"
      <| fun (id: NonEmptyString) ->
        let link = debugCommandLink id.Get
        let encoded = link.Substring(link.IndexOf '?' + 1).TrimEnd(')')
        let argument = JsonDocument.Parse(Uri.UnescapeDataString encoded).RootElement
        argument.GetArrayLength() = 1 && argument.[0].GetString() = id.Get

      testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 } "WHY - a failure message reads as written in the hover: every markdown character is escaped"
      <| fun (text: NonEmptyString) ->
        let escaped = escapeMarkdown text.Get
        // Un-escaping the escapes gives the text back, so nothing was lost or changed besides the backslashes.
        Regex.Replace(escaped, "\\\\([\\\\`*_\\[\\]()<>#|~])", "$1") = text.Get

      testCase "WHY - a message cannot smuggle a link into the hover" <| fun _ ->
        let hover = hoverWithDebugLink "[click](command:evil)" "AB12"
        hover |> Expect.stringContains "the message is escaped" "\\[click\\]\\(command:evil\\)"
        hover |> Expect.stringContains "our own link is intact" "[Debug this test](command:sagefs.debugTest?"
    ]

    testList "wiring (sources read as data)" [

      let pkg = JsonDocument.Parse(read "package.json").RootElement
      let contributes = pkg.GetProperty "contributes"
      let commandIds =
        contributes.GetProperty("commands").EnumerateArray()
        |> Seq.map (fun c -> c.GetProperty("command").GetString())
        |> Set.ofSeq
      let hiddenFromPalette =
        contributes.GetProperty("menus").GetProperty("commandPalette").EnumerateArray()
        |> Seq.filter (fun m -> match m.TryGetProperty "when" with | true, w -> w.GetString() = "false" | _ -> false)
        |> Seq.map (fun m -> m.GetProperty("command").GetString())
        |> Set.ofSeq

      testCase "WHY - the debug command is contributed, and hidden from the palette because it needs a test to act on" <| fun _ ->
        commandIds |> Expect.contains "contributed" DebugCommandId
        hiddenFromPalette |> Expect.contains "no palette entry that can only fail" DebugCommandId

      testCase "WHY - the extension registers the command the lens, the hover and the profile name" <| fun _ ->
        read "src/Extension.fs" |> Expect.stringContains "registered" (sprintf "reg \"%s\"" DebugCommandId)

      testCase "WHY - the per-test CodeLens offers Debug, through the command and title this module owns" <| fun _ ->
        let source = read "src/TestCodeLensProvider.fs"
        source |> Expect.stringContains "uses the command id" "DebugCommandId"
        source |> Expect.stringContains "uses the lens title" "DebugLensTitle"
        source |> Expect.stringContains "only where the outcome offers it" "debugOfferFor"

      testCase "WHY - the Test Explorer has a Debug run profile, which is what puts Debug Test on the gutter glyph" <| fun _ ->
        read "src/TestControllerAdapter.fs" |> Expect.stringContains "a debug profile" "TestRunProfileKind.Debug"

      testCase "WHY - a failing test's gutter hover carries the Debug link" <| fun _ ->
        read "src/TestDecorations.fs" |> Expect.stringContains "the hover link" "hoverWithDebugLink"

      testCase "WHY - the command attaches with the configuration this module builds, and checks for a debugger first" <| fun _ ->
        let source = read "src/TestDebugCommand.fs"
        source |> Expect.stringContains "checks for a debugger" "chooseDebugger"
        source |> Expect.stringContains "attaches with the pure configuration" "BeginStep.AttachDebugger(configuration"
        source |> Expect.stringContains "decides each step purely" "beginStep"
        source |> Expect.stringContains "decides each step purely" "continueStep"
    ]
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
