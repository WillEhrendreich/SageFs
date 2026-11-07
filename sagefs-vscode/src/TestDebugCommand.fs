module SageFs.Vscode.TestDebugCommand

// Debug tests: ask the daemon to hold a test in the test host, attach a .NET debugger to the host, release the test, and
// detach when it finishes. Several tests go one after another under that one attach: the debugger stays on the host, so the
// next test is held and released at once. Every decision (is there a debugger, what to attach to, what to tell the person,
// when to stop waiting, whether the batch goes on) is TestDebugPure's, tested under plain `dotnet fsi`; this file only
// performs them.
//
// A notification's promise settles when the person dismisses it, which can be long after the run is over, so nothing here
// waits on one except to read a button press, and then only after the run has been released.

open Fable.Core
open Fable.Core.JsInterop
open Vscode
open SageFs.Vscode.SafeInterop
open SageFs.Vscode.TestDebugPure

module Client = SageFs.Vscode.SageFsClient

/// One debug run at a time: the test host holds one test at a time, and two debug sessions over one host would only
/// confuse each other.
[<RequireQualifiedAccess>]
type private Run =
  | Idle
  | Debugging of testIds: string list

let mutable private run = Run.Idle

/// Where the debug session this run started stands.
[<RequireQualifiedAccess>]
type private SessionState =
  | NotStarted
  | Started of DebugSession
  | Ended

/// The button on the missing-debugger message.
let private installText = "Install C# extension"

/// Say something. Not awaited: see the header.
let private say (notice: Notice) : unit =
  match notice.Severity with
  | Severity.Info -> Window.showInformationMessage notice.Text [||] |> ignore
  | Severity.Warning -> Window.showWarningMessage notice.Text [||] |> ignore
  | Severity.Error -> Window.showErrorMessage notice.Text [||] |> ignore

/// The debug types an extension contributes, read from its package.json.
let private debuggersOf (extension: Extension) : ExtensionDebuggers =
  let types =
    fieldObj "contributes" extension.packageJSON
    |> Option.bind (fieldArray "debuggers")
    |> Option.map (Array.choose (fieldString "type") >> Array.toList)
    |> Option.defaultValue []
  { ExtensionId = extension.id; DebuggerTypes = types }

/// Offer to install the recommended debugger, the way the Extensions view's Install button does.
let private offerInstall () : unit =
  Window.showErrorMessage missingDebuggerNotice.Text [| installText |]
  |> Promise.map (fun choice ->
    match choice with
    | Some text when text = installText ->
      Commands.executeCommandWith "workbench.extensions.installExtension" (box RecommendedExtensionId) |> ignore
    | _ -> ())
  |> ignore

/// Detach: stopping an attach session leaves the test host running.
let private detach (state: SessionState ref) : JS.Promise<unit> =
  match state.Value with
  | SessionState.Started session -> Debug.stopDebugging session
  | SessionState.NotStarted
  | SessionState.Ended -> Promise.lift ()

/// How the wait for one released test came out.
[<RequireQualifiedAccess>]
type private Waited =
  /// The daemon's last answer: the test ended, or the hold did.
  | Answer of DebugAnswerView
  /// The daemon could not be reached.
  | Unreachable of detail: string
  /// The debug session ended while the test was still running.
  | SessionEndedFirst

/// Release the test and wait for it, asking again for as long as it runs under the debugger.
let rec private waitForTest (c: Client.Client) (sessionId: string) (view: DebugAnswerView) (state: SessionState ref) : JS.Promise<Waited> =
  promise {
    let! answer = Client.debugContinue view.Ticket sessionId c
    match answer with
    | Result.Error detail -> return Waited.Unreachable detail
    | Result.Ok progress ->
      match continueStep view.TestName progress with
      | ContinueStep.KeepWaiting ->
        match state.Value with
        | SessionState.Ended -> return Waited.SessionEndedFirst
        | SessionState.NotStarted
        | SessionState.Started _ -> return! waitForTest c sessionId view state
      | ContinueStep.Finished _
      | ContinueStep.Abandon _ -> return Waited.Answer progress
  }

/// The debug configuration VS Code is asked to start.
let private optionsOf (configuration: AttachConfiguration) : obj =
  createObj [
    "type" ==> configuration.Type
    "request" ==> configuration.Request
    "name" ==> configuration.Name
    "processId" ==> configuration.ProcessId
  ]

