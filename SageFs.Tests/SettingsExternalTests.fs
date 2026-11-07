module SageFs.Tests.SettingsExternalTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open Falco.Markup
open SageFs
open SageFs.Server

let private withTempDir (run: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-settings-ext-" + Guid.NewGuid().ToString("N").[..7])
  Directory.CreateDirectory dir |> ignore
  try run dir
  finally try Directory.Delete(dir, true) with _ -> ()

let private noRepo (dir: string) : ConfigPaths = { GlobalDir = dir; Repo = NoRepoCheckout }

let private env (pairs: (string * string) list) : ExternalInputs =
  let table = Map.ofList pairs
  { ExternalInputs.none with
      Environment = fun name -> match Map.tryFind name table with Some raw -> EnvSet raw | None -> EnvUnset }

let private saveGlobal (dir: string) (key: string) (raw: string) =
  SettingsStore.setKey (SettingsStore.globalPath dir) key raw |> Expect.isOk "the raw write succeeds"

/// A saved Live setting is the one in force after a restart. Sequenced: it writes a process-global in `Timeouts`.
[<Tests>]
let startupTests =
  testSequenced <| testList "Settings — what a restart reads back" [

    testCase "WHY — a saved per-test timeout is the per-test timeout after a restart, because the page promised it and nothing read the file" <| fun _ ->
      withTempDir (fun dir ->
        let original = Timeouts.perTestDefault ()
        try
          saveGlobal dir SettingsCatalog.perTestTimeout.Key "42"
          SettingsCatalog.applyAtStart ExternalInputs.none (noRepo dir) [ SettingsCatalog.perTestTimeout ]
          (Timeouts.perTestDefault ()).TotalSeconds |> Expect.equal "the daemon uses what the panel saved" 42.0
        finally
          Timeouts.setPerTestTimeout original)

    testCase "WHY — a setting nobody saved is left at what the daemon already holds, because the default is not a value to push" <| fun _ ->
      withTempDir (fun dir ->
        let original = Timeouts.perTestDefault ()
        try
          SettingsCatalog.applyAtStart ExternalInputs.none (noRepo dir) [ SettingsCatalog.perTestTimeout ]
          Timeouts.perTestDefault () |> Expect.equal "unchanged" original
        finally
          Timeouts.setPerTestTimeout original)

    testCase "WHY — an environment variable beats the saved value at start, because the person who set it chose that number" <| fun _ ->
      withTempDir (fun dir ->
        let original = Timeouts.perTestDefault ()
        try
          saveGlobal dir SettingsCatalog.perTestTimeout.Key "42"
          SettingsCatalog.applyAtStart (env [ "SAGEFS_PER_TEST_TIMEOUT_SECONDS", "7" ]) (noRepo dir) [ SettingsCatalog.perTestTimeout ]
          (Timeouts.perTestDefault ()).TotalSeconds |> Expect.equal "the variable's value is in force" 7.0
        finally
          Timeouts.setPerTestTimeout original)

    testCase "WHY — a saved value that does not parse is not applied, because a typo in the file must not stop the daemon starting" <| fun _ ->
      withTempDir (fun dir ->
        let original = Timeouts.perTestDefault ()
        try
          saveGlobal dir SettingsCatalog.perTestTimeout.Key "soon"
          SettingsCatalog.applyAtStart ExternalInputs.none (noRepo dir) [ SettingsCatalog.perTestTimeout ]
          Timeouts.perTestDefault () |> Expect.equal "unchanged" original
        finally
          Timeouts.setPerTestTimeout original)
  ]

[<Tests>]
let resolveExternalTests =
  testList "Settings — the environment and flag layers" [

    testCase "WHY — an environment variable over a saved port resolves to the environment, with both layers listed" <| fun _ ->
      withTempDir (fun dir ->
        saveGlobal dir SettingsCatalog.mcpPort.Key "6000"
        match SettingsCatalog.resolveWith (env [ "SAGEFS_MCP_PORT", "5000" ]) (noRepo dir) SettingsCatalog.mcpPort with
        | Error why -> failtestf "expected Ok, got %A" why
        | Ok p ->
          p.Source |> Expect.equal "the variable wins" LEnvironment
          SettingsCatalog.mcpPort.Render p.Effective |> Expect.equal "effective is the variable's port" "5000"
          p.PerLayer |> List.map fst |> Expect.equal "every layer that set one, lowest first" [ LDefault; LGlobal; LEnvironment ])

    testCase "WHY — a flag beats the environment and the saved value, because the command line is the last word" <| fun _ ->
      withTempDir (fun dir ->
        saveGlobal dir SettingsCatalog.mcpPort.Key "6000"
        let inputs = { env [ "SAGEFS_MCP_PORT", "5000" ] with Flags = Map.ofList [ "--mcp-port", "4000" ] }
        match SettingsCatalog.resolveWith inputs (noRepo dir) SettingsCatalog.mcpPort with
        | Error why -> failtestf "expected Ok, got %A" why
        | Ok p ->
          p.Source |> Expect.equal "the flag wins" LCommandLine
          SettingsCatalog.mcpPort.Render p.Effective |> Expect.equal "effective is the flag's port" "4000")

    testCase "WHY — a variable that does not parse is an error row naming the layer, not a silent fall back to the saved value" <| fun _ ->
      withTempDir (fun dir ->
        match SettingsCatalog.resolveWith (env [ "SAGEFS_MCP_PORT", "not-a-port" ]) (noRepo dir) SettingsCatalog.mcpPort with
        | Ok _ -> failtest "expected an error"
        | Error (LayerValueInvalid(_, layer, _)) -> layer |> Expect.equal "the environment layer is named" LEnvironment
        | Error other -> failtestf "expected LayerValueInvalid, got %A" other)

    testCase "WHY — the override note names the variable and the saved value, so a row never hides what it ignores" <| fun _ ->
      withTempDir (fun dir ->
        saveGlobal dir "waits.warmupMaxMinutes" "25"
        let d = EnvBackedSettings.warmupMax.Descriptor
        match SettingsCatalog.resolveWith (env [ "SAGEFS_WARMUP_MAX_MINUTES", "15" ]) (noRepo dir) d with
        | Error why -> failtestf "expected Ok, got %A" why
        | Ok p ->
          match SettingsCatalog.overrideNote d p with
          | SettingsCatalog.OverriddenBy(layer, name, saved) ->
            layer |> Expect.equal "by the environment" LEnvironment
            name |> Expect.equal "names the variable" "SAGEFS_WARMUP_MAX_MINUTES"
            d.Render saved |> Expect.equal "and the saved value" "25"
          | SettingsCatalog.NotOverridden -> failtest "the variable overrides the saved value")

    testCase "WHY — nothing overrides when the saved value is the one in force" <| fun _ ->
      withTempDir (fun dir ->
        saveGlobal dir "waits.warmupMaxMinutes" "25"
        let d = EnvBackedSettings.warmupMax.Descriptor
        match SettingsCatalog.resolveWith ExternalInputs.none (noRepo dir) d with
        | Error why -> failtestf "expected Ok, got %A" why
        | Ok p -> SettingsCatalog.overrideNote d p |> Expect.equal "not overridden" SettingsCatalog.NotOverridden)

    testProperty "WHY — the winner is always the highest-ranked layer present, over all six layers" <| fun (g: bool) (r: bool) (s: bool) (e: bool) (f: bool) ->
      let set =
        [ if g then yield LGlobal, VToggle On
          if r then yield LRepo, VToggle On
          if s then yield LSession, VToggle On
          if e then yield LEnvironment, VToggle On
          if f then yield LCommandLine, VToggle On ]
      let p = SettingsResolver.resolve (VToggle Off) set
      let expected =
        if f then LCommandLine
        elif e then LEnvironment
        elif s then LSession
        elif r then LRepo
        elif g then LGlobal
        else LDefault
      p.Source = expected

    testCase "WHY — the layers rank in the order chooseMcpPort already uses, so the page and the daemon agree" <| fun _ ->
      let ranked = [ LDefault; LGlobal; LRepo; LSession; LEnvironment; LCommandLine ] |> List.map ConfigLayer.rank
      ranked |> Expect.equal "strictly increasing" [ 0; 1; 2; 3; 4; 5 ]
  ]

/// What the bind-host row said before: the file layers alone, so with SAGEFS_BIND_HOST=::1 it said localhost, source
/// default. The daemon takes the bind host from the variable alone.
[<Tests>]
let bindHostTruthTests =
  testList "Settings — the bind host row tells the truth" [

    testCase "WHY — with SAGEFS_BIND_HOST set the row's value is the variable's, with the environment as its source" <| fun _ ->
      withTempDir (fun dir ->
        let row = SettingsPanel.rowFor (env [ "SAGEFS_BIND_HOST", "::1" ]) (noRepo dir) SettingsCatalog.bindHost
        match row.Resolved with
        | Error why -> failtestf "expected Ok, got %A" why
        | Ok p ->
          p.Source |> Expect.equal "source is the environment" LEnvironment
          p.Effective |> Expect.equal "effective is the IPv6 loopback" (VBindHost SageFsConfig.LoopbackHost.Ipv6))

    testCase "WHY — the bind host row has no edit control, because changing it from a page the daemon serves points the trust the wrong way" <| fun _ ->
      withTempDir (fun dir ->
        let row = SettingsPanel.rowFor (env [ "SAGEFS_BIND_HOST", "::1" ]) (noRepo dir) SettingsCatalog.bindHost
        let html = renderNode (SettingsPanel.renderPanel SettingsPanel.Quiet NoRepoCheckout [ row ])
        html |> Expect.stringContains "says it is read-only" "read-only"
        (html.Contains "settings-input") |> Expect.isFalse "no input"
        (html.Contains "/dashboard/settings/edit/") |> Expect.isFalse "no save button")

    testCase "WHY — a bind host key in a layer file is not a value, because nothing reads it back" <| fun _ ->
      withTempDir (fun dir ->
        saveGlobal dir SettingsCatalog.bindHost.Key "::1"
        match SettingsCatalog.resolveWith ExternalInputs.none (noRepo dir) SettingsCatalog.bindHost with
        | Error why -> failtestf "expected Ok, got %A" why
        | Ok p -> p.Source |> Expect.equal "still the default" LDefault)

    testCase "WHY — an edit aimed at a read-only setting is refused with its reason and writes nothing" <| fun _ ->
      withTempDir (fun dir ->
        SettingsCatalog.edit (noRepo dir) LGlobal "127.0.0.1" SettingsCatalog.bindHost |> Expect.isError "refused"
        SettingsStore.readLayer (SettingsStore.globalPath dir) |> Map.tryFind SettingsCatalog.bindHost.Key
        |> Expect.isNone "nothing persisted")
  ]

[<Tests>]
let repoLayerTests =
  testList "Settings — the repo layer is the session's directory" [

    testCase "WHY — the repo layer file is the session's working directory's .SageFs/settings.json" <| fun _ ->
      withTempDir (fun dir ->
        let work = Path.Combine(dir, "work")
        Directory.CreateDirectory work |> ignore
        match (SettingsCatalog.pathsFor dir work).Repo with
        | RepoRootAt root -> SettingsStore.repoPath root |> Expect.equal "the layer file" (Path.Combine(work, ".SageFs", "settings.json"))
        | NoRepoCheckout -> failtest "an existing directory is a repo layer")

    testCase "WHY — a blank or missing directory has no repo layer, and a repo edit is refused with LayerUnavailable" <| fun _ ->
      withTempDir (fun dir ->
        for working in [ ""; "   "; Path.Combine(dir, "gone") ] do
          let paths = SettingsCatalog.pathsFor dir working
          paths.Repo |> Expect.equal (sprintf "no layer for '%s'" working) NoRepoCheckout
          match SettingsCatalog.edit paths LRepo "30" SettingsCatalog.perTestTimeout with
          | Error (LayerUnavailable _) -> ()
          | other -> failtestf "expected LayerUnavailable, got %A" other)

    testCase "WHY — the page's edit target names the directory it will write, and offers no repo target without one" <| fun _ ->
      let withRepo = renderNode (SettingsPanel.renderPanel SettingsPanel.Quiet (RepoRootAt "/work/app") [])
      withRepo |> Expect.stringContains "names the directory" "This repo (/work/app)"
      let without = renderNode (SettingsPanel.renderPanel SettingsPanel.Quiet NoRepoCheckout [])
      (without.Contains "This repo") |> Expect.isFalse "no repo option"
  ]

let private firstBatch : EnvBacked list = EnvBackedRows.all

[<Tests>]
let roundTripTests =
  testList "Settings — a descriptor's text is its value" [

    testProperty "WHY — a duration setting parses what it renders, for every legal amount in its unit" <| fun (n: int) ->
      [ EnvBackedSettings.warmupInactivity; EnvBackedSettings.warmupMax; EnvBackedSettings.buildTimeout; EnvBackedSettings.hostBuildTimeout ]
      |> List.forall (fun setting ->
        let d = setting.Descriptor
        let amount = 1 + (abs (n % 1000))
        match d.Parse (string amount) with
        | Error _ -> false
        | Ok v -> d.Parse (d.Render v) = Ok v)

    testProperty "WHY — a text that is not a positive number of the unit is refused, so an illegal wait is unrepresentable" <| fun (s: string) ->
      let d = EnvBackedSettings.warmupInactivity.Descriptor
      match d.Parse (match s with null -> "" | text -> text) with
      | Ok (VDuration limit) ->
        let seconds = (WaitLimit.value limit).TotalSeconds
        seconds >= 1.0 && seconds <= 86400.0
      | Ok _ -> false
      | Error _ -> true

    testCase "WHY — zero hours is a real answer for the reaper and means off, written as 0 and read as off" <| fun _ ->
      let d = EnvBackedSettings.abandonedSessionHours.Descriptor
      d.Parse "0" |> Expect.equal "off" (Ok (VLimit LimitOff))
      d.Render (VLimit LimitOff) |> Expect.equal "written as 0" "0"
      d.Parse "-1" |> Expect.isError "negative hours are refused"
      d.Parse "12" |> Result.map d.Render |> Expect.equal "twelve hours round-trips" (Ok "12")

    testCase "WHY — the machine tier offers auto and every tier, and nothing else" <| fun _ ->
      let d = EnvBackedSettings.machineTier.Descriptor
      for name in "auto" :: (MachineTier.all |> List.map (fun tier -> (MachineTier.toString tier).ToLowerInvariant())) do
        d.Parse name |> Expect.isOk (sprintf "%s is a choice" name)
      d.Parse "turbo" |> Expect.isError "turbo is not one"

    testCase "WHY — every first-batch descriptor's default parses back from its own rendering" <| fun _ ->
      for setting in firstBatch do
        let d = setting.Descriptor
        d.Parse (d.Render d.Default) |> Expect.equal (sprintf "%s default round-trips" d.Key) (Ok d.Default)
  ]

/// The import the daemon runs first thing, before `Timeouts` is read.
[<Tests>]
let bootstrapTests =
  testList "Settings — the start-up import" [

    let run (saved: (string * string) list) (set: (string * string) list) (f: ImportOutcome list -> (string * string) list -> unit) =
      withTempDir (fun dir ->
        for key, raw in saved do saveGlobal dir key raw
        let written = ResizeArray<string * string>()
        let outcomes =
          SettingsBootstrap.importSavedWith
            (fun name -> match List.tryFind (fun (n, _) -> n = name) set with Some (_, v) -> EnvSet v | None -> EnvUnset)
            (fun name text -> written.Add((name, text)))
            dir
            EnvBackedRows.all
        f outcomes (List.ofSeq written))

    testCase "WHY — a saved wait becomes the variable when the person set none, so Timeouts reads what the page saved" <| fun _ ->
      run [ "waits.warmupInactivitySeconds", "90" ] [] (fun outcomes written ->
        written |> Expect.equal "the variable is set to the saved text" [ "SAGEFS_WARMUP_INACTIVITY_SECONDS", "90" ]
        outcomes |> Expect.equal "and the import says so" [ ImportOutcome.Imported("SAGEFS_WARMUP_INACTIVITY_SECONDS", "90") ])

    testCase "WHY — a variable the person set wins over the saved value and is left alone" <| fun _ ->
      run [ "waits.warmupMaxMinutes", "25" ] [ "SAGEFS_WARMUP_MAX_MINUTES", "15" ] (fun outcomes written ->
        written |> Expect.isEmpty "nothing written"
        outcomes |> Expect.equal "the import says the environment kept it" [ ImportOutcome.LeftToEnvironment "SAGEFS_WARMUP_MAX_MINUTES" ])

    testCase "WHY — a saved value that does not parse is reported and the variable is left as it was" <| fun _ ->
      run [ "waits.buildTimeoutMinutes", "abc" ] [] (fun outcomes written ->
        written |> Expect.isEmpty "nothing written"
        match outcomes with
        | [ ImportOutcome.NotUsable(variable, reason) ] ->
          variable |> Expect.equal "names the variable" "SAGEFS_BUILD_TIMEOUT_MINUTES"
          reason |> Expect.stringContains "says which key and why" "waits.buildTimeoutMinutes"
        | other -> failtestf "expected one NotUsable, got %A" other)

    testCase "WHY — a saved auto tier leaves the variable unset, because auto means let SageFs measure the machine" <| fun _ ->
      run [ "waits.machineTier", "auto" ] [] (fun outcomes written ->
        written |> Expect.isEmpty "nothing written"
        outcomes |> Expect.equal "the import says it means unset" [ ImportOutcome.SavedMeansUnset MachineTier.envVar ])

    testCase "WHY — a saved tier is the variable a person would have set, so the machine tier reads it as theirs" <| fun _ ->
      run [ "waits.machineTier", "constrained" ] [] (fun _ written ->
        written |> Expect.equal "the tier variable" [ MachineTier.envVar, "constrained" ])

    testCase "WHY — a saved reaper bound of 0 is written as 0, the spelling the variable's reader treats as off" <| fun _ ->
      run [ "waits.abandonedSessionHours", "0" ] [] (fun _ written ->
        written |> Expect.equal "zero hours" [ "SAGEFS_ABANDONED_SESSION_HOURS", "0" ])

    testCase "WHY — an unreadable layer file is one report, not a row per setting" <| fun _ ->
      withTempDir (fun dir ->
        File.WriteAllText(SettingsStore.globalPath dir, "{ this is not json")
        let outcomes =
          SettingsBootstrap.importSavedWith (fun _ -> EnvUnset) (fun _ _ -> failtest "nothing may be written") dir EnvBackedRows.all
        match outcomes with
        | [ ImportOutcome.NotUsable(_, reason) ] -> reason |> Expect.stringContains "names the file" "settings.json"
        | other -> failtestf "expected one NotUsable, got %A" other)

    testCase "WHY — flagsOf reads the two flags that set a setting and ignores the rest of the line" <| fun _ ->
      SettingsBootstrap.flagsOf [ "--mcp-port"; "4000"; "--no-resume"; "--ttl"; "30m" ]
      |> Expect.equal "the port and the switch" (Map.ofList [ "--mcp-port", "4000"; "--no-resume", "on" ])

    testCase "WHY — every setting the import writes names the variable its row shows, so the page and the import cannot disagree" <| fun _ ->
      for setting in EnvBackedRows.all do
        ExternalSource.variable setting.Descriptor.External
        |> Expect.equal (sprintf "%s names its variable" setting.Descriptor.Key) (Some setting.Variable)
  ]

/// What a row says about the value in use and about what overrides what it saved.
[<Tests>]
let rowTextTests =
  testList "Settings — the row says what is in use" [

    let render (row: SettingsPanel.SettingRow) = renderNode (SettingsPanel.renderPanel SettingsPanel.Quiet NoRepoCheckout [ row ])

    testCase "WHY — a saved value ignored because the variable is set says so, naming the variable" <| fun _ ->
      withTempDir (fun dir ->
        saveGlobal dir "waits.warmupMaxMinutes" "25"
        let row = SettingsPanel.rowFor (env [ "SAGEFS_WARMUP_MAX_MINUTES", "15" ]) (noRepo dir) EnvBackedSettings.warmupMax.Descriptor
        let html = render row
        html |> Expect.stringContains "names the variable" "Saved 25. Ignored while SAGEFS_WARMUP_MAX_MINUTES is set."
        html |> Expect.stringContains "the source badge says environment" "settings-badge-environment")

    testCase "WHY — a flag that wins says which flag" <| fun _ ->
      withTempDir (fun dir ->
        saveGlobal dir SettingsCatalog.mcpPort.Key "6000"
        let inputs = { ExternalInputs.none with Flags = Map.ofList [ "--mcp-port", "4000" ] }
        let html = render (SettingsPanel.rowFor inputs (noRepo dir) SettingsCatalog.mcpPort)
        html |> Expect.stringContains "names the flag" "Saved 6000. Ignored while the daemon runs with --mcp-port."
        html |> Expect.stringContains "the source badge says flag" "settings-badge-flag")

    testCase "WHY — a restart-required row says when it applies, in a word and in a sentence" <| fun _ ->
      withTempDir (fun dir ->
        let html = render (SettingsPanel.rowFor ExternalInputs.none (noRepo dir) EnvBackedSettings.buildTimeout.Descriptor)
        html |> Expect.stringContains "the chip word" ">restart<"
        html |> Expect.stringContains "the sentence" "applies after the daemon restarts")

    testCase "WHY — a live row says saving applies it now" <| fun _ ->
      withTempDir (fun dir ->
        let html = render (SettingsPanel.rowFor ExternalInputs.none (noRepo dir) SettingsCatalog.perTestTimeout)
        html |> Expect.stringContains "the sentence" "Saving applies it now.")

    testCase "WHY — a saved value the subsystem has not picked up yet shows what is in use and that a restart applies the saved one" <| fun _ ->
      withTempDir (fun dir ->
        // Timeouts.buildCompletion is whatever this test process started with; a saved value that differs from it is the case.
        let inUse = Timeouts.buildCompletion.TotalMinutes
        let saved = (if inUse = 41.0 then 42.0 else 41.0)
        saveGlobal dir "waits.buildTimeoutMinutes" (sprintf "%g" saved)
        let html = render (SettingsPanel.rowFor ExternalInputs.none (noRepo dir) EnvBackedSettings.buildTimeout.Descriptor)
        html |> Expect.stringContains "says what is in use" (sprintf "In use now: %g." inUse)
        html |> Expect.stringContains "says the saved value waits for a restart" "The saved value applies after a restart.")

    testCase "WHY — a saved value that is the one in use adds no in-use sentence" <| fun _ ->
      withTempDir (fun dir ->
        saveGlobal dir "waits.buildTimeoutMinutes" (sprintf "%g" Timeouts.buildCompletion.TotalMinutes)
        let html = render (SettingsPanel.rowFor ExternalInputs.none (noRepo dir) EnvBackedSettings.buildTimeout.Descriptor)
        (html.Contains "In use now") |> Expect.isFalse "nothing to add")

    testCase "WHY — Reset appears only when a file holds a value, so an environment override alone has nothing to clear" <| fun _ ->
      withTempDir (fun dir ->
        let d = EnvBackedSettings.warmupMax.Descriptor
        let onlyEnv = render (SettingsPanel.rowFor (env [ "SAGEFS_WARMUP_MAX_MINUTES", "15" ]) (noRepo dir) d)
        (onlyEnv.Contains "settings-btn-clear") |> Expect.isFalse "no reset"
        saveGlobal dir d.Key "25"
        let saved = render (SettingsPanel.rowFor (env [ "SAGEFS_WARMUP_MAX_MINUTES", "15" ]) (noRepo dir) d)
        saved |> Expect.stringContains "reset is there" "settings-btn-clear")

    testCase "WHY — every row the page draws has a chip word, so no row leaves the person guessing when it applies" <| fun _ ->
      withTempDir (fun dir ->
        for d in SettingsCatalog.all do
          let html = render (SettingsPanel.rowFor ExternalInputs.none (noRepo dir) d)
          let word =
            match d.Applicability with
            | Live -> ">live<"
            | RestartRequired -> ">restart<"
            | ReadOnly _ -> ">read-only<"
          (html.Contains word || html.Contains ("live<")) |> Expect.isTrue (sprintf "%s shows its chip" d.Key))
  ]

/// The rows the first batch did not cover: every wait, age, count and switch that `Timeouts.fs` and `SageFsConfig.fs`
/// read, with the read-only rows for the ones that are not for editing.
[<Tests>]
let moreRowsTests =
  testList "Settings — every variable has a row of its own" [

    testCase "WHY — every row has a key, a name, a help line and a variable of its own, so none is a copy of another" <| fun _ ->
      let all = SettingsCatalog.all @ [ SessionAgent.reflectionReadModeSetting; SessionAgent.tieredCompilationSetting ]
      let keys = all |> List.map (fun d -> d.Key)
      keys |> List.distinct |> List.length |> Expect.equal "keys are distinct" keys.Length
      let variables = all |> List.choose (fun d -> ExternalSource.variable d.External)
      variables |> List.distinct |> List.length |> Expect.equal "variables are distinct" variables.Length
      for d in all do
        String.IsNullOrWhiteSpace d.Name |> Expect.isFalse (sprintf "%s has a name" d.Key)
        (d.Description.Trim().Length > 20) |> Expect.isTrue (sprintf "%s has a help line" d.Key)
        d.Description.EndsWith "." |> Expect.isTrue (sprintf "the help line of %s is a sentence" d.Key)

    testCase "WHY — a row that is read-only says why, and the reason is one of the closed set" <| fun _ ->
      let readOnly = SettingsCatalog.all |> List.choose (fun d -> match d.Applicability with ReadOnly reason -> Some (d, reason) | Live | RestartRequired -> None)
      (List.length readOnly > 8) |> Expect.isTrue "there are read-only rows"
      for d, reason in readOnly do
        ReadOnlyReason.describe reason |> Expect.stringStarts (sprintf "%s says read-only" d.Key) "read-only here:"

    testCase "WHY — the test-harness variables are read-only rows in their own group, so a person is not offered a knob that only the runner turns" <| fun _ ->
      let harness = SettingsCatalog.all |> List.filter (fun d -> d.Category = TestHarness)
      harness |> List.length |> Expect.equal "five harness deadlines" 5
      for d in harness do
        d.Applicability |> Expect.equal (sprintf "%s is read-only for the harness" d.Key) (ReadOnly TestHarnessOnly)

    testProperty "WHY — a row's parser never throws, whatever text it is given, and what it accepts it renders back to the same value" <| fun (text: string) ->
      let text = match text with null -> "" | t -> t
      EnvBackedRows.all
      |> List.forall (fun setting ->
        let d = setting.Descriptor
        match d.Parse text with
        | Error _ -> true
        | Ok v ->
          // Rendering keeps six significant digits, so the first render may round; after that it is stable.
          d.Parse (d.Render v) |> Result.map d.Render = Ok (d.Render v))

    testProperty "WHY — a count is accepted exactly between its least and the largest, so no count row holds an illegal number" <| fun (n: int) ->
      let d = EnvBackedRows.descriptors |> List.find (fun d -> d.Key = "data.gateLogsKept")
      match d.Parse (string n) with
      | Ok _ -> n >= 1 && n <= Timeouts.settingCountLargest
      | Error _ -> n < 1 || n > Timeouts.settingCountLargest

    testProperty "WHY — an age is accepted exactly when it is more than zero days and at most ten years" <| fun (days: float) ->
      let d = EnvBackedRows.descriptors |> List.find (fun d -> d.Key = "data.frictionMaxAgeDays")
      let text = days.ToString("R", Globalization.CultureInfo.InvariantCulture)
      match d.Parse text with
      | Ok _ -> days > 0.0 && days <= Timeouts.settingAgeLongestDays
      // A number of days too small to be one tick of a span is refused, too: it would be zero.
      | Error _ -> not (days > 0.0 && days <= Timeouts.settingAgeLongestDays) || TimeSpan.FromDays days = TimeSpan.Zero

    testCase "WHY — a number that would overflow a span is refused by every row, not thrown" <| fun _ ->
      // The editor row accepts any text on purpose (text naming no editor is VS Code, as the links already did).
      let refusing = EnvBackedRows.all |> List.filter (fun s -> s.Descriptor.Key <> EnvBackedRows.editor.Descriptor.Key)
      for text in [ "Infinity"; "-Infinity"; "NaN"; "1e999"; "1e308"; "9223372036854775807" ] do
        for setting in refusing do
          setting.Descriptor.Parse text |> Expect.isError (sprintf "%s refuses %s" setting.Descriptor.Key text)

    testCase "WHY — the memory ceiling is auto until a positive number of megabytes is named" <| fun _ ->
      let d = EnvBackedRows.referencesResidentCeiling.Descriptor
      d.Parse "auto" |> Expect.equal "auto" (Ok d.Default)
      d.Parse "" |> Expect.equal "blank is auto" (Ok d.Default)
      d.Parse "4096" |> Result.map d.Render |> Expect.equal "4096 round-trips" (Ok "4096")
      d.Parse "0" |> Expect.isError "zero would drop the cache on every ask"
      EnvBackedRows.referencesResidentCeiling.EnvText d.Default |> Expect.isNone "auto leaves the variable unset"

    testCase "WHY — the retention rows write days as the variable reads them, so a saved 45 is 45 days in the daemon" <| fun _ ->
      let row = EnvBackedRows.all |> List.find (fun s -> s.Variable = "SAGEFS_FRICTION_MAX_AGE_DAYS")
      match row.Descriptor.Parse "45" with
      | Error why -> failtestf "expected Ok, got %A" why
      | Ok v -> row.EnvText v |> Expect.equal "the variable text" (Some "45")

    testCase "WHY — the variable's own spelling of zero minutes is not a wait, so a saved 0 is refused instead of switching a timer off" <| fun _ ->
      let row = EnvBackedRows.all |> List.find (fun s -> s.Variable = "SAGEFS_ORPHAN_SWEEP_MINUTES")
      row.Descriptor.Parse "0" |> Expect.isError "zero is refused"

    testCase "WHY — every row of a wait that is scaled by the machine tier says so, and no row that is not scaled does" <| fun _ ->
      let scaledDefaults = [ "SAGEFS_FSI_HOST_STARTUP_SECONDS"; "SAGEFS_STOP_GRACEFUL_SECONDS"; "SAGEFS_GIT_QUICK_SECONDS" ]
      let unscaledDefaults = [ "SAGEFS_CONFIRM_ANSWER_SECONDS"; "SAGEFS_AGENT_CLEANUP_SECONDS"; "SAGEFS_WATCHER_SYNC_SECONDS" ]
      let help (variable: string) = (EnvBackedRows.all |> List.find (fun s -> s.Variable = variable)).Descriptor.Description
      for v in scaledDefaults do help v |> Expect.stringContains (sprintf "%s names the tier" v) "scaled for slower ones"
      for v in unscaledDefaults do (help v).Contains "scaled" |> Expect.isFalse (sprintf "%s is not scaled" v)
  ]

/// One reader for a variable that several places must agree on.
[<Tests>]
let oneReaderTests =
  testList "Settings — one reader for each variable that several places read" [

    testCase "WHY — SAGEFS_DEVRELOAD: unset is on, 0 and false in any case are off, anything else is on" <| fun _ ->
      let parse (text: string | null) = DevReloadMode.parse text
      parse null |> Expect.equal "unset" DevReloadMode.Enabled
      for off in [ "0"; "false"; "FALSE"; " False "; " 0 " ] do
        parse off |> Expect.equal (sprintf "'%s' is off" off) DevReloadMode.Disabled
      for on in [ "1"; "true"; "yes"; ""; "banana" ] do
        parse on |> Expect.equal (sprintf "'%s' is on" on) DevReloadMode.Enabled

    testCase "WHY — SAGEFS_EDITOR wins over EDITOR, EDITOR is read when it is blank, and neither means VS Code" <| fun _ ->
      EditorChoice.choose "cursor" "rider" |> Expect.equal "SAGEFS_EDITOR wins" EditorChoice.Cursor
      EditorChoice.choose "" "rider" |> Expect.equal "blank falls to EDITOR" EditorChoice.JetBrains
      EditorChoice.choose null "/opt/jetbrains/bin/idea" |> Expect.equal "a path names its editor" EditorChoice.JetBrains
      EditorChoice.choose null null |> Expect.equal "neither is VS Code" EditorChoice.VsCode
      EditorChoice.choose "nvim" "cursor" |> Expect.equal "an editor with no link scheme is VS Code" EditorChoice.VsCode

    testProperty "WHY — an editor text always names one of the three, so the link is never undefined" <| fun (a: string) (b: string) ->
      List.contains (EditorChoice.choose a b) EditorChoice.all

    testCase "WHY — the editor row judges text the way the links do, so a variable holding a path shows the editor the links open" <| fun _ ->
      let d = EnvBackedRows.editor.Descriptor
      d.Parse "/usr/bin/nvim" |> Result.map d.Render |> Expect.equal "unknown editor" (Ok "vscode")
      d.Parse "Cursor" |> Result.map d.Render |> Expect.equal "any case" (Ok "cursor")
      d.Parse "auto" |> Expect.equal "auto" (Ok d.Default)

    testCase "WHY — each of these variables is read by one function, so no other file reads it from the environment" <| fun _ ->
      let root = RepoPaths.repoPathFull [||]
      let readers (variable: string) =
        [ for project in [ "SageFs"; "SageFs.Core"; "SageFs.Host" ] do
            for file in Directory.EnumerateFiles(Path.Combine(root, project), "*.fs", SearchOption.AllDirectories) do
              let relative = Path.GetRelativePath(root, file).Replace('\\', '/')
              match relative.Contains "/obj/" || relative.Contains "/bin/" with
              | true -> ()
              | false ->
                let text = File.ReadAllText file
                match text.Contains (sprintf "GetEnvironmentVariable(\"%s\")" variable) || text.Contains (sprintf "GetEnvironmentVariable \"%s\"" variable) with
                | true -> yield relative
                | false -> () ]
      readers "SAGEFS_DEVRELOAD" |> Expect.isEmpty "SAGEFS_DEVRELOAD is read through DevReloadMode"
      readers "SAGEFS_EDITOR" |> Expect.isEmpty "SAGEFS_EDITOR is read through EditorChoice"
  ]
  |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant

/// Every `SAGEFS_*` variable that `Timeouts.fs` and `SageFsConfig.fs` read is the variable of a row on the Settings
/// page. The count of those without a row is a budget table, so a new variable fails until it has a row, and
/// `--ratchets --tighten` is how the number goes down.
module private WithoutARow =
  open System.Text.RegularExpressions

  let private repoRoot = RepoPaths.repoPathFull [||]
  let private quoted = Regex("\"(SAGEFS_[A-Z0-9_]+)\"", RegexOptions.Compiled)

  let private declaredIn (file: string) : Set<string> =
    File.ReadAllLines(Path.Combine(repoRoot, "SageFs.Core", file))
    |> Array.filter (fun line -> not (line.TrimStart().StartsWith "//"))
    |> Array.collect (fun line -> quoted.Matches line |> Seq.map (fun m -> m.Groups.[1].Value) |> Seq.toArray)
    |> Set.ofArray

  /// Variables with a row: a descriptor names them as its environment variable.
  let withARow : Set<string> =
    SettingsCatalog.all
    |> List.choose (fun d -> ExternalSource.variable d.External)
    |> Set.ofList

  /// Read by Timeouts.fs or SageFsConfig.fs and without a row.
  let withoutARow : Set<string> =
    Set.union (declaredIn "Timeouts.fs") (declaredIn "SageFsConfig.fs") - withARow

  /// Every variable the product's own source spells as a literal outside the files that declare the rows.
  let readByTheProduct : Set<string> =
    [ for project in [ "SageFs"; "SageFs.Core"; "SageFs.Host" ] do
        let dir = Path.Combine(repoRoot, project)
        for file in Directory.EnumerateFiles(dir, "*.fs", SearchOption.AllDirectories) do
          let relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/')
          let own = relative.EndsWith "EnvBackedSettings.fs" || relative.EndsWith "EnvBackedRows.fs" || relative.EndsWith "SettingsCatalog.fs"
          match own || relative.Contains "/obj/" || relative.Contains "/bin/" with
          | true -> ()
          | false ->
            for line in File.ReadAllLines file do
              match line.TrimStart().StartsWith "//" with
              | true -> ()
              | false -> for m in quoted.Matches line do yield m.Groups.[1].Value ]
    |> Set.ofList

/// The budget table as data, so `--ratchets --tighten` can lower it.
let budgets : (string * int) list =
  [ "variables read without a row", 0 ]

let _withoutARowTable =
  TestInfrastructure.Ratchet.table
    { Name = "variables without a settings row"
      SourceFile = "SageFs.Tests/SettingsExternalTests.fs"
      Budgets = budgets
      Actual = fun () -> [ "variables read without a row", WithoutARow.withoutARow.Count ] }

[<Tests>]
let notEditableRatchet =
  testList "Settings — every variable has a row" [
    testCase "WHY — no variable Timeouts.fs or SageFsConfig.fs reads is without a row, and a new one fails until it has one" <| fun _ ->
      let allowed = budgets |> List.head |> snd
      (WithoutARow.withoutARow.Count <= allowed)
      |> Expect.isTrue (sprintf "%d variables are read without a row (budget %d). Give each a row, or a read-only row that says why: %A" WithoutARow.withoutARow.Count allowed (Set.toList WithoutARow.withoutARow))
      (WithoutARow.withoutARow.Count = allowed)
      |> Expect.isTrue (sprintf "the budget is %d but %d variables are without a row: run --ratchets --tighten" allowed WithoutARow.withoutARow.Count)

    testCase "WHY — every variable a row names is read by the product outside the row's own file, so a row cannot name a variable nothing reads" <| fun _ ->
      for variable in WithoutARow.withARow do
        WithoutARow.readByTheProduct |> Set.contains variable |> Expect.isTrue (sprintf "%s is read somewhere besides its row" variable)

    testCase "WHY — a duration row's default is the number Timeouts.fs spells next to its variable, so the page starts from the real default" <| fun _ ->
      let pattern = System.Text.RegularExpressions.Regex("\"(SAGEFS_[A-Z0-9_]+)\"\\s+([0-9_.]+)\\s*$")
      let inSource =
        File.ReadAllLines(Path.Combine(RepoPaths.repoPathFull [||], "SageFs.Core", "Timeouts.fs"))
        |> Array.filter (fun line -> not (line.TrimStart().StartsWith "//"))
        |> Array.choose (fun line ->
          let m = pattern.Match line
          match m.Success with
          | true -> Some (m.Groups.[1].Value, float (m.Groups.[2].Value.Replace("_", "")))
          | false -> None)
        |> Map.ofArray
      let durations =
        EnvBackedRows.all
        |> List.filter (fun s -> (SettingValue.duration s.Descriptor.Default).IsSome || (SettingValue.limit s.Descriptor.Default).IsSome)
      let checkedAgainstSource =
        durations
        |> List.choose (fun setting ->
          inSource
          |> Map.tryFind setting.Variable
          |> Option.map (fun expected -> setting, expected))
      (checkedAgainstSource.Length >= 40) |> Expect.isTrue (sprintf "the scan found the numbers of at least 40 duration rows, found %d" checkedAgainstSource.Length)
      for setting, expected in checkedAgainstSource do
        float (setting.Descriptor.Render setting.Descriptor.Default)
        |> Expect.equal (sprintf "%s: the row's default" setting.Variable) expected
  ]
  |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant
