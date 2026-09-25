/// What a browser is actually TOLD about a save.
///
/// `ReloadOutcome` decides what happened; this is the layer that turns that
/// decision into the one event a page sees. It exists because the shipped bug
/// was not in the decision — it was in a surface re-deriving "should I refresh?"
/// from a method count of its own. Every assertion here is aimed at that:
/// nothing may reach a browser as a refresh unless the running process changed.
module SageFs.Tests.ReloadBroadcastTests

open System
open System.Threading
open Expecto
open Expecto.Flip
open SageFs.Features.RestartScope
open SageFs.DevReload
open SageFs.Features.ReloadOutcome

// `SageFs.Features` is deliberately NOT opened: it would make the bare name
// `ReloadOutcome` resolve to the sibling MODULE rather than the type, and the
// DU is RequireQualifiedAccess. Abbreviations instead — one meaning per name.
module Outcome = SageFs.Features.ReloadOutcome.ReloadOutcome
module Broadcast = SageFs.Features.ReloadBroadcast
module Planning = SageFs.Features.ReloadPlanning

let private allOutcomes =
  [ ReloadOutcome.Patched(1, 3)
    ReloadOutcome.Patched(3, 3)
    ReloadOutcome.NoEffect(3, [ RestartReason.StartupComputedValue "routes" ])
    ReloadOutcome.NoEffect(448, [])
    ReloadOutcome.Restarted [ RestartReason.SignatureChanged "Program.handle" ]
    ReloadOutcome.RestartRequired [ RestartReason.MutableModuleState "counter" ]
    ReloadOutcome.CompileFailed "FS0039: not defined" ]

let private allChanges =
  [ Planning.ReloadChange.TypeChanged "TodoItem"
    Planning.ReloadChange.ValueChanged "routes"
    Planning.ReloadChange.MutableStateChanged "counter"
    Planning.ReloadChange.SignatureChanged "handle"
    Planning.ReloadChange.EntryPointChanged
    Planning.ReloadChange.ModuleChanged "Views"
    Planning.ReloadChange.StartupCodeChanged
    Planning.ReloadChange.DeclarationRemoved "oldHelper"
    Planning.ReloadChange.DeclarationAdded "newHelper"
    Planning.ReloadChange.UsesNonPublicMember("render", "privateHelper") ]

/// Drain everything a client was sent. The channel is unbounded and every
/// broadcast in these tests is synchronous, so a single non-blocking drain
/// sees the whole conversation.
let private drain (reader: Channels.ChannelReader<DevReloadEvent>) =
  let mutable evt = Unchecked.defaultof<DevReloadEvent>
  let acc = ResizeArray<DevReloadEvent>()
  while reader.TryRead(&evt) do
    acc.Add evt
  List.ofSeq acc

let private withClient (body: Channels.ChannelReader<DevReloadEvent> -> unit) =
  let id = sprintf "rb-%s" (Guid.NewGuid().ToString("N").[..7])
  let reader = registerClient id
  try body reader
  finally unregisterClient id

