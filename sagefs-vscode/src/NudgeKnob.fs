module SageFs.Vscode.NudgeKnob

open Fable.Core
open Fable.Core.JsInterop
open Vscode
open SageFs.Vscode.JsHelpers
open SageFs.Vscode.SafeInterop

module KnobPure = SageFs.Vscode.NudgeKnobPure
module Client = SageFs.Vscode.SageFsClient

// The editing half of the value knob (roadmap: "Alt-drag in VS Code … both writing through the
// nudge door"). VS Code does not expose a drag: `TextEditorSelectionChangeEvent` carries only
// the editor, the selections and a `kind` — no modifiers, no mouse position, no mouse-up — and
// the typings have no editor mouse event at all. What an Alt+drag DOES produce is a selection
// change of kind Mouse with a multi-cursor in it, because under `editor.multiCursorModifier =
// "alt"` (VS Code's default) the Alt+click that starts the drag adds a cursor. That fingerprint
// is the gesture decoded in `NudgeKnobPure.decide`; this file is only the shell: facts out of
// the event, knobs from `nudge_value inspect`, one journaled write per settle through
// `nudge_value set`, and the door's own refusals shown as they come.

/// Quiet time after the last selection event before the pending steps are written — the
/// dashboard knob's "one write per settle".
[<Literal>]
let private settleMs = 250

let mutable private scrub = KnobPure.ScrubState.Idle
/// Absent = not inspected yet; present `[]` = inspected and holds no nudgeable value.
let mutable private knobsByFile: Map<string, KnobPure.Knob list> = Map.empty
let mutable private inspecting: Set<string> = Set.empty
/// The last selection event per file, so a gesture that started before the inspect landed can
/// be replayed against the knobs the inspect just brought in.
let mutable private latestFacts: Map<string, KnobPure.Facts> = Map.empty
let mutable private settleTimer: obj option = None
let mutable private writing = false
let mutable private modifierWarned = false

/// One `nudge_value` call routed the way this window's session is bound.
let private callFor (action: string) (file: string) (address: string) (seen: string) (literal: string) (getSession: unit -> string option * string option) : Client.NudgeCall =
  let sessionId, workingDirectory = getSession ()
  { Action = action
    File = file
    Address = address
    Seen = seen
    Literal = literal
    Expression = ""
    SessionId = sessionId
    WorkingDirectory = workingDirectory }

let private shortName (path: string) =
  let parts = path.Split([| '/'; '\\' |])
  match parts.Length with
  | 0 -> path
  | _ -> parts.[parts.Length - 1]

/// Carry a `Written` reply into what the next gesture will be seen against: the new text, the
/// `hashAfter` the next set is checked with, and the span's end moved by the text's growth —
/// `NudgeKnobPure.afterWrite` is the decision, this is only the map update.
let private applyWritten (file: string) (address: string) (before: string) (after: string) (hashAfter: string) (notes: string list) (log: string -> unit) =
  match Map.tryFind file knobsByFile with
  | None -> ()
  | Some knobs ->
    knobsByFile <-
      Map.add file
        (knobs
         |> List.map (fun k -> if k.Address = address then KnobPure.afterWrite k address after hashAfter else k))
        knobsByFile
  log (sprintf "[knob] %s %s: %s → %s" (shortName file) address before after)
  notes |> List.iter (fun note -> log (sprintf "[knob] note: %s" note))

let private reportRefusal (context: string) (rule: string) (nextAction: string) (log: string -> unit) =
  log (sprintf "[knob] %s refused: %s %s" context rule nextAction)

