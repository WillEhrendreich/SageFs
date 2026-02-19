module SageFs.Tests.ThemePersistenceTests

open Expecto
open VerifyExpecto
open VerifyTests
open Falco.Markup
open SageFs
open SageFs.Server.Dashboard
open SageFs.DaemonClient

let snapshotsDir =
  System.IO.Path.Combine(__SOURCE_DIRECTORY__, "snapshots")

let verifyTheme (name: string) (html: string) =
  let settings = VerifySettings()
  settings.UseDirectory(snapshotsDir)
  settings.DisableDiff()
  Verifier.Verify(name, html, "html", settings).ToTask()

// ─── Snapshot: renderThemeVars ───────────────────────────────────────────────

let themeVarsSnapshotTests = testList "renderThemeVars snapshots" [
  testTask "One Dark theme vars" {
    let html = renderThemeVars "One Dark" |> renderNode
    do! verifyTheme "theme_vars_oneDark" html
  }

  testTask "Dracula theme vars" {
    let html = renderThemeVars "Dracula" |> renderNode
    do! verifyTheme "theme_vars_dracula" html
  }

  testTask "Nordic theme vars" {
    let html = renderThemeVars "Nordic" |> renderNode
    do! verifyTheme "theme_vars_nordic" html
  }

  testTask "unknown theme falls back to defaults" {
    let html = renderThemeVars "NonExistentTheme" |> renderNode
    let defaultHtml = renderThemeVars "One Dark" |> renderNode
    Expect.equal html defaultHtml "unknown theme should use defaults"
  }

  testTask "empty theme name falls back to defaults" {
    let html = renderThemeVars "" |> renderNode
    let defaultHtml = renderThemeVars "One Dark" |> renderNode
    Expect.equal html defaultHtml "empty name should use defaults"
  }
]

// ─── Snapshot: renderThemePicker ─────────────────────────────────────────────

let themePickerSnapshotTests = testList "renderThemePicker snapshots" [
  testTask "picker with One Dark selected" {
    let html = renderThemePicker "One Dark" |> renderNode
    do! verifyTheme "theme_picker_oneDark" html
  }

  testTask "picker with Dracula selected" {
    let html = renderThemePicker "Dracula" |> renderNode
    do! verifyTheme "theme_picker_dracula" html
  }

  testTask "picker with Nordic selected" {
    let html = renderThemePicker "Nordic" |> renderNode
    do! verifyTheme "theme_picker_nordic" html
  }
]

// ─── Unit: renderThemeVars content ───────────────────────────────────────────

let themeVarsUnitTests = testList "renderThemeVars content" [
  test "contains :root selector" {
    let html = renderThemeVars "One Dark" |> renderNode
    Expect.stringContains html ":root" "should contain :root"
  }

  test "contains all CSS variable names" {
    let html = renderThemeVars "One Dark" |> renderNode
    let expectedVars = [
      "--fg-default"; "--fg-dim"; "--fg-green"; "--fg-red"
      "--fg-yellow"; "--fg-cyan"; "--fg-blue"; "--fg-magenta"
      "--bg-default"; "--bg-panel"; "--bg-editor"
      "--bg-selection"; "--bg-status"; "--bg-focus"
      "--border-normal"; "--border-focus"
      "--syn-keyword"; "--syn-string"; "--syn-comment"; "--syn-number"
      "--syn-operator"; "--syn-type"; "--syn-function"; "--syn-variable"
      "--syn-punctuation"; "--syn-constant"; "--syn-module"
      "--syn-attribute"; "--syn-directive"; "--syn-property"
    ]
    for v in expectedVars do
      Expect.stringContains html v (sprintf "should contain %s" v)
  }

  test "has style element with theme-vars id" {
    let html = renderThemeVars "One Dark" |> renderNode
    Expect.stringContains html """id="theme-vars""" "should have id=theme-vars"
  }

  test "Dracula has different colors than One Dark" {
    let oneDark = renderThemeVars "One Dark" |> renderNode
    let dracula = renderThemeVars "Dracula" |> renderNode
    Expect.notEqual oneDark dracula "different themes should produce different CSS"
  }

  test "all presets produce valid CSS" {
    for (name, _) in ThemePresets.all do
      let html = renderThemeVars name |> renderNode
      Expect.stringContains html ":root" (sprintf "%s should contain :root" name)
      Expect.stringContains html "--fg-default" (sprintf "%s should contain --fg-default" name)
  }
]

