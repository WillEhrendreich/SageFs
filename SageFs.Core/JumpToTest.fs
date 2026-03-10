module SageFs.JumpToTest

open System
open System.Diagnostics

/// Open a file at a specific line in the user's editor.
/// Respects the $EDITOR environment variable (vim/emacs-style `+N` arg);
/// falls back to `code --goto` (VS Code) when $EDITOR is not set.
let openInEditor (file: string) (line: int) =
  let editor = Environment.GetEnvironmentVariable("EDITOR")
  let exe, args =
    match editor with
    | null | "" ->
      // VS Code: code --goto "file:line"
      "code", sprintf "--goto \"%s:%d\"" file line
    | e ->
      // Vim / Emacs / nano style: editor +N "file"
      e, sprintf "+%d \"%s\"" line file
  try
    Process.Start(exe, args) |> ignore
  with ex ->
    // F12 is non-critical — log but don't crash the TUI/GUI loop.
    eprintfn "[SageFs] openInEditor failed: %s" ex.Message

/// Try to find (file, line) for the test at the given scroll offset in the regions.
///
/// TODO: No dedicated Tests pane exists in the UI yet. Test source locations live
/// in TestCase.Origin (SourceMapped of file * line) in the daemon's LiveTestState —
/// they are NOT included in the rendered RenderRegion content delivered over SSE.
///
/// When a dedicated Tests/LiveTests pane is added to PaneId and the render pipeline:
///   1. Add PaneId.Tests (or LiveTests) and its rendering to SageFsApp.render.
///   2. Embed (file, line) in the region's LineAnnotations or affordances so the
///      client can look them up without an extra daemon round-trip.
///   3. Replace the `None` return below with: find the annotation at index `scrollOffset`.
///
/// Until then F12 is a no-op — the keybinding machinery is in place and ready.
let getSelectedTestLocation
  (_regions: RenderRegion list)
  (_scrollOffset: int)
  : (string * int) option =
  None