/// One settle, written: the literal comes from the knob's own shape (the dashboard's step
/// arithmetic), the write is `nudge_value set` with the hash the knob last showed. Nothing here
/// decides whether the write is ALLOWED — the door does, and its answer is shown as it is.
let private doWrite
  (log: string -> unit)
  (getClient: unit -> Client.Client option)
  (getSession: unit -> string option * string option)
  (knob: KnobPure.Knob)
  (steps: int)
  =
  match KnobPure.literalAfter knob steps with
  | Result.Error _ ->
    log (sprintf "[knob] %d steps from %s would leave the numbers a literal can spell; nothing written" steps knob.Text)
  | Result.Ok literal ->
    match getClient () with
    | None -> ()
    | Some c ->
      writing <- true
      Client.nudgeValue (callFor "set" knob.File knob.Address knob.Hash literal getSession) c
      |> Promise.map (fun reply ->
        writing <- false
        match reply with
        | Result.Ok (Client.NudgeWritten (file, address, before, after, hashAfter, notes)) ->
          applyWritten file address before after hashAfter notes log
        | Result.Ok (Client.NudgeRefused (_, rule, nextAction)) ->
          // The door said no with its rule and next action. Drop what this file was inspected
          // as: the next gesture re-inspects and starts from what is true.
          knobsByFile <- Map.remove knob.File knobsByFile
          reportRefusal (shortName knob.File) rule nextAction log
          Window.showErrorMessage (sprintf "SageFs: %s %s" rule nextAction) [||] |> ignore
        | Result.Ok (Client.NudgeUnchanged (address, text)) ->
          log (sprintf "[knob] %s is already %s; nothing written" address text)
        | Result.Ok other ->
          log (sprintf "[knob] unexpected reply to set: %A" other)
        | Result.Error err ->
          log (sprintf "[knob] cannot reach nudge_value: %s" err))
      |> Promise.catch (fun err ->
        writing <- false
        log (sprintf "[knob] cannot reach nudge_value: %O" err))
      |> promiseIgnoreLog log

let rec private flushNow
  (log: string -> unit)
  (getClient: unit -> Client.Client option)
  (getSession: unit -> string option * string option)
  ()
  =
  let settled, work = KnobPure.flush scrub
  scrub <- settled
  match work with
  | None -> ()
  | Some (knob, steps) -> doWrite log getClient getSession knob steps

and private armSettle
  (log: string -> unit)
  (getClient: unit -> Client.Client option)
  (getSession: unit -> string option * string option)
  =
  settleTimer |> Option.iter jsClearTimeout
  settleTimer <-
    Some (
      jsSetTimeout
        (fun () ->
          settleTimer <- None
          match writing with
          // A write is still in the air: hold the steps a moment rather than race the door.
          | true -> armSettle log getClient getSession
          | false -> flushNow log getClient getSession ())
        settleMs)

/// Re-run the event that asked for the inspect now that the file's knobs are known.
let private replayLatest
  (log: string -> unit)
  (getClient: unit -> Client.Client option)
  (getSession: unit -> string option * string option)
  (file: string)
  ()
  =
  match Map.tryFind file latestFacts with
  | None -> ()
  | Some facts ->
    let knobs = Map.tryFind file knobsByFile |> Option.defaultValue []
    scrub <- KnobPure.decide scrub { facts with KnobPure.Facts.Knobs = knobs }
    if KnobPure.isTracking scrub then armSettle log getClient getSession

/// `nudge_value inspect` for one file: the knob store is the door's own listing of what can be
/// nudged where, refreshed by the door — never guessed from the document text.
let private startInspect
  (log: string -> unit)
  (getClient: unit -> Client.Client option)
  (getSession: unit -> string option * string option)
  (file: string)
  =
  match getClient () with
  | None -> ()
  | Some c ->
    inspecting <- Set.add file inspecting
    Client.nudgeValue (callFor "inspect" file "" "" "" getSession) c
    |> Promise.map (fun reply ->
      inspecting <- Set.remove file inspecting
      match reply with
      | Result.Ok (Client.NudgeInspected (_, knobs, _, _)) ->
        knobsByFile <- Map.add file knobs knobsByFile
        log (sprintf "[knob] %d nudgeable value(s) in %s" knobs.Length (shortName file))
        replayLatest log getClient getSession file ()
      | Result.Ok (Client.NudgeRefused (token, rule, nextAction)) ->
        // A file outside the session's projects will never hold a knob: stop asking. Anything
        // else the door may be refusing (the session has no worker yet) stays unknown, so the
        // next gesture asks again rather than silently going dead.
        if token = "NotOwned" then knobsByFile <- Map.add file [] knobsByFile
        reportRefusal (shortName file) rule nextAction log
      | Result.Ok _ ->
        knobsByFile <- Map.add file [] knobsByFile
        replayLatest log getClient getSession file ()
      | Result.Error err ->
        inspecting <- Set.remove file inspecting
        log (sprintf "[knob] cannot reach nudge_value for %s: %s" (shortName file) err))
    |> Promise.catch (fun err ->
      inspecting <- Set.remove file inspecting
      log (sprintf "[knob] cannot reach nudge_value for %s: %O" (shortName file) err))
    |> promiseIgnoreLog log

/// The event reduced to the facts the decision takes. Nothing VS Code does not say: `kind` 2 is
/// `TextEditorSelectionChangeKind.Mouse`, and more than one selection on a mouse event is the
/// multi-cursor an Alt+click added under the default `editor.multiCursorModifier`.
let private factsOfEvent (e: obj) : KnobPure.Facts option =
  match fieldObj "textEditor" e with
  | None -> None
  | Some editor ->
    match fieldObj "document" editor with
    | None -> None
    | Some doc ->
      let file = defaultArg (fieldString "fileName" doc) ""
      let isMouse = (fieldInt "kind" e |> Option.defaultValue 0) = 2
      let positions =
        fieldArray "selections" e
        |> Option.defaultValue [||]
        |> Array.toList
        |> List.choose (fun selection ->
          match fieldObj "active" selection with
          | None -> None
          | Some active ->
            match fieldInt "line" active, fieldInt "character" active with
            | Some line, Some col -> Some (line, col)
            | _ -> None)
      Some
        { KnobPure.Facts.File = file
          IsMouse = isMouse
          IsAltGesture = isMouse && positions.Length > 1
          Positions = positions
          Knobs = Map.tryFind file knobsByFile |> Option.defaultValue [] }

/// Whether the Alt half of "Alt-drag" can be inferred at all: only when Alt is the modifier
/// that adds cursors. In the other setting (`ctrlCmd`) an Alt+drag is indistinguishable from a
/// plain drag in the selection event, so the knob stays disarmed rather than guess.
let private altDragArmed () : bool =
  let cfg = Workspace.getConfiguration "editor"
  cfg.get ("multiCursorModifier", "alt") = "alt"

/// Subscribe the gesture. Returns the listener's disposable (plus the settle timer's cleanup)
/// for `Extension.activate` to hold.
let register
  (getClient: unit -> Client.Client option)
  (getSession: unit -> string option * string option)
  (log: string -> unit)
  : Disposable =
  let listener =
    Window.onDidChangeTextEditorSelectionRaw windowExports (fun e ->
      try
        match factsOfEvent e with
        | None -> ()
        | Some facts ->
          if not (altDragArmed ()) then
            if not modifierWarned && facts.IsMouse then
              modifierWarned <- true
              log "[knob] Alt-drag scrubbing stays off: editor.multiCursorModifier is not \"alt\" (VS Code's default), so an Alt+drag cannot be told from a plain drag in a selection event."
          else
            latestFacts <- Map.add facts.File facts latestFacts
            scrub <- KnobPure.decide scrub facts
            if KnobPure.isTracking scrub then armSettle log getClient getSession
            match facts.IsAltGesture, Map.tryFind facts.File knobsByFile with
            | true, None when not (Set.contains facts.File inspecting) ->
              startInspect log getClient getSession facts.File
            | _ -> ()
      with ex ->
        log (sprintf "[knob] selection handler: %O" ex))

  { new Disposable with
      member _.dispose () =
        listener.dispose () |> ignore
        settleTimer |> Option.iter jsClearTimeout
        settleTimer <- None
        null }