// ─── Unit: renderThemePicker content ─────────────────────────────────────────

let themePickerUnitTests = testList "renderThemePicker content" [
  test "contains select element with theme-picker id" {
    let html = renderThemePicker "One Dark" |> renderNode
    Expect.stringContains html """id="theme-picker""" "should have id=theme-picker"
  }

  test "contains all preset names as options" {
    let html = renderThemePicker "One Dark" |> renderNode
    for (name, _) in ThemePresets.all do
      Expect.stringContains html name (sprintf "should contain option for %s" name)
  }

  test "selected theme has selected attribute" {
    let html = renderThemePicker "Dracula" |> renderNode
    Expect.stringContains html """value="Dracula" selected""" "Dracula should be selected"
  }

  test "non-selected themes lack selected attribute" {
    let html = renderThemePicker "Dracula" |> renderNode
    // One Dark should NOT have selected
    Expect.isFalse
      (html.Contains """value="One Dark" selected""")
      "One Dark should not be selected when Dracula is"
  }

  test "each preset name appears exactly once as option value" {
    let html = renderThemePicker "One Dark" |> renderNode
    for (name, _) in ThemePresets.all do
      let pattern = sprintf """value="%s""" name
      let count =
        let mutable c = 0
        let mutable idx = 0
        while idx >= 0 do
          idx <- html.IndexOf(pattern, idx)
          if idx >= 0 then c <- c + 1; idx <- idx + 1
        c
      Expect.equal count 1 (sprintf "%s should appear exactly once" name)
  }
]

// ─── Unit: toCssVariables completeness ───────────────────────────────────────

let cssVariablesTests = testList "toCssVariables" [
  test "produces 30 CSS variable declarations" {
    let css = Theme.toCssVariables Theme.defaults
    let count = css.Split("--") |> Array.length
    // split on "--" gives N+1 pieces for N variables
    Expect.isGreaterThanOrEqual count 30 "should have at least 30 CSS variables"
  }

  test "all hex values start with #" {
    let css = Theme.toCssVariables Theme.defaults
    let lines = css.Split(";") |> Array.filter (fun s -> s.Trim().Length > 0)
    for line in lines do
      Expect.stringContains line "#" (sprintf "CSS line should contain hex color: %s" (line.Trim()))
  }

  test "different themes produce different CSS" {
    let oneDark = Theme.toCssVariables Theme.defaults
    let dracula = Theme.toCssVariables ThemePresets.dracula
    Expect.notEqual oneDark dracula "One Dark and Dracula should differ"
  }

  test "all preset configs produce non-empty CSS" {
    for (name, config) in ThemePresets.all do
      let css = Theme.toCssVariables config
      Expect.isGreaterThan css.Length 0 (sprintf "%s should produce non-empty CSS" name)
  }
]

// ─── Unit: ThemePresets functions ─────────────────────────────────────────────

let themePresetsTests = testList "ThemePresets" [
  test "all has 8 presets" {
    Expect.equal ThemePresets.all.Length 8 "should have 8 presets"
  }

  test "all preset names are unique" {
    let names = ThemePresets.all |> List.map fst
    let unique = names |> List.distinct
    Expect.equal names.Length unique.Length "all names should be unique"
  }

  test "tryFind returns Some for exact name" {
    let result = ThemePresets.tryFind "Dracula"
    Expect.isSome result "should find Dracula"
  }

  test "tryFind is case-insensitive" {
    let result = ThemePresets.tryFind "dracula"
    Expect.isSome result "should find dracula (lowercase)"
    let result2 = ThemePresets.tryFind "DRACULA"
    Expect.isSome result2 "should find DRACULA (uppercase)"
  }

  test "tryFind returns None for unknown name" {
    let result = ThemePresets.tryFind "NonExistent"
    Expect.isNone result "should not find NonExistent"
  }

  test "tryFind returns None for empty string" {
    let result = ThemePresets.tryFind ""
    Expect.isNone result "should not find empty string"
  }

  test "cycleNext wraps around from last to first" {
    let lastName, lastConfig = ThemePresets.all |> List.last
    let nextName, _ = ThemePresets.cycleNext lastConfig
    let firstName, _ = ThemePresets.all |> List.head
    Expect.equal nextName firstName "should wrap to first preset"
  }

  test "cycleNext advances sequentially" {
    let mutable current = snd ThemePresets.all.[0]
    for i in 1 .. ThemePresets.all.Length - 1 do
      let expectedName, _ = ThemePresets.all.[i]
      let name, next = ThemePresets.cycleNext current
      Expect.equal name expectedName (sprintf "step %d should be %s" i expectedName)
      current <- next
  }

  test "cycleNext with unknown config starts from first" {
    let unknown = { Theme.defaults with FgDefault = "#000001" }
    let name, _ = ThemePresets.cycleNext unknown
    let firstName, _ = ThemePresets.all.[0]
    Expect.equal name firstName "unknown config should cycle to first"
  }
]

