// WHY — this file exists because a whole CLASS of VS Code defect kept shipping
// green: a control that names a command string nothing registers, or teaches a
// keystroke nothing binds. Nothing in the repo could catch it, because every
// existing contract test pins a PROJECTION ("what does the lens say?") and
// never the thing the projection NAMES ("does that command exist?").
//
// Three measured instances (sagefs-ux-roast.md §4.3, §2.2):
//   * `sagefs.showCoveringTests` — emitted by every coverage CodeLens in every
//     F# file, appearing exactly ONCE in the whole repo: the line that emits
//     it. Clicking it popped `command 'sagefs.showCoveringTests' not found`.
//     Its rendering was contract-tested in 194 lines. Its existence was not.
//   * `sagefs.reconnect` — the `[Reconnect]` button on a connection-lost
//     dialog. Never registered; the rejection was swallowed into the output
//     channel, so the button looked like it worked.
//   * The official walkthrough's step 4 told a brand-new user to press
//     `Ctrl+Enter`. No `ctrl+enter` binding exists, and there is not one
//     `"mac"` key in the whole keybindings block. Step 4 of onboarding did
//     nothing, and the in-editor sample said something different again.
//
// These tests are the gate. They read package.json and every `src/*.fs` as
// data — no Fable, no extension host — and enforce four contracts:
//   1. every `sagefs.*` command string referenced from source is registered or
//      contributed (kills the dead-command class outright),
//   2. every command contributed to the palette is actually registered,
//   3. every command a menu / keybinding / welcome view / walkthrough links to
//      exists, and
//   4. every keystroke named in onboarding PROSE is a real binding.
//
// Runs under plain `dotnet fsi` (no Fable), mirroring AppRunContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

let private root = Path.Combine(__SOURCE_DIRECTORY__, "..")
let private srcDir = Path.Combine(root, "src")
let private packageJsonPath = Path.Combine(root, "package.json")

let private packageJsonText = File.ReadAllText packageJsonPath
let private pkg = JsonDocument.Parse(packageJsonText).RootElement
let private contributes = pkg.GetProperty "contributes"

let private tryProp (name: string) (el: JsonElement) =
  match el.TryGetProperty name with
  | true, v -> Some v
  | _ -> None

/// Every `.fs` under src/, as text. The extension's whole command vocabulary
/// lives in string literals, so text is the honest unit here.
let private sourceFiles =
  Directory.GetFiles(srcDir, "*.fs") |> Array.map (fun f -> Path.GetFileName f, File.ReadAllText f)

// ── What package.json declares ────────────────────────────────────

let private contributedCommands =
  contributes
  |> tryProp "commands"
  |> Option.map (fun c -> c.EnumerateArray() |> Seq.choose (tryProp "command" >> Option.map (fun p -> p.GetString())) |> Set.ofSeq)
  |> Option.defaultValue Set.empty

let private contributedColors =
  contributes
  |> tryProp "colors"
  |> Option.map (fun c -> c.EnumerateArray() |> Seq.choose (tryProp "id" >> Option.map (fun p -> p.GetString())) |> Set.ofSeq)
  |> Option.defaultValue Set.empty

let private contributedSettings =
  contributes
  |> tryProp "configuration"
  |> Option.bind (tryProp "properties")
  |> Option.map (fun p -> p.EnumerateObject() |> Seq.map (fun m -> m.Name) |> Set.ofSeq)
  |> Option.defaultValue Set.empty

/// Every `"command": "..."` under contributes.menus, whatever the menu group.
let private menuCommands =
  contributes
  |> tryProp "menus"
  |> Option.map (fun m ->
    m.EnumerateObject()
    |> Seq.collect (fun grp -> grp.Value.EnumerateArray())
    |> Seq.choose (tryProp "command" >> Option.map (fun p -> p.GetString()))
    |> Set.ofSeq)
  |> Option.defaultValue Set.empty

let private keybindingEntries =
  contributes
  |> tryProp "keybindings"
  |> Option.map (fun k -> k.EnumerateArray() |> List.ofSeq)
  |> Option.defaultValue []

let private keybindingCommands =
  keybindingEntries |> List.choose (tryProp "command" >> Option.map (fun p -> p.GetString())) |> Set.ofList

