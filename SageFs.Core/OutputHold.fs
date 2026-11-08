namespace SageFs

open System.Threading

/// Holds a clipped reply's whole text in the session, so the notice's expression can read what was cut.
[<RequireQualifiedAccess>]
module OutputHold =

  /// Bind `whole` under `Features.EvalResultSummary.OutputBinding` in `session`. True when the session now holds it. The echo
  /// of the binding is recorded and dropped: it is not the eval's output, and it must not turn up as output written between calls.
  let bind (session: FsiSession.IFsiSession) (recorder: TextWriterRecorder) (token: CancellationToken) (whole: string) : bool =
    recorder.StartRecording()
    let held = session.Eval(Features.EvalResultSummary.bindingCode whole, token)
    recorder.StopRecording() |> ignore
    held.Outcome = FsiSession.FsiSucceeded