// ─── Unit: parseStateEvent activeWorkingDir ──────────────────────────────────

let parseStateEventThemeTests = testList "parseStateEvent activeWorkingDir" [
  test "parses activeWorkingDir when present" {
    let json = """{"sessionId":"s1","sessionState":"Ready","evalCount":0,"activeWorkingDir":"C:\\Code\\MyProj","regions":[]}"""
    let result = parseStateEvent json
    Expect.isSome result "should parse"
    let (_, _, _, _, workingDir, _) = result.Value
    Expect.equal workingDir @"C:\Code\MyProj" "activeWorkingDir"
  }

  test "activeWorkingDir defaults to empty when missing" {
    let json = """{"sessionId":"s1","sessionState":"Ready","evalCount":0,"regions":[]}"""
    let result = parseStateEvent json
    Expect.isSome result "should parse"
    let (_, _, _, _, workingDir, _) = result.Value
    Expect.equal workingDir "" "missing activeWorkingDir defaults to empty"
  }

  test "all 6 tuple fields parsed correctly" {
    let json = """{"sessionId":"abc","sessionState":"WarmingUp","evalCount":7,"avgMs":42.5,"activeWorkingDir":"C:\\test","regions":[{"id":"out","content":"hi"}]}"""
    let result = parseStateEvent json
    Expect.isSome result "should parse"
    let (sid, state, count, avgMs, workingDir, regions) = result.Value
    Expect.equal sid "abc" "sessionId"
    Expect.equal state "WarmingUp" "sessionState"
    Expect.equal count 7 "evalCount"
    Expect.floatClose Accuracy.medium avgMs 42.5 "avgMs"
    Expect.equal workingDir @"C:\test" "activeWorkingDir"
    Expect.equal regions.Length 1 "region count"
  }

  test "activeWorkingDir with forward slashes" {
    let json = """{"sessionState":"Ready","evalCount":0,"activeWorkingDir":"/home/user/project","regions":[]}"""
    let result = parseStateEvent json
    Expect.isSome result "should parse"
    let (_, _, _, _, workingDir, _) = result.Value
    Expect.equal workingDir "/home/user/project" "unix-style path"
  }

  test "activeWorkingDir with spaces in path" {
    let json = """{"sessionState":"Ready","evalCount":0,"activeWorkingDir":"C:\\My Projects\\Cool App","regions":[]}"""
    let result = parseStateEvent json
    Expect.isSome result "should parse"
    let (_, _, _, _, workingDir, _) = result.Value
    Expect.equal workingDir @"C:\My Projects\Cool App" "path with spaces"
  }
]

// ─── Unit: sessionThemes dictionary behavior ─────────────────────────────────

