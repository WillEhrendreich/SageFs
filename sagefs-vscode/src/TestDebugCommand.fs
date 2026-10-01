module SageFs.Vscode.TestDebugCommand

// Debug one test: ask the daemon to hold it in the test host, attach a .NET debugger to the host, release the test, and
// detach when it finishes. Every decision (is there a debugger, what to attach to, what to tell the person, when to stop
// waiting) is TestDebugPure's, tested under plain `dotnet fsi`; this file only performs them.
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
  | Debugging of testId: string

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

/// Release the test and wait for it, asking again for as long as it runs under the debugger.
let rec private waitForTest (c: Client.Client) (view: DebugAnswerView) (state: SessionState ref) : JS.Promise<unit> =
  promise {
    let! answer = Client.debugContinue view.Ticket c
    match answer with
    | Result.Error detail ->
      do! detach state
      say (unreachableNotice detail)
    | Result.Ok progress ->
      match continueStep view.TestName progress with
      | ContinueStep.KeepWaiting ->
        match state.Value with
        | SessionState.Ended -> say (sessionEndedNotice view.TestName)
        | SessionState.NotStarted
        | SessionState.Started _ -> return! waitForTest c view state
      | ContinueStep.Finished notice
      | ContinueStep.Abandon notice ->
        do! detach state
        say notice
  }

let private attachAndWait (c: Client.Client) (view: DebugAnswerView) (configuration: AttachConfiguration) : JS.Promise<unit> =
  promise {
    let state = ref SessionState.NotStarted
    let started =
      Debug.onDidStartDebugSession (fun session ->
        match session.name = configuration.Name with
        | true -> state.Value <- SessionState.Started session
        | false -> ())
    let ended =
      Debug.onDidTerminateDebugSession (fun session ->
        match session.name = configuration.Name with
        | true -> state.Value <- SessionState.Ended
        | false -> ())
    try
      let options =
        createObj [
          "type" ==> configuration.Type
          "request" ==> configuration.Request
          "name" ==> configuration.Name
          "processId" ==> configuration.ProcessId
        ]
      let! attached = Debug.startDebugging None options
      match attached with
      | true -> do! waitForTest c view state
      | false -> say (attachFailedNotice view)
    finally
      started.dispose () |> ignore
      ended.dispose () |> ignore
  }

/// What a debug run does once it has the daemon's answer to "hold this test".
let private begin' (c: Client.Client) (testId: string) : JS.Promise<unit> =
  promise {
    match chooseDebugger (Extensions.all () |> Array.map debuggersOf |> Array.toList) with
    | DebuggerAvailability.Missing -> offerInstall ()
    | DebuggerAvailability.Installed _ ->
      let! answer = Client.debugTest testId c
      match answer with
      | Result.Error detail -> say (unreachableNotice detail)
      | Result.Ok view ->
        match beginStep view with
        | BeginStep.Refuse notice -> say notice
        | BeginStep.AttachDebugger(configuration, notices) ->
          notices |> List.iter say
          do! attachAndWait c view configuration
  }

/// Debug the test with this id (a test id the daemon reported, which is what the lens, the hover and the Test Explorer carry).
let debugTest (c: Client.Client) (testId: string) : JS.Promise<unit> =
  match run with
  | Run.Debugging _ ->
    say { Severity = Severity.Warning; Text = "A test is already being debugged. Stop that debug session first." }
    Promise.lift ()
  | Run.Idle ->
    run <- Run.Debugging testId
    begin' c testId
    |> Promise.map (fun () -> run <- Run.Idle)
    |> Promise.catch (fun err ->
      run <- Run.Idle
      say (unreachableNotice (string err)))
