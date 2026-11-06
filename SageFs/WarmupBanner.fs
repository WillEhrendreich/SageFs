namespace SageFs

open System
open SageFs.WorkerProtocol
open SageFs.WarmUp
open SageFs.Features.Diagnostics
open SageFs.Features.LiveTesting

/// Converts a SessionContext into OutputLine list for the warmup banner.
/// Shown in the output pane BEFORE any eval results so users know what was loaded.
module WarmupBanner =
  /// "1 assembly", "3 assemblies": a count is never followed by the wrong number of the noun (the banner said "1 assemblies").
  let assembliesText (count: int) : string =
    match count with
    | 1 -> "1 assembly"
    | n -> sprintf "%d assemblies" n

  let toOutputLines (ctx: SessionContext) : OutputLine list =
    let w = ctx.Warmup
    let sid = ctx.SessionId
    let now = DateTime.UtcNow
    let opened = w.NamespacesOpened.Length
    let failed = w.FailedOpens.Length
    let asmCount = w.AssembliesLoaded.Length
    let lines = Collections.Generic.List<OutputLine>()
    lines.Add {
      Kind = OutputKind.System
      Text = sprintf "%s Warmup: %s, %d opened, %d failed, %dms"
        (Glyphs.text Glyph.Settings) (assembliesText asmCount) opened failed (WarmupContext.totalDurationMs w)
      Timestamp = now; SessionId = sid
    }
    match asmCount > 0 with
    | true ->
      for a in w.AssembliesLoaded do
        lines.Add {
          Kind = OutputKind.System
          Text = sprintf "  %s %s (%d ns, %d modules)" (Glyphs.text Glyph.Package) a.Name a.NamespaceCount a.ModuleCount
          Timestamp = now; SessionId = sid
        }
    | false -> ()
    // Per-namespace opens are streamed via WarmupProgress events during warmup.
    // Banner only shows failures.
    for f in w.FailedOpens do
      let kind = OpenableKind.label f.Kind
      lines.Add {
        Kind = OutputKind.Failure
        Text = sprintf "  %s Failed to open %s (%s) — %s" (Glyphs.text Glyph.Fail) f.Name kind f.ErrorMessage
        Timestamp = now; SessionId = sid
      }
      for d in f.Diagnostics do
        lines.Add {
          Kind = OutputKind.Failure
          Text = sprintf "    %s" (WarmupFcsDiagnostic.formatLine d)
          Timestamp = now; SessionId = sid
        }
      WarmupOpenFailure.suggestedAction f
      |> Option.iter (fun action ->
        lines.Add { Kind = OutputKind.Failure; Text = sprintf "    → %s" action; Timestamp = now; SessionId = sid })
    match ctx.FileStatuses.Length > 0 with
    | true ->
      let loaded = ctx.FileStatuses |> List.filter (fun f -> f.Readiness = Loaded) |> List.length
      lines.Add {
        Kind = OutputKind.System
        Text = sprintf "  Files (%d/%d loaded):" loaded ctx.FileStatuses.Length
        Timestamp = now; SessionId = sid
      }
      for f in ctx.FileStatuses do
        lines.Add {
          Kind = OutputKind.System
          Text = sprintf "    %s %s" (FileReadiness.icon f.Readiness) f.Path
          Timestamp = now; SessionId = sid
        }
    | false -> ()
    lines |> Seq.toList
