module SageFs.Tests.SettingsTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open SageFs

let private withTempDir (run: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-settings-" + Guid.NewGuid().ToString("N").[..7])
  Directory.CreateDirectory dir |> ignore
  try run dir
  finally try Directory.Delete(dir, true) with _ -> ()

/// Phase A of unified-settings-design.md: the typed config core. The load-
/// bearing property is requirement #4 — illegal configuration is
/// unrepresentable: a value type's only inhabitants are legal, so `create`
/// succeeds exactly when the input is in range / in set, and a constructed
/// value always round-trips.
[<Tests>]
let tests =
  testList "Settings core" [

    // -- Port: only the unprivileged range is constructable --

    testCase "WHY — Port.create rejects a privileged port because the daemon can't bind <1024 without root" <| fun _ ->
      Port.create 80 |> Expect.isError "port 80 is below 1024"

    testCase "WHY — Port.create rejects an out-of-range port because 65536 is not a TCP port" <| fun _ ->
      Port.create 70000 |> Expect.isError "70000 exceeds 65535"

    testCase "WHY — Port.create accepts the default MCP port and round-trips it because in-range values are legal" <| fun _ ->
      Port.create SageFsConfig.DefaultMcpPort
      |> Result.map Port.value
      |> Expect.equal "37749 round-trips" (Ok SageFsConfig.DefaultMcpPort)

    testProperty "WHY — Port.create succeeds exactly for the unprivileged range because that is the only legal set" <| fun (n: int) ->
      match Port.create n with
      | Ok p -> n >= 1024 && n <= 65535 && Port.value p = n
      | Error _ -> not (n >= 1024 && n <= 65535)

    // -- EnumValue: chosen is provably a member of allowed --

    testCase "WHY — EnumValue.create rejects a non-member because a closed set must exclude outsiders" <| fun _ ->
      EnumValue.create [ "dark"; "light" ] "neon" |> Expect.isError "neon is not a member"

    testCase "WHY — EnumValue.create accepts a member and round-trips it because members are legal" <| fun _ ->
      EnumValue.create [ "dark"; "light" ] "dark"
      |> Result.map EnumValue.value
      |> Expect.equal "dark round-trips" (Ok "dark")

    testProperty "WHY — EnumValue.create succeeds exactly for members because a value outside the set is unrepresentable" <| fun (allowed: string list) (raw: string) ->
      match EnumValue.create allowed raw with
      | Ok e -> List.contains raw allowed && EnumValue.value e = raw
      | Error _ -> not (List.contains raw allowed)

    // -- Resolver: precedence + provenance --

    testCase "WHY — resolve returns the default when no layer is set because LDefault is always present" <| fun _ ->
      let p = SettingsResolver.resolve (VToggle Off) []
      p.Source |> Expect.equal "source is default" LDefault
      p.Effective |> Expect.equal "effective is the default" (VToggle Off)

    testCase "WHY — repo overrides global because a higher layer wins" <| fun _ ->
      let p = SettingsResolver.resolve (VToggle Off) [ LGlobal, VToggle On; LRepo, VToggle Off ]
      p.Source |> Expect.equal "repo is the source" LRepo
      p.Effective |> Expect.equal "repo's value wins" (VToggle Off)

    testCase "WHY — session overrides repo and global because it is the highest layer" <| fun _ ->
      let p = SettingsResolver.resolve (VToggle Off) [ LGlobal, VToggle On; LRepo, VToggle Off; LSession, VToggle On ]
      p.Source |> Expect.equal "session is the source" LSession

    testCase "WHY — provenance records every set layer so the UI can show base-vs-override" <| fun _ ->
      let p = SettingsResolver.resolve (VToggle Off) [ LGlobal, VToggle On; LRepo, VToggle Off ]
      p.PerLayer |> List.map fst
      |> Expect.equal "all three layers, lowest-first" [ LDefault; LGlobal; LRepo ]

    testProperty "WHY — resolve's source is always the highest-ranked layer present because precedence is total" <| fun (useGlobal: bool) (useRepo: bool) (useSession: bool) ->
      let set =
        [ if useGlobal then yield LGlobal, VToggle On
          if useRepo then yield LRepo, VToggle On
          if useSession then yield LSession, VToggle On ]
      let p = SettingsResolver.resolve (VToggle Off) set
      let expected =
        if useSession then LSession
        elif useRepo then LRepo
        elif useGlobal then LGlobal
        else LDefault
      p.Source = expected
  ]

/// Phase A3: the layer store persists to machine-safe settings.json files,
/// reads fail-safe, and clearing a key falls back to the lower layer.
[<Tests>]
let storeTests =
  testList "Settings store" [

    testCase "WHY — a missing layer file reads as empty because resolution must never fail on absence" <| fun _ ->
      withTempDir (fun dir ->
        SettingsStore.readLayer (Path.Combine(dir, "settings.json"))
        |> Expect.isEmpty "no file yields the empty layer")

    testCase "WHY — a malformed layer file reads as empty because a corrupt file must not throw mid-resolution" <| fun _ ->
      withTempDir (fun dir ->
        let path = Path.Combine(dir, "settings.json")
        File.WriteAllText(path, "{ this is not valid json ]")
        SettingsStore.readLayer path |> Expect.isEmpty "malformed file yields the empty layer")

    testCase "WHY — setKey then readLayer round-trips because a persisted value must be readable back" <| fun _ ->
      withTempDir (fun dir ->
        let path = SettingsStore.globalPath dir
        SettingsStore.setKey path "livetest.perTestTimeoutSeconds" "30" |> Expect.isOk "write succeeds"
        SettingsStore.readLayer path |> Map.tryFind "livetest.perTestTimeoutSeconds"
        |> Expect.equal "value round-trips" (Some "30"))

    testCase "WHY — setKey preserves other keys because a single edit must not drop the rest of the layer" <| fun _ ->
      withTempDir (fun dir ->
        let path = SettingsStore.globalPath dir
        SettingsStore.setKey path "a" "1" |> ignore
        SettingsStore.setKey path "b" "2" |> ignore
        let m = SettingsStore.readLayer path
        (Map.tryFind "a" m, Map.tryFind "b" m)
        |> Expect.equal "both keys survive" (Some "1", Some "2"))

    testCase "WHY — clearKey removes a repo override so the value falls back to the lower layer" <| fun _ ->
      withTempDir (fun dir ->
        let path = SettingsStore.repoPath dir
        SettingsStore.setKey path "theme" "dark" |> ignore
        SettingsStore.clearKey path "theme" |> Expect.isOk "clear succeeds"
        SettingsStore.readLayer path |> Map.tryFind "theme"
        |> Expect.isNone "the repo override is gone, so it falls back")

    testCase "WHY — clearing an absent key is a no-op success because there is nothing to remove" <| fun _ ->
      withTempDir (fun dir ->
        SettingsStore.clearKey (SettingsStore.globalPath dir) "never-set"
        |> Expect.isOk "clearing an absent key succeeds")

    testCase "WHY — repoPath nests under .SageFs so per-repo settings live with the repo, not the global store" <| fun _ ->
      let p = SettingsStore.repoPath "/home/x/proj"
      p.Replace('\\', '/') |> Expect.stringContains "under .SageFs" "/proj/.SageFs/settings.json"
  ]

/// Phase A4: the catalog glue ties descriptors to the store end-to-end —
/// parse -> persist to the chosen layer -> apply-if-live -> re-resolve — and
/// an illegal edit is refused at parse, never persisted. Sequenced because the
/// Live timeout pilot mutates a process-global (Timeouts) that other suites read.
[<Tests>]
let catalogTests =
  // Same sequenced group as TimeoutsTests' "Thread-safe mutable timeouts": both
  // mutate the process-global Timeouts.perTestDefault, so they must be mutually
  // exclusive, not merely internally sequenced (testSequenced only orders within
  // a list; a parallel list would still read a transiently-mutated global).
  testSequencedGroup "timeouts-global" <| testList "Settings catalog" [

    testCase "WHY — editing the Live per-test timeout persists AND applies to the running Timeouts, because it revives the dead setTestTimeouts path" <| fun _ ->
      withTempDir (fun dir ->
        let original = Timeouts.perTestDefault ()
        try
          let paths = { GlobalDir = dir; Repo = NoRepoCheckout }
          match SettingsCatalog.edit paths LGlobal "30" SettingsCatalog.perTestTimeout with
          | Error why -> failtestf "expected Ok, got Error %A" why
          | Ok prov ->
            prov.Source |> Expect.equal "resolved from the global layer" LGlobal
            // Live apply reached the process-global setter.
            (Timeouts.perTestDefault ()).TotalSeconds |> Expect.equal "Timeouts now reflects the edit" 30.0
            // And it round-trips through resolution.
            match prov.Effective with
            | VTimeout t -> (ValidTimeout.value t).TotalSeconds |> Expect.equal "effective is 30s" 30.0
            | other -> failtestf "expected VTimeout, got %A" other
        finally
          Timeouts.setPerTestTimeout original)

    testCase "WHY — an out-of-range port edit is refused at parse and never persisted, because illegal config is unrepresentable" <| fun _ ->
      withTempDir (fun dir ->
        let paths = { GlobalDir = dir; Repo = NoRepoCheckout }
        SettingsCatalog.edit paths LGlobal "70000" SettingsCatalog.mcpPort
        |> Expect.isError "70000 is not a valid port"
        // Nothing was written.
        SettingsStore.readLayer (SettingsStore.globalPath dir) |> Map.tryFind SettingsCatalog.mcpPort.Key
        |> Expect.isNone "a refused edit persists nothing")

    testCase "WHY — a valid RestartRequired port edit persists and resolves but is not applied live, because the port only takes effect on restart" <| fun _ ->
      withTempDir (fun dir ->
        let paths = { GlobalDir = dir; Repo = NoRepoCheckout }
        match SettingsCatalog.edit paths LGlobal "40000" SettingsCatalog.mcpPort with
        | Error why -> failtestf "expected Ok, got Error %A" why
        | Ok prov ->
          match prov.Effective with
          | VPort p -> Port.value p |> Expect.equal "resolves to 40000" 40000
          | other -> failtestf "expected VPort, got %A" other
          SettingsStore.readLayer (SettingsStore.globalPath dir) |> Map.tryFind SettingsCatalog.mcpPort.Key
          |> Expect.equal "persisted the rendered value" (Some "40000"))

    testCase "WHY — a non-loopback bind host is refused at parse, because a LAN bind is RCE and must be structurally impossible" <| fun _ ->
      withTempDir (fun dir ->
        let paths = { GlobalDir = dir; Repo = NoRepoCheckout }
        SettingsCatalog.edit paths LGlobal "0.0.0.0" SettingsCatalog.bindHost
        |> Expect.isError "0.0.0.0 is not loopback"
        SettingsStore.readLayer (SettingsStore.globalPath dir) |> Map.tryFind SettingsCatalog.bindHost.Key
        |> Expect.isNone "a refused bind host persists nothing")

    testCase "WHY — a loopback bind host edit resolves to the typed LoopbackHost, because loopback values are legal" <| fun _ ->
      withTempDir (fun dir ->
        let paths = { GlobalDir = dir; Repo = NoRepoCheckout }
        match SettingsCatalog.edit paths LGlobal "127.0.0.1" SettingsCatalog.bindHost with
        | Error why -> failtestf "expected Ok, got Error %A" why
        | Ok prov ->
          prov.Effective |> Expect.equal "resolves to the IPv4 loopback" (VBindHost SageFsConfig.LoopbackHost.Ipv4))

    testCase "WHY — a repo override beats the global value, then clearing it falls back, because that is the base-vs-override contract" <| fun _ ->
      withTempDir (fun dir ->
        let repo = Path.Combine(dir, "repo")
        Directory.CreateDirectory repo |> ignore
        let original = Timeouts.perTestDefault ()
        try
          let paths = { GlobalDir = dir; Repo = RepoRootAt repo }
          SettingsCatalog.edit paths LGlobal "10" SettingsCatalog.perTestTimeout |> ignore
          match SettingsCatalog.edit paths LRepo "30" SettingsCatalog.perTestTimeout with
          | Error why -> failtestf "repo edit failed: %A" why
          | Ok prov ->
            prov.Source |> Expect.equal "repo override wins" LRepo
            match prov.Effective with
            | VTimeout t -> (ValidTimeout.value t).TotalSeconds |> Expect.equal "repo's 30s wins over global 10s" 30.0
            | other -> failtestf "expected VTimeout, got %A" other
          // Clearing the repo override falls back to the global value.
          match SettingsCatalog.clear paths LRepo SettingsCatalog.perTestTimeout with
          | Error why -> failtestf "clear failed: %A" why
          | Ok prov ->
            prov.Source |> Expect.equal "falls back to global" LGlobal
            match prov.Effective with
            | VTimeout t -> (ValidTimeout.value t).TotalSeconds |> Expect.equal "global's 10s is the fallback" 10.0
            | other -> failtestf "expected VTimeout, got %A" other
        finally
          Timeouts.setPerTestTimeout original)
  ]