let sessionThemesTests = testList "sessionThemes dictionary logic" [
  test "save and restore theme for working dir" {
    let themes = System.Collections.Generic.Dictionary<string, string>()
    themes.[@"C:\Proj1"] <- "Dracula"
    match themes.TryGetValue(@"C:\Proj1") with
    | true, name -> Expect.equal name "Dracula" "should restore Dracula"
    | false, _ -> failtest "should find saved theme"
  }

  test "different working dirs have independent themes" {
    let themes = System.Collections.Generic.Dictionary<string, string>()
    themes.[@"C:\Proj1"] <- "Dracula"
    themes.[@"C:\Proj2"] <- "Nordic"
    match themes.TryGetValue(@"C:\Proj1") with
    | true, n -> Expect.equal n "Dracula" "Proj1 should be Dracula"
    | _ -> failtest "should find Proj1"
    match themes.TryGetValue(@"C:\Proj2") with
    | true, n -> Expect.equal n "Nordic" "Proj2 should be Nordic"
    | _ -> failtest "should find Proj2"
  }

  test "unknown working dir returns false" {
    let themes = System.Collections.Generic.Dictionary<string, string>()
    themes.[@"C:\Known"] <- "Gruvbox"
    let found, _ = themes.TryGetValue(@"C:\Unknown")
    Expect.isFalse found "unknown dir should not be found"
  }

  test "overwriting theme for same dir updates value" {
    let themes = System.Collections.Generic.Dictionary<string, string>()
    themes.[@"C:\Proj"] <- "Dracula"
    themes.[@"C:\Proj"] <- "Nordic"
    match themes.TryGetValue(@"C:\Proj") with
    | true, n -> Expect.equal n "Nordic" "should have latest theme"
    | _ -> failtest "should find theme"
  }

  test "ConcurrentDictionary works identically for thread safety" {
    let themes = System.Collections.Concurrent.ConcurrentDictionary<string, string>()
    themes.[@"C:\Proj1"] <- "Dracula"
    themes.[@"C:\Proj2"] <- "Nordic"
    match themes.TryGetValue(@"C:\Proj1") with
    | true, n -> Expect.equal n "Dracula" "concurrent: Proj1 should be Dracula"
    | _ -> failtest "should find Proj1"
    themes.[@"C:\Proj1"] <- "Monokai"
    match themes.TryGetValue(@"C:\Proj1") with
    | true, n -> Expect.equal n "Monokai" "concurrent: should update to Monokai"
    | _ -> failtest "should find updated"
  }

  test "theme restore with ThemePresets.tryFind" {
    let themes = System.Collections.Generic.Dictionary<string, string>()
    themes.[@"C:\Proj"] <- "Dracula"
    match themes.TryGetValue(@"C:\Proj") with
    | true, themeName ->
      let config = ThemePresets.tryFind themeName
      Expect.isSome config "should find Dracula config"
      Expect.equal config.Value.BgDefault ThemePresets.dracula.BgDefault "should match Dracula bg"
    | _ -> failtest "should find saved theme"
  }

  test "theme restore for invalid name returns None from tryFind" {
    let themes = System.Collections.Generic.Dictionary<string, string>()
    themes.[@"C:\Proj"] <- "DeletedTheme"
    match themes.TryGetValue(@"C:\Proj") with
    | true, themeName ->
      let config = ThemePresets.tryFind themeName
      Expect.isNone config "invalid theme name should not resolve"
    | _ -> failtest "should find saved entry"
  }
]

// ─── Unit: theme switch detection logic ──────────────────────────────────────

