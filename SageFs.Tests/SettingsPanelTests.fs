module SageFs.Tests.SettingsPanelTests

open System
open System.IO
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Server

/// Phase B1: the pure Settings panel render. Its subject IS HTML formatting, so
/// structural string assertions on the rendered markup are the right test —
/// they guard the grouping, the DU-driven chips/badges, and provenance display.
[<Tests>]
let tests =
  testList "Settings panel render" [

    let renderAllDefault () =
      // Resolve every pilot against an empty temp layer -> all at LDefault.
      let dir = Path.Combine(Path.GetTempPath(), "sagefs-panel-" + Guid.NewGuid().ToString("N").[..7])
      Directory.CreateDirectory dir |> ignore
      try
        let paths : ConfigPaths = { GlobalDir = dir; Repo = NoRepoCheckout }
        let rows : SettingsPanel.SettingRow list =
          SettingsCatalog.pilots
          |> List.map (fun d -> { Descriptor = d; Resolved = SettingsCatalog.resolve paths d })
        renderNode (SettingsPanel.renderPanel SettingsPanel.Quiet rows)
      finally
        try Directory.Delete(dir, true) with _ -> ()

    testCase "WHY — every pilot's name appears, so no setting is silently dropped from the panel" <| fun _ ->
      let html = renderAllDefault ()
      for name in [ "Per-test timeout"; "Test-run timeout"; "MCP port"; "Bind host" ] do
        html |> Expect.stringContains (sprintf "panel shows %s" name) name

    testCase "WHY — settings are grouped by category, so the panel is organised not flat" <| fun _ ->
      let html = renderAllDefault ()
      html |> Expect.stringContains "Live testing group header" "Live testing"
      html |> Expect.stringContains "Daemon group header" "Daemon"

    testCase "WHY — the applicability chip class matches the DU case, so live/restart/guarded read distinctly" <| fun _ ->
      let html = renderAllDefault ()
      html |> Expect.stringContains "a live chip (per-test timeout)" "settings-chip-live"
      html |> Expect.stringContains "a restart chip (MCP port)" "settings-chip-restart"
      html |> Expect.stringContains "a guarded chip (bind host)" "settings-chip-guarded"

    testCase "WHY — an all-default resolution shows the default provenance badge, so the source is explicit" <| fun _ ->
      let html = renderAllDefault ()
      html |> Expect.stringContains "default provenance badge" "settings-badge-default"

    testCase "WHY — the panel root carries the stable DOM id, so an edit can morph it in place" <| fun _ ->
      let html = renderAllDefault ()
      html |> Expect.stringContains "stable morph target id" SettingsPanel.PanelDomId

    // ── Phase C1: the edit-target scope toggle ──────────────────────────
    // layerForScope is the single mapping used by both render and handler.
    let repoD = SettingsCatalog.pilots |> List.find (fun d -> d.Scope = RepoOverridable)
    let globalD = SettingsCatalog.pilots |> List.find (fun d -> d.Scope = Global)

    testCase "WHY — a repo-overridable setting edited in repo scope targets the repo layer, so a per-repo override is possible" <| fun _ ->
      SettingsPanel.layerForScope SettingsPanel.ScopeRepo repoD
      |> Expect.equal "repo scope on a RepoOverridable setting writes the repo layer" LRepo

    testCase "WHY — a repo-overridable setting edited in global scope targets the global layer, so the toggle is honoured both ways" <| fun _ ->
      SettingsPanel.layerForScope SettingsPanel.ScopeGlobal repoD
      |> Expect.equal "global scope writes the global layer" LGlobal

    testCase "WHY — a global-only setting stays global even when repo scope is chosen, so a daemon-wide setting can never be silently shadowed per-repo (the safety property)" <| fun _ ->
      SettingsPanel.layerForScope SettingsPanel.ScopeRepo globalD
      |> Expect.equal "a Global setting ignores the repo toggle" LGlobal

    testCase "WHY — the scope selector offers both edit targets, so the user can choose where a write lands" <| fun _ ->
      let html = renderAllDefault ()
      html |> Expect.stringContains "scope selector present" "settings-scope-select"
      html |> Expect.stringContains "global target option" ">Global<"
      html |> Expect.stringContains "repo target option" ">This repo<"
  ]