[<Tests>]
let reloadBroadcastTests =
  testSequenced
  <| testList "ReloadBroadcast — what the browser is told" [

    // WHY — THE bug, as one executable assertion. Reintroduce the old
    // behaviour ("any method detoured ⇒ broadcast reload") anywhere between
    // ReloadOutcome and the SSE writer and this goes red.
    test "WHY — a save that reached nothing never reaches the browser as a refresh" {
      let outcome = ReloadOutcome.NoEffect(448, [ RestartReason.StartupComputedValue "routes" ])
      Broadcast.eventOf outcome
      |> DevReloadEvent.refreshes
      |> Expect.isFalse "refreshing into byte-identical code is the failure users read as 'the tool is broken'"
    }

    // WHY — the count is the whole point of Flutter's "Reloaded 1 of 448
    // libraries": "0 of 448" has to be visible as a non-event.
    test "WHY — a no-effect save carries its count all the way to the client" {
      match Broadcast.eventOf (ReloadOutcome.NoEffect(448, [])) with
      | DevReloadEvent.NotApplied report ->
        report.Considered |> Expect.equal "the count survives the trip to the wire" 448
        report.Patched |> Expect.equal "nothing was patched, and the number says so" 0
        report.Message |> Expect.stringContains "the user reads the count, not just a verdict" "0 of 448"
      | other -> failtestf "a no-effect save must be NotApplied, got %A" other
    }

    test "a partial patch is reported as partial, with both numbers" {
      match Broadcast.eventOf (ReloadOutcome.Patched(1, 3)) with
      | DevReloadEvent.Patched report ->
        report.Patched |> Expect.equal "patched count survives" 1
        report.Considered |> Expect.equal "considered count survives" 3
        report.Outcome |> Expect.equal "a client branches on the case, not on prose" "Patched"
        report.Message |> Expect.stringContains "the message says how partial it was" "1 of 3"
      | other -> failtestf "a patch must be Patched, got %A" other
    }

    test "an auto-restart is a refresh, and asks the user for nothing" {
      let outcome = ReloadOutcome.Restarted [ RestartReason.StartupComputedValue "routes" ]
      match Broadcast.eventOf outcome with
      | DevReloadEvent.Restarted report ->
        report.Outcome |> Expect.equal "the case is on the wire" "Restarted"
        report.Message |> Expect.stringContains "says what SageFs did" "Restarted the app"
        report.SuggestedAction
        |> Expect.equal "a restart SageFs performed asks the user to do nothing" ""
      | other -> failtestf "a restart must be Restarted, got %A" other
      Broadcast.eventOf outcome
      |> DevReloadEvent.refreshes
      |> Expect.isTrue "the process IS current after a restart, so the page must fetch it"
    }

    test "a restart SageFs cannot perform carries the remedy instead of claiming credit" {
      match Broadcast.eventOf (ReloadOutcome.RestartRequired [ RestartReason.MutableModuleState "counter" ]) with
      | DevReloadEvent.NotApplied report ->
        report.Outcome |> Expect.equal "never claims a restart that did not happen" "RestartRequired"
        report.Message |> Expect.stringContains "says what happened" "Restart needed"
        report.Message |> Expect.stringContains "the remedy survives in the field clients read" "→ "
        report.SuggestedAction
        |> Expect.stringContains "and in the field they should read" "Restart the app"
      | other -> failtestf "a required restart must be NotApplied, got %A" other
    }

    // WHY — a client that has to re-derive WHY a reload was refused will either
    // stay silent (what sagefs.nvim does today) or word it differently from
    // every other client. The refusals ship rendered.
    test "WHY — every refusal reaches the client already worded, with its remedy" {
      match Broadcast.eventOf (ReloadOutcome.NoEffect(2, [ RestartReason.StartupComputedValue "getStats" ])) with
      | DevReloadEvent.NotApplied report ->
        match report.Reasons with
        | [ refusal ] ->
          refusal.Case |> Expect.equal "a stable token to branch on" "StartupComputedValue"
          refusal.Message |> Expect.stringContains "named in the user's own code" "getStats"
          refusal.SuggestedAction
          |> Expect.stringContains "and what to do about it" "Restart the app"
        | other -> failtestf "the refusal must travel, got %A" other
      | other -> failtestf "a no-effect save must be NotApplied, got %A" other
    }

    // WHY — job #3's regression: a fail-closed refusal for a redirect that
    // reached SOME copy, with no proof it's the one the running app calls,
    // needs its own stable wire token so a client can render it distinctly
    // from every other refusal, and its remedy names the actual limitation
    // (no build baseline) rather than a generic restart instruction.
    test "WHY — an unverified copy carries its own stable case and an honest remedy" {
      match Broadcast.eventOf (ReloadOutcome.NoEffect(1, [ RestartReason.UnverifiedCopy "greeting" ])) with
      | DevReloadEvent.NotApplied report ->
        match report.Reasons with
        | [ refusal ] ->
          refusal.Case |> Expect.equal "a stable token to branch on" "UnverifiedCopy"
          refusal.Message |> Expect.stringContains "names the declaration" "greeting"
          refusal.SuggestedAction |> Expect.stringContains "and what to do about it" "Restart the app"
        | other -> failtestf "the refusal must travel, got %A" other
      | other -> failtestf "an unverified copy must be NotApplied, not a claimed reload, got %A" other
    }

    test "a compile failure keeps its summary and leaves the app serving the last good code" {
      match Broadcast.eventOf (ReloadOutcome.CompileFailed "FS0039: not defined") with
      | DevReloadEvent.CompilationFailed(summary, report, _) ->
        summary |> Expect.stringContains "the compiler's own words survive" "FS0039"
        report.Outcome |> Expect.equal "the case is on the wire" "CompileFailed"
        report.SuggestedAction |> Expect.stringContains "and what to do" "Fix the compile error"
      | other -> failtestf "a compile failure must be CompilationFailed, got %A" other
    }

    // WHY — one decision, one place. Every surface asking
    // `ReloadOutcome.shouldRefreshBrowser` and every surface reading the event
    // must agree, for every outcome, or the two can drift apart again.
    test "WHY — the event and the outcome can never disagree about refreshing" {
      for outcome in allOutcomes do
        DevReloadEvent.refreshes (Broadcast.eventOf outcome)
        |> Expect.equal
             (sprintf "the wire must agree with ReloadOutcome.shouldRefreshBrowser for %A" outcome)
             (Outcome.shouldRefreshBrowser outcome)
    }

    // WHY — belt and suspenders: even a caller that hand-builds "patched 0"
    // cannot put a refresh on the wire. The lie is impossible at the lowest
    // layer, not merely discouraged at the highest.
    test "WHY — broadcasting a patch of nothing is demoted, not trusted" {
      withClient (fun reader ->
        // A caller hand-building the lie the smart constructor forbids.
        broadcastPatched
          { ReloadReport.none with
              Outcome = "Patched"
              Patched = 0
              Considered = 5
              Message = "Hot reloaded 0 of 5 changed definition(s)" }
        match drain reader with
        | [ DevReloadEvent.NotApplied report ] ->
          report.Considered |> Expect.equal "the count is preserved through the demotion" 5
        | other -> failtestf "patched=0 must never reach a client as a refresh, got %A" other)
    }

    test "broadcastOutcome delivers exactly one terminal event per save" {
      withClient (fun reader ->
        Broadcast.broadcastOutcome (ReloadOutcome.Patched(2, 2))
        match drain reader with
        | [ DevReloadEvent.Patched report ] ->
          report.Patched |> Expect.equal "both numbers reach the client" 2
        | other -> failtestf "expected one Patched event, got %A" other)
    }

    test "broadcastOutcome of a no-effect save still closes the compiling overlay" {
      withClient (fun reader ->
        broadcastCompiling (Some "Handlers.fs")
        Broadcast.broadcastOutcome (ReloadOutcome.NoEffect(4, []))
        match drain reader with
        | [ DevReloadEvent.Compiling(Some "Handlers.fs"); DevReloadEvent.NotApplied report ] ->
          report.Considered |> Expect.equal "the count travels with the close" 4
        | other -> failtestf "every Compiling must be closed by a terminal event, got %A" other)
    }

    // WHY — a refusal a user cannot act on is a dead end. Every shape the
    // planner can produce must arrive with something to do about it, and it
    // must survive the trip to a client with both strings intact.
    test "WHY — every change the planner can refuse reaches a client with a remedy" {
      for change in allChanges do
        let outcome =
          ReloadOutcome.RestartRequired [ Planning.ReloadChange.restartReason change ]
        match Broadcast.eventOf outcome with
        | DevReloadEvent.NotApplied report ->
          match report.Reasons with
          | [ refusal ] ->
            refusal.Case.Length > 0
            |> Expect.isTrue (sprintf "%A must carry a token a client can branch on" change)
            refusal.Message.Length > 0
            |> Expect.isTrue (sprintf "%A must be described in the user's own terms" change)
            refusal.SuggestedAction.Length > 0
            |> Expect.isTrue (sprintf "%A must tell the user what to do" change)
          | other -> failtestf "%A must produce exactly one refusal, got %A" change other
        | other -> failtestf "%A must not be reported as a refresh, got %A" change other
    }

    test "a save that broke two ways reaches the client saying both" {
      let outcome =
        ReloadOutcome.RestartRequired
          (Planning.ReloadChange.restartReasons
            (Planning.ReloadChange.ValueChanged "routes")
            [ Planning.ReloadChange.TypeChanged "TodoItem" ])
      match Broadcast.eventOf outcome with
      | DevReloadEvent.NotApplied report ->
        report.Reasons |> List.map _.Case
        |> Expect.equal
             "both refusals travel, in the order the planner found them"
             [ "StartupComputedValue"; "TypeShapeChanged" ]
        report.Considered |> Expect.equal "one count per definition that did not land" 2
      | other -> failtestf "a required restart must be NotApplied, got %A" other
    }

    // WHY — a save whose declarations are identical to the running build is not
    // a reload and not a failure. Refreshing here is the byte-identical refresh
    // the whole subsystem exists to prevent.
    test "WHY — an unchanged save is reported as unchanged and never refreshes" {
      let evt = Broadcast.unchanged "Handlers.fs"
      evt |> DevReloadEvent.refreshes |> Expect.isFalse "nothing changed, so nothing must be fetched"
      match evt with
      | DevReloadEvent.NotApplied report ->
        report.Message |> Expect.stringContains "names the file the user saved" "Handlers.fs"
        report.SuggestedAction |> Expect.equal "there is nothing to do, so no remedy is invented" ""
      | other -> failtestf "an unchanged save must be NotApplied, got %A" other
    }

    // WHY — one wedged eval used to disable hot reload for every other file for
    // the rest of the session, with no log, no SSE and no signal at all. The
    // recovery must be LOUD, and it must say what happens next.
    test "WHY — a save that could not even reach the compiler says so" {
      let evt = Broadcast.compilerBusy "Handlers.fs" (TimeSpan.FromSeconds 60.0)
      evt |> DevReloadEvent.refreshes |> Expect.isFalse "nothing compiled, so nothing may be fetched"
      match evt with
      | DevReloadEvent.NotApplied report ->
        report.Outcome |> Expect.equal "a client can branch on it" "CompilerBusy"
        report.Message |> Expect.stringContains "names the file that was dropped" "Handlers.fs"
        report.Message |> Expect.stringContains "says how long it waited" "60"
        report.SuggestedAction |> Expect.stringContains "says what to do about it" "Save again"
      | other -> failtestf "a busy compiler must be NotApplied, got %A" other
    }

    test "an eval that ran out of its budget is reported, not swallowed" {
      let evt = Broadcast.evalTimedOut "Handlers.fs" (TimeSpan.FromMinutes 5.0)
      evt |> DevReloadEvent.refreshes |> Expect.isFalse "an unfinished eval changed nothing"
      match evt with
      | DevReloadEvent.NotApplied report ->
        report.Outcome |> Expect.equal "a client can branch on it" "EvalTimedOut"
        report.Message |> Expect.stringContains "names the file" "Handlers.fs"
        report.SuggestedAction |> Expect.stringContains "says what to do about it" "Save again"
      | other -> failtestf "a timed-out eval must be NotApplied, got %A" other
    }

    // WHY — a lost save used to be silent: the overflow handler reset the
    // session and said nothing, so a user who saved mid-overflow saw no
    // outcome at all. "Nothing silent" is the hot reload rule, and it has to
    // hold on the path that exists BECAUSE SageFs lost track of what changed.
    test "WHY — a watch-buffer overflow is reported, not swallowed by a silent reset" {
      let evt = Broadcast.watcherOverflow "/repo/SomeProject"
      evt |> DevReloadEvent.refreshes |> Expect.isFalse "a reset alone is not a patch the browser should fetch"
      match evt with
      | DevReloadEvent.NotApplied report ->
        report.Outcome |> Expect.equal "a client can branch on it" "WatcherOverflow"
        report.Message |> Expect.stringContains "names the directory that overflowed" "/repo/SomeProject"
        report.SuggestedAction |> Expect.stringContains "tells the user what to do" "save"
      | other -> failtestf "a watch overflow must be NotApplied, got %A" other
    }

    // WHY — the SSE payload is the actual contract with the page. A refresh
    // must be a refresh on the wire and a non-event must not be.
    test "WHY — the wire distinguishes a refresh from a non-event" {
      DevReloadEvent.sseData (Broadcast.eventOf (ReloadOutcome.Patched(1, 3)))
      |> Expect.stringContains "a patch is the page's cue to refetch" "\"type\":\"reload\""
      (DevReloadEvent.sseData (Broadcast.eventOf (ReloadOutcome.NoEffect(3, [])))).Contains "\"type\":\"reload\""
      |> Expect.isFalse "a non-event must never carry the refresh cue"
      DevReloadEvent.sseData (Broadcast.eventOf (ReloadOutcome.Restarted []))
      |> Expect.stringContains "a restart has its own cue: wait for the app, then refetch" "\"type\":\"restarted\""
    }

    // WHY — a client that only gets `{ fileReloaded, sessionId, elapsed_ms }`
    // is structurally unable to say anything useful; sagefs.nvim's handler is
    // silent for exactly that reason. The payload must carry the outcome.
    test "WHY — the payload lets a non-browser client render the outcome verbatim" {
      let outcome = ReloadOutcome.NoEffect(448, [ RestartReason.StartupComputedValue "routes" ])
      let data = DevReloadEvent.sseData (Broadcast.eventOf outcome)
      let json = System.Text.Json.JsonDocument.Parse(data.Substring(6, data.Length - 8)).RootElement
      json.GetProperty("outcome").GetString() |> Expect.equal "the case name is on the wire" "NoEffect"
      json.GetProperty("patched").GetInt32() |> Expect.equal "the numerator is on the wire" 0
      json.GetProperty("considered").GetInt32() |> Expect.equal "the denominator is on the wire" 448
      json.GetProperty("message").GetString()
      |> Expect.equal "rendered once, server-side, so every client words it identically" (Outcome.describeForUser outcome)
      json.GetProperty("suggestedAction").GetString()
      |> Expect.stringContains "under the name the rest of SageFs's structured errors already use" "Restart the app"
      let reasons = json.GetProperty("reasons")
      reasons.GetArrayLength() |> Expect.equal "the refusals travel too" 1
      reasons.[0].GetProperty("case").GetString()
      |> Expect.equal "each refusal carries a stable token" "StartupComputedValue"
      reasons.[0].GetProperty("suggestedAction").GetString().Length > 0
      |> Expect.isTrue "and its own remedy"
    }

    test "every SSE payload is a single well-formed data frame" {
      let events =
        [ DevReloadEvent.Compiling None
          DevReloadEvent.Compiling(Some "A\"B.fs")
          Broadcast.eventOf (ReloadOutcome.Patched(1, 2))
          Broadcast.eventOf (ReloadOutcome.Restarted [ RestartReason.TypeShapeChanged("T", SageFs.Features.RestartScope.Everything) ])
          Broadcast.unchanged "A.fs"
          Broadcast.eventOf (ReloadOutcome.NoEffect(2, [ RestartReason.MutableModuleState "x" ]))
          Broadcast.eventOf (ReloadOutcome.CompileFailed "boom\nsecond line") ]
      for evt in events do
        let data = DevReloadEvent.sseData evt
        data |> Expect.stringStarts (sprintf "%A must be an SSE data frame" evt) "data: "
        data |> Expect.stringEnds (sprintf "%A must terminate its frame" evt) "\n\n"
        // An embedded newline inside the JSON would split the frame in two and
        // the page would parse half a message.
        let json = data.Substring(6, data.Length - 8)
        json.Contains "\n" |> Expect.isFalse (sprintf "%A must not break its own frame" evt)
        let parsed = System.Text.Json.JsonDocument.Parse json
        (parsed.RootElement.GetProperty("type").GetString()).Length > 0
        |> Expect.isTrue (sprintf "%A must name its type" evt)
    }
  ]