/// Debug these tests one after another under one attach, then detach. A batch of one says what happened to that test in the
/// words it always has; a longer one says one sentence at the end.
let private holdAndAttachAll (c: Client.Client) (sessionId: string) (ids: string list) : JS.Promise<unit> =
  promise {
    let total = ids.Length
    let state = ref SessionState.NotStarted
    let attachedName = ref ""
    let attached = ref false
    let ended : TestEnded list ref = ref []
    let startedWatch =
      Debug.onDidStartDebugSession (fun session ->
        match attachedName.Value <> "" && session.name = attachedName.Value with
        | true -> state.Value <- SessionState.Started session
        | false -> ())
    let endedWatch =
      Debug.onDidTerminateDebugSession (fun session ->
        match attachedName.Value <> "" && session.name = attachedName.Value with
        | true -> state.Value <- SessionState.Ended
        | false -> ())
    /// Release the held test and take what comes back. Returns whether the batch goes on.
    let release (view: DebugAnswerView) : JS.Promise<bool> =
      promise {
        let! waited = waitForTest c sessionId view state
        match waited with
        | Waited.Unreachable detail ->
          say (unreachableNotice detail)
          return false
        | Waited.SessionEndedFirst ->
          say (sessionEndedNotice view.TestName)
          return false
        | Waited.Answer answer ->
          match continueStep view.TestName answer with
          | ContinueStep.Finished notice ->
            ended.Value <- ended.Value @ [ endedOf answer ]
            match total with
            | 1 -> say notice
            | _ -> ()
            return afterTest answer = AfterTest.GoOn
          | ContinueStep.Abandon notice ->
            say notice
            return false
          | ContinueStep.KeepWaiting -> return false
      }
    let rec step (remaining: string list) : JS.Promise<unit> =
      promise {
        match remaining, state.Value with
        | [], _ -> ()
        | _ :: _, SessionState.Ended ->
          say { Severity = Severity.Warning; Text = sprintf "The debug session ended, so %d tests were not run." remaining.Length }
        | testId :: rest, _ ->
          let! answer = Client.debugTest testId sessionId c
          match answer with
          | Result.Error detail -> say (unreachableNotice detail)
          | Result.Ok view ->
            let session = match attached.Value with | true -> BatchSession.Attached | false -> BatchSession.NotAttachedYet
            match afterHold session view with
            | AfterHold.StopBatch notice -> say notice
            | AfterHold.SkipThisTest notice ->
              say notice
              do! step rest
            | AfterHold.ReleaseNow notices ->
              notices |> List.iter say
              let! goOn = release view
              match goOn with
              | true -> do! step rest
              | false -> ()
            | AfterHold.AttachThenRelease(configuration, notices) ->
              notices |> List.iter say
              attachedName.Value <- configuration.Name
              let! started = Debug.startDebugging None (optionsOf configuration)
              match started with
              | false -> say (attachFailedNotice view)
              | true ->
                attached.Value <- true
                let! goOn = release view
                match goOn with
                | true -> do! step rest
                | false -> ()
      }
    try
      do! step ids
    finally
      startedWatch.dispose () |> ignore
      endedWatch.dispose () |> ignore
    do! detach state
    match total with
    | 1 -> ()
    | _ -> say (batchSummary (tally total ended.Value))
  }

/// Run `work` with live testing paused for the session when it was running, and put it back on every way out: the tests
/// finishing, a refusal, a detach, a debugger that never attached, a thrown error. A pause the person set is left alone.
let private whilePaused (c: Client.Client) (sessionId: string) (work: unit -> JS.Promise<unit>) : JS.Promise<unit> =
  promise {
    let! prior = Client.getLiveTestingPause sessionId c
    match pausePlan prior with
    | PausePlan.LeaveAlone -> do! work ()
    | PausePlan.PauseThenResume ->
      let! paused = Client.pauseLiveTesting sessionId c
      match paused with
      | Client.Failed _ -> do! work ()
      | Client.Succeeded _ ->
        let resume () = Client.resumeLiveTesting sessionId c |> Promise.map ignore
        let! outcome = work () |> Promise.map Result.Ok |> Promise.catch Result.Error
        do! resume ()
        match outcome with
        | Result.Ok () -> ()
        | Result.Error err -> raise err
  }

let private begin' (c: Client.Client) (sessionId: string) (ids: string list) : JS.Promise<unit> =
  promise {
    match chooseDebugger (Extensions.all () |> Array.map debuggersOf |> Array.toList) with
    | DebuggerAvailability.Missing -> offerInstall ()
    | DebuggerAvailability.Installed _ -> do! whilePaused c sessionId (fun () -> holdAndAttachAll c sessionId ids)
  }

/// Debug the tests with these ids (test ids the daemon reported, which is what the lens, the hover and the Test Explorer
/// carry), in the session this window shows, one after another under one debugger attach.
let debugTests (c: Client.Client) (sessionId: string) (testIds: string list) : JS.Promise<unit> =
  match batchOf testIds, run with
  | [], _ -> Promise.lift ()
  | _, Run.Debugging _ ->
    say { Severity = Severity.Warning; Text = "A test is already being debugged. Stop that debug session first." }
    Promise.lift ()
  | ids, Run.Idle ->
    run <- Run.Debugging ids
    begin' c sessionId ids
    |> Promise.map (fun () -> run <- Run.Idle)
    |> Promise.catch (fun err ->
      run <- Run.Idle
      say (unreachableNotice (string err)))