let themeSwitchDetectionTests = testList "theme switch detection" [
  test "detects working dir change" {
    let mutable lastWorkingDir = @"C:\Proj1"
    let newWorkingDir = @"C:\Proj2"
    let switched = newWorkingDir.Length > 0 && newWorkingDir <> lastWorkingDir && lastWorkingDir.Length > 0
    Expect.isTrue switched "should detect switch"
  }

  test "no switch when same working dir" {
    let mutable lastWorkingDir = @"C:\Proj1"
    let newWorkingDir = @"C:\Proj1"
    let switched = newWorkingDir.Length > 0 && newWorkingDir <> lastWorkingDir && lastWorkingDir.Length > 0
    Expect.isFalse switched "same dir should not trigger switch"
  }

  test "no switch on first event (lastWorkingDir empty)" {
    let mutable lastWorkingDir = ""
    let newWorkingDir = @"C:\Proj1"
    let switched = newWorkingDir.Length > 0 && newWorkingDir <> lastWorkingDir && lastWorkingDir.Length > 0
    Expect.isFalse switched "first event should not trigger switch"
  }

  test "no switch when new working dir empty" {
    let mutable lastWorkingDir = @"C:\Proj1"
    let newWorkingDir = ""
    let switched = newWorkingDir.Length > 0 && newWorkingDir <> lastWorkingDir && lastWorkingDir.Length > 0
    Expect.isFalse switched "empty new dir should not trigger switch"
  }

  test "full save-switch-restore cycle" {
    let themes = System.Collections.Generic.Dictionary<string, string>()
    let mutable lastWorkingDir = ""
    let mutable currentThemeName = "One Dark"
    let mutable currentTheme = Theme.defaults

    // First session arrives
    let wd1 = @"C:\Proj1"
    if wd1.Length > 0 then lastWorkingDir <- wd1

    // User changes theme
    let name, theme = ThemePresets.cycleNext currentTheme
    currentTheme <- theme
    currentThemeName <- name
    if lastWorkingDir.Length > 0 then
      themes.[lastWorkingDir] <- name

    // Switch to second session
    let wd2 = @"C:\Proj2"
    if wd2.Length > 0 && wd2 <> lastWorkingDir && lastWorkingDir.Length > 0 then
      themes.[lastWorkingDir] <- currentThemeName
      match themes.TryGetValue(wd2) with
      | true, tn ->
        match ThemePresets.tryFind tn with
        | Some t -> currentTheme <- t; currentThemeName <- tn
        | None -> ()
      | false, _ -> ()
    if wd2.Length > 0 then lastWorkingDir <- wd2

    // Proj2 should have default theme (never set)
    Expect.equal currentThemeName name "Proj2 never had theme, keeps current"

    // Switch back to Proj1
    let wd1again = @"C:\Proj1"
    if wd1again.Length > 0 && wd1again <> lastWorkingDir && lastWorkingDir.Length > 0 then
      themes.[lastWorkingDir] <- currentThemeName
      match themes.TryGetValue(wd1again) with
      | true, tn ->
        match ThemePresets.tryFind tn with
        | Some t -> currentTheme <- t; currentThemeName <- tn
        | None -> ()
      | false, _ -> ()
    if wd1again.Length > 0 then lastWorkingDir <- wd1again

    // Should restore the theme we set for Proj1
    Expect.equal currentThemeName name "should restore Proj1 theme"
  }
]

// ─── Unit: resolveThemePush pure logic ───────────────────────────────────────

