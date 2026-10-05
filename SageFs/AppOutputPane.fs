/// The app output pane's decision: which of a running app's stdout and stderr lines the dashboard shows, given what the
/// person using it asked for.
///
/// WHY A PURE MODULE. The pane has three things a user drives (follow, pause, a search string) and one stream of lines
/// the daemon pushes. Every interesting question is "what does the pane show for these lines, given those settings",
/// which is a function of its arguments with no clock, no buffer and no HTML in it, so a test and a REPL eval call it
/// the same way. The renderer (`AppOutputView`) draws what this decides.
///
/// WHY NOT JUST APPEND. An app that prints continuously pushes lines faster than anyone reads, so a pane that appends
/// is worse than no pane: it buries the evals it was meant to stop burying, and its memory grows for the life of the
/// daemon. So the decision is "which lines survive this tick", and the answer depends on whether the user is
/// following, has paused, and what they are filtering on.
///
/// WHAT THE APP'S OUTPUT IS. The daemon already receives it: a worker's stdout and stderr lines arrive through
/// `OnAppOutput`, batched per session and flushed every 150 ms (DaemonMode.fs, #88). Nothing here receives anything;
/// a caller feeds lines in and reads a pane out.
module SageFs.Server.AppOutputPane

open System

// ── what a line is ───────────────────────────────────────────────────────────────────────────────────────────────────

/// Which stream a line came from. Not decoration: an app's stderr is the one you are usually looking for, and
/// reading the two as one undifferentiated stream is how a stack trace ends up rendered as ordinary output.
[<RequireQualifiedAccess>]
type OutputStream =
  | Stdout
  | Stderr

/// One line of a running app's output.
type AppOutputLine =
  { Stream: OutputStream
    Text: string }

[<RequireQualifiedAccess>]
module AppOutputLine =
  /// A line, or nothing. A blank line is a real event in the app's world but carries nothing for a reader, and
  /// keeping them is how a pane's row count stops meaning anything. So the drop happens HERE, at the edge, where
  /// the reason is obvious, rather than in the renderer where it would be a filter nobody would think to question.
  let ofText (stream: OutputStream) (text: string) : AppOutputLine option =
    if String.IsNullOrWhiteSpace text then
      None
    else
      Some { Stream = stream; Text = text.TrimEnd('\r', '\n') }

  /// The line as CSS would want it, so the renderer keeps one naming rule.
  let cssClass (line: AppOutputLine) : string =
    match line.Stream with
    | OutputStream.Stdout -> "app-output-stdout"
    | OutputStream.Stderr -> "app-output-stderr"

// ── the buffer, and its bound ─────────────────────────────────────────────────────────────────────────────────────

/// The most recent lines, capped. A ring would be the obvious structure, but the cap is small and the pane reads the
/// whole buffer on every render, so a list trimmed on append is both simpler and faster at this size.
type AppOutputBuffer =
  { Lines: AppOutputLine list
    Capacity: int }

/// Why a buffer cannot be made.
[<RequireQualifiedAccess>]
type BufferRefusal =
  /// A cap of zero is valid and means "keep nothing", which is never what anyone meant. Refusing it by name beats
  /// a pane that is mysteriously empty.
  | CapacityMustBePositive of given: int