/// Every key a binding actually installs, across the default and the
/// platform-specific slots, lowercased for comparison.
let private boundKeys =
  keybindingEntries
  |> List.collect (fun e ->
    [ "key"; "mac"; "linux"; "win" ]
    |> List.choose (fun slot -> e |> tryProp slot |> Option.map (fun p -> p.GetString().ToLowerInvariant())))
  |> Set.ofList

/// `[Label](command:sagefs.x)` links embedded in viewsWelcome contents and
/// walkthrough descriptions. These are clickable and fail the same way.
let private commandLinksIn (text: string) =
  Regex.Matches(text, @"command:(sagefs\.[A-Za-z0-9._]+)")
  |> Seq.map (fun m -> m.Groups.[1].Value)
  |> Set.ofSeq

let private viewsWelcomeText =
  contributes
  |> tryProp "viewsWelcome"
  |> Option.map (fun v ->
    v.EnumerateArray()
    |> Seq.choose (tryProp "contents" >> Option.map (fun p -> p.GetString()))
    |> String.concat "\n")
  |> Option.defaultValue ""

let private walkthroughSteps =
  contributes
  |> tryProp "walkthroughs"
  |> Option.map (fun w ->
    w.EnumerateArray()
    |> Seq.collect (fun wt -> wt |> tryProp "steps" |> Option.map (fun s -> s.EnumerateArray() |> List.ofSeq) |> Option.defaultValue [])
    |> List.ofSeq)
  |> Option.defaultValue []

let private walkthroughText =
  walkthroughSteps
  |> List.collect (fun s ->
    [ "title"; "description" ] |> List.choose (fun f -> s |> tryProp f |> Option.map (fun p -> p.GetString())))
  |> String.concat "\n"

// ── What the source actually registers and references ─────────────

/// Every spelling the extension uses: the raw VS Code API call, the
/// value-returning variant (`registerCommandValue exports "id" ...`), and the
/// local `reg` helper that wraps the plain one.
let private registeredCommands =
  sourceFiles
  |> Array.collect (fun (_, text) ->
    Regex.Matches(text, @"(?:registerCommand\w*|registerTextEditorCommand\w*|\breg)\s+(?:\w+\s+)?""(sagefs\.[A-Za-z0-9._]+)""")
    |> Seq.map (fun m -> m.Groups.[1].Value)
    |> Array.ofSeq)
  |> Set.ofArray

/// Every `"sagefs.*"` string literal in the source, with the file it came from
/// so a failure names where to look.
let private referencedIdentifiers =
  sourceFiles
  |> Array.collect (fun (file, text) ->
    Regex.Matches(text, @"""(sagefs\.[A-Za-z0-9._]+)""")
    |> Seq.map (fun m -> m.Groups.[1].Value, file)
    |> Array.ofSeq)

/// Anything that legitimately shares the `sagefs.` prefix without being a
/// command: theme colours and settings keys.
let private nonCommandIdentifiers = Set.union contributedColors contributedSettings

// ── Keystroke prose ───────────────────────────────────────────────

/// A keystroke as a human writes it in documentation ("Ctrl+Enter"), rendered
/// into the form `contributes.keybindings` uses ("ctrl+enter").
let private normalizeKey (s: string) = s.ToLowerInvariant().Replace("cmd+", "cmd+")

let private keystrokesIn (text: string) =
  Regex.Matches(text, @"\b(?:Ctrl|Cmd|Alt|Shift)(?:\+(?:Ctrl|Cmd|Alt|Shift))*\+[A-Za-z0-9\[\]]+")
  |> Seq.map (fun m -> m.Value)
  |> Set.ofSeq

/// VS Code writes a Mac binding as `cmd+...` in the `mac` slot. A prose
/// "Cmd+Enter" is therefore satisfied by `cmd+enter` in any slot.
let private isBound (prose: string) =
  boundKeys |> Set.contains (normalizeKey prose)

let tests =
  testList "VS Code command + keybinding contract" [

    testCase "WHY - the fixtures loaded at all, so an empty regex sweep cannot pass vacuously" <| fun _ ->
      sourceFiles.Length > 20 |> Expect.isTrue "source files found"
      contributedCommands.Count > 40 |> Expect.isTrue "commands contributed"
      keybindingEntries.Length > 5 |> Expect.isTrue "keybindings contributed"
      registeredCommands.Count > 40 |> Expect.isTrue "the registerCommand regex still matches"

    testCase "WHY - every sagefs.* command named in source exists, because showCoveringTests did not" <| fun _ ->
      // The defect: a CodeLens on every covered function in every F# file,
      // naming a command that appears nowhere else in the repo. Clicking it
      // popped `command not found`.
      let orphans =
        referencedIdentifiers
        |> Array.filter (fun (id, _) ->
          not (Set.contains id registeredCommands)
          && not (Set.contains id contributedCommands)
          && not (Set.contains id nonCommandIdentifiers))
        |> Array.map (fun (id, file) -> sprintf "%s (referenced in %s)" id file)
        |> Array.distinct
        |> Array.sort
      orphans
      |> Expect.isEmpty (sprintf "these command ids are referenced but neither registered nor contributed: %s" (String.concat ", " orphans))

    testCase "WHY - every command offered in the palette is registered, so no palette entry is a dead end" <| fun _ ->
      let unregistered =
        contributedCommands - registeredCommands |> Set.toArray |> Array.sort
      unregistered
      |> Expect.isEmpty (sprintf "contributed but never registered: %s" (String.concat ", " unregistered))

    testCase "WHY - every command a menu or keybinding points at exists" <| fun _ ->
      let known = Set.union contributedCommands registeredCommands
      let dangling = (Set.union menuCommands keybindingCommands) - known |> Set.toArray |> Array.sort
      dangling
      |> Expect.isEmpty (sprintf "menu/keybinding points at a non-existent command: %s" (String.concat ", " dangling))

    testCase "WHY - every command:... link in a welcome view or walkthrough exists" <| fun _ ->
      // A welcome view's only affordance is its links. A dead link there is a
      // first-run dead end with nothing else on the screen to fall back to.
      let known = Set.union contributedCommands registeredCommands
      let links = Set.union (commandLinksIn viewsWelcomeText) (commandLinksIn walkthroughText)
      links.Count > 3 |> Expect.isTrue "links were actually found (the regex still matches)"
      let dangling = links - known |> Set.toArray |> Array.sort
      dangling
      |> Expect.isEmpty (sprintf "welcome/walkthrough links at a non-existent command: %s" (String.concat ", " dangling))

    testCase "WHY - every keystroke the walkthrough teaches is a real binding, because Ctrl+Enter was not" <| fun _ ->
      // Step 4 of the official walkthrough said "press Ctrl+Enter". No
      // ctrl+enter binding existed. A new user's first instruction did nothing.
      let taught = keystrokesIn walkthroughText
      let unbound = taught |> Set.filter (isBound >> not) |> Set.toArray |> Array.sort
      unbound
      |> Expect.isEmpty (sprintf "walkthrough teaches unbound keystroke(s): %s (bound keys: %s)" (String.concat ", " unbound) (boundKeys |> Set.toArray |> String.concat ", "))

    testCase "WHY - every keystroke named anywhere in the extension's own prose is a real binding" <| fun _ ->
      // The walkthrough and the in-editor Getting Started sample contradicted
      // each other AND the keymap. One rule, both sources.
      let offenders =
        sourceFiles
        |> Array.collect (fun (file, text) ->
          keystrokesIn text |> Set.filter (isBound >> not) |> Set.toArray |> Array.map (fun k -> sprintf "%s in %s" k file))
        |> Array.sort
      offenders
      |> Expect.isEmpty (sprintf "source prose names unbound keystroke(s): %s" (String.concat ", " offenders))

    testCase "WHY - the eval keystroke is bound exactly as onboarding says, in both onboarding sources" <| fun _ ->
      // Pins the specific reconciliation: both sources must name the SAME key,
      // and it must be the one bound to sagefs.eval.
      let evalKey =
        keybindingEntries
        |> List.tryFind (fun e -> (e |> tryProp "command" |> Option.map (fun p -> p.GetString())) = Some "sagefs.eval")
        |> Option.bind (tryProp "key")
        |> Option.map (fun p -> p.GetString())
      evalKey |> Expect.equal "sagefs.eval is bound to alt+enter" (Some "alt+enter")
      let gettingStarted =
        sourceFiles |> Array.tryFind (fun (f, _) -> f = "Extension.fs") |> Option.map snd |> Option.defaultValue ""
      keystrokesIn walkthroughText
      |> Expect.contains "the walkthrough names the real eval key" "Alt+Enter"
      keystrokesIn gettingStarted
      |> Expect.contains "the in-editor sample names the same key" "Alt+Enter"
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