let resolveThemePushTests = testList "resolveThemePush" [
  // --- Basic happy paths ---
  test "pushes stored theme when workingDir changes" {
    let themes = dict [| @"C:\Proj2", "Nordic" |] :> System.Collections.Generic.IDictionary<_,_>
    let result = resolveThemePush themes "s2" @"C:\Proj2" "s1" @"C:\Proj1"
    Expect.equal result (Some "Nordic") "should push stored theme for new dir"
  }

  test "pushes default when workingDir changes to unknown dir" {
    let themes = dict [||] :> System.Collections.Generic.IDictionary<_,_>
    let result = resolveThemePush themes "s2" @"C:\Unknown" "s1" @"C:\Proj1"
    Expect.equal result (Some "One Dark") "should push default for unknown dir"
  }

  test "no push when nothing changes" {
    let themes = dict [| @"C:\Proj1", "Dracula" |] :> System.Collections.Generic.IDictionary<_,_>
    let result = resolveThemePush themes "s1" @"C:\Proj1" "s1" @"C:\Proj1"
    Expect.isNone result "same session, same dir should not push"
  }

  // --- Bug 1: same workingDir, different session SHOULD push ---
  test "pushes when session changes but workingDir is same" {
    let themes = dict [| @"C:\SageFs", "Nordic" |] :> System.Collections.Generic.IDictionary<_,_>
    let result = resolveThemePush themes "session-B" @"C:\SageFs" "session-A" @"C:\SageFs"
    Expect.equal result (Some "Nordic") "different session same dir should push theme"
  }

  test "pushes default when session changes, same dir, no stored theme" {
    let themes = dict [||] :> System.Collections.Generic.IDictionary<_,_>
    let result = resolveThemePush themes "s2" @"C:\Proj" "s1" @"C:\Proj"
    Expect.equal result (Some "One Dark") "session change with no stored theme should push default"
  }

  // --- Bug 2: empty workingDir (faulted session) should push default ---
  test "pushes default when switching to empty workingDir session" {
    let themes = dict [| @"C:\Proj1", "Nordic" |] :> System.Collections.Generic.IDictionary<_,_>
    let result = resolveThemePush themes "faulted-session" "" "s1" @"C:\Proj1"
    Expect.equal result (Some "One Dark") "empty workingDir should push default theme"
  }

  test "pushes default when both previous and current workingDir empty but session changes" {
    let themes = dict [||] :> System.Collections.Generic.IDictionary<_,_>
    let result = resolveThemePush themes "s2" "" "s1" ""
    Expect.equal result (Some "One Dark") "session change with both dirs empty should push default"
  }

  // --- Bug 3: switching back from empty workingDir restores theme ---
  test "restores stored theme when switching back from empty-dir session" {
    let themes = dict [| @"C:\Proj1", "Gruvbox" |] :> System.Collections.Generic.IDictionary<_,_>
    // Was on Proj1 (Gruvbox), switched to faulted (empty), now switching back
    let result = resolveThemePush themes "s1" @"C:\Proj1" "faulted" ""
    Expect.equal result (Some "Gruvbox") "should restore Gruvbox when returning from empty-dir session"
  }

  // --- Full round-trip scenario ---
  test "full round trip: set Nordic, switch away, switch back" {
    let themes =
      System.Collections.Concurrent.ConcurrentDictionary<string, string>()
      :> System.Collections.Generic.IDictionary<_,_>
    themes.[@"C:\SageFs"] <- "Nordic"

    // Step 1: initial push for session A
    let r1 = resolveThemePush themes "sA" @"C:\SageFs" "" ""
    Expect.isSome r1 "initial session should push"
    Expect.equal r1.Value "Nordic" "should push Nordic"

    // Step 2: switch to session B (different dir, Harmony)
    let r2 = resolveThemePush themes "sB" @"C:\Harmony" "sA" @"C:\SageFs"
    Expect.isSome r2 "switch to Harmony should push"
    Expect.equal r2.Value "One Dark" "Harmony has no stored theme"

    // Step 3: switch back to session A (same dir as step 1)
    let r3 = resolveThemePush themes "sA" @"C:\SageFs" "sB" @"C:\Harmony"
    Expect.isSome r3 "switch back should push"
    Expect.equal r3.Value "Nordic" "should restore Nordic"
  }

  test "round trip with shared workingDir sessions" {
    let themes =
      System.Collections.Concurrent.ConcurrentDictionary<string, string>()
      :> System.Collections.Generic.IDictionary<_,_>
    themes.[@"C:\SageFs"] <- "Nordic"

    // Session A at C:\SageFs
    let r1 = resolveThemePush themes "sA" @"C:\SageFs" "" ""
    Expect.equal r1 (Some "Nordic") "session A initial push"

    // Session B also at C:\SageFs (different session, same dir)
    let r2 = resolveThemePush themes "sB" @"C:\SageFs" "sA" @"C:\SageFs"
    Expect.equal r2 (Some "Nordic") "session B same dir should still push"

    // Session C at C:\Harmony
    let r3 = resolveThemePush themes "sC" @"C:\Harmony" "sB" @"C:\SageFs"
    Expect.equal r3 (Some "One Dark") "Harmony no stored theme"

    // Back to session A at C:\SageFs
    let r4 = resolveThemePush themes "sA" @"C:\SageFs" "sC" @"C:\Harmony"
    Expect.equal r4 (Some "Nordic") "back to SageFs should restore Nordic"
  }

  // --- Edge cases ---
  test "no push when currentSessionId is empty" {
    let themes = dict [||] :> System.Collections.Generic.IDictionary<_,_>
    let result = resolveThemePush themes "" @"C:\Proj" "s1" @"C:\Proj"
    Expect.isNone result "empty currentSessionId should not push"
  }

  test "initial push on first connection (both previous empty)" {
    let themes = dict [| @"C:\Proj", "Dracula" |] :> System.Collections.Generic.IDictionary<_,_>
    let result = resolveThemePush themes "s1" @"C:\Proj" "" ""
    Expect.equal result (Some "Dracula") "first connection should push stored theme"
  }

  test "initial push with no stored theme defaults to One Dark" {
    let themes = dict [||] :> System.Collections.Generic.IDictionary<_,_>
    let result = resolveThemePush themes "s1" @"C:\NewProj" "" ""
    Expect.equal result (Some "One Dark") "first connection, no stored theme should push default"
  }
]


[<Tests>]
let allThemePersistenceTests = testList "Theme Persistence" [
  themeVarsSnapshotTests
  themePickerSnapshotTests
  themeVarsUnitTests
  themePickerUnitTests
  cssVariablesTests
  themePresetsTests
  parseStateEventThemeTests
  sessionThemesTests
  themeSwitchDetectionTests
  resolveThemePushTests
]