[<RequireQualifiedAccess>]
module AppOutputBuffer =
  let empty (capacity: int) : AppOutputBuffer =
    { Lines = []; Capacity = max 1 capacity }

  /// The refusing constructor, for a caller that has a capacity it does not control.
  let tryEmpty (capacity: int) : Result<AppOutputBuffer, BufferRefusal> =
    match capacity > 0 with
    | true -> Ok(empty capacity)
    | false -> Error(BufferRefusal.CapacityMustBePositive capacity)

  let describe (refusal: BufferRefusal) : string =
    match refusal with
    | BufferRefusal.CapacityMustBePositive given ->
      sprintf "an output buffer needs room for at least one line, and %d was asked for" given

  /// Add a line, dropping from the FRONT when full. The newest survive: a reader looking at output wants the end
  /// of it, and the beginning of a burst is the least interesting part.
  let append (buffer: AppOutputBuffer) (line: AppOutputLine) : AppOutputBuffer =
    let kept = buffer.Lines @ [ line ]
    let dropped = max 0 (List.length kept - buffer.Capacity)
    { buffer with Lines = kept |> List.skip dropped }

  let count (buffer: AppOutputBuffer) : int = List.length buffer.Lines

  let toList (buffer: AppOutputBuffer) : AppOutputLine list = buffer.Lines

  let last (buffer: AppOutputBuffer) : string option =
    buffer.Lines |> List.tryLast |> Option.map (fun l -> l.Text)

  let clear (buffer: AppOutputBuffer) : AppOutputBuffer = { buffer with Lines = [] }

/// Which streams the pane shows. Not `[<RequireQualifiedAccess>]` for the reason given on `Following`: nothing
/// serializes this type, and `ErrorsOnly` at the arm says more than `OutputStreamFilter.ErrorsOnly`.
type OutputStreamFilter =
  /// Both. What the pane does until anyone asks for otherwise.
  | BothStreams
  /// Only stderr, which is what you want when hunting a stack trace in an app that logs steadily.
  | ErrorsOnly
  /// Only stdout, for when the errors are known and noisy and you want the app's own voice.
  | OutputOnly

// ── the pane ───────────────────────────────────────────────────────────────────────────────────────────────────────

/// Whether the pane takes new lines into view as they arrive.
///
/// NOT `[<RequireQualifiedAccess>]`, unlike the closed sets above. Those are wire tokens read from a payload far from
/// here, where an unqualified `Stdout` in a match is ambiguous with anything else in scope. These two are read a few
/// lines from where they are declared and appear in no payload, so the attribute would only make every arm longer.
type Following =
  /// The view moves as lines arrive, which is what you want while tailing a log.
  | AutoScroll
  /// Held still: lines still arrive and are still buffered, they just do not move the view. A pane that stopped
  /// scrolling because you scrolled up is not broken, and one that stopped taking lines has lost data.
  | HeldStill

/// Whether the pane shows what is in the buffer.
type Paused =
  /// Showing the buffer.
  | Showing
  /// Held. Lines keep arriving and are kept; the pane shows nothing, and the header says how many it is holding.
  | Held

type AppOutputPane =
  { Buffer: AppOutputBuffer
    Following: Following
    Paused: Paused
    /// "" is not a search. Whitespace-only would be "match lines containing nothing", which is not a thing anyone
    /// asked for, so it is read as no search rather than as one that hides everything.
    Search: string
    StreamFilter: OutputStreamFilter }

[<RequireQualifiedAccess>]
module AppOutputPane =
  /// How many lines a pane keeps. A chatty app prints far more than this in a minute and nobody scrolls back that
  /// far; the cap is what stops the pane being a leak with a UI on it.
  let defaultCapacity = 2000

  let create : AppOutputPane =
    { Buffer = AppOutputBuffer.empty defaultCapacity
      Following = AutoScroll
      Paused = Showing
      Search = ""
      StreamFilter = BothStreams }

  let setFollowing (following: bool) (pane: AppOutputPane) : AppOutputPane =
    { pane with Following = if following then AutoScroll else HeldStill }

  let following (pane: AppOutputPane) : bool =
    match pane.Following with
    | AutoScroll -> true
    | HeldStill -> false

  let setPaused (paused: bool) (pane: AppOutputPane) : AppOutputPane =
    { pane with Paused = if paused then Held else Showing }

  let paused (pane: AppOutputPane) : bool =
    match pane.Paused with
    | Showing -> false
    | Held -> true

  let setSearch (search: string) (pane: AppOutputPane) : AppOutputPane =
    { pane with Search = search }

  /// Whether there IS a search, which is not the same as whether `Search` is non-empty.
  let searching (pane: AppOutputPane) : bool = not (String.IsNullOrWhiteSpace pane.Search)

  let setStreamFilter (filter: OutputStreamFilter) (pane: AppOutputPane) : AppOutputPane =
    { pane with StreamFilter = filter }

  /// Take a line into the pane. The LINE is the first argument, so a run of lines pipes naturally
  /// (`lines |> List.fold (flip feed) pane`, or `line |> fun l -> feed pane l`).
  let feed (line: AppOutputLine) (pane: AppOutputPane) : AppOutputPane =
    { pane with Buffer = AppOutputBuffer.append pane.Buffer line }

  /// Take raw text, dropping a blank line. This is the door the daemon's `OnAppOutput` lines come through.
  let feedText (stream: OutputStream) (text: string) (pane: AppOutputPane) : AppOutputPane =
    match AppOutputLine.ofText stream text with
    | None -> pane
    | Some line -> feed line pane

  /// Take many lines, in order.
  let feedAll (lines: AppOutputLine list) (pane: AppOutputPane) : AppOutputPane =
    lines |> List.fold (fun pane line -> feed line pane) pane

  /// Take lines built from text, in order. The TEXTS are the first argument so a run of them pipes in, which is how
  /// the daemon's flush loop hands over a session's pending lines.
  let feedTexts (stream: OutputStream) (texts: string list) (pane: AppOutputPane) : AppOutputPane =
    texts |> List.fold (fun pane text -> feedText stream text pane) pane

  /// How many lines arrived while the pane was paused. This is the number the header shows, and the reason a paused
  /// pane is not a lossy one: nothing was thrown away, it is just not on screen yet.
  let heldWhilePaused (pane: AppOutputPane) : int =
    match pane.Paused with
    | Showing -> 0
    | Held -> AppOutputBuffer.count pane.Buffer

  let buffered (pane: AppOutputPane) : int = AppOutputBuffer.count pane.Buffer

  /// Whether a line passes the stream filter and the search, case-insensitively, because a reader does not
  /// remember the app's casing.
  let matches (pane: AppOutputPane) (line: AppOutputLine) : bool =
    let streamOk =
      match pane.StreamFilter with
      | BothStreams -> true
      | ErrorsOnly -> line.Stream = OutputStream.Stderr
      | OutputOnly -> line.Stream = OutputStream.Stdout

    let searchOk =
      match searching pane with
      | false -> true
      | true -> line.Text.Contains(pane.Search.Trim(), StringComparison.OrdinalIgnoreCase)

    streamOk && searchOk

  /// The lines on screen. Empty while paused, because a paused pane that still showed new lines would not be paused.
  let visible (pane: AppOutputPane) : AppOutputLine list =
    match pane.Paused with
    | Held -> []
    | Showing -> pane.Buffer.Lines |> List.filter (matches pane)

  /// Whether the search is on and matched nothing. The panel says so, because a search that quietly falls back to
  /// showing everything is indistinguishable from the search having been cleared.
  let matching (pane: AppOutputPane) : bool =
    match searching pane with
    | false -> true
    | true -> pane.Buffer.Lines |> List.exists (matches pane)

  let clear (pane: AppOutputPane) : AppOutputPane =
    { pane with Buffer = AppOutputBuffer.clear pane.Buffer }

  /// The one line the pane's header says about itself. An idle pane says it is idle, so a still pane is not mistaken
  /// for a dead app.
  let header (pane: AppOutputPane) : string =
    let held = AppOutputBuffer.count pane.Buffer
    match pane.Paused, held with
    | Held, 0 -> "paused, with no output yet"
    | Held, n -> sprintf "%d lines while paused" n
    | Showing, 0 -> "no output yet: the app prints here when it prints anything"
    | Showing, 1 -> "1 line"
    | Showing, n -> sprintf "%d lines%s" n (if searching pane then ", filtered" else "")
