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
      let p = SettingsResolver.resolve (VBool false) []
      p.Source |> Expect.equal "source is default" LDefault
      p.Effective |> Expect.equal "effective is the default" (VBool false)

    testCase "WHY — repo overrides global because a higher layer wins" <| fun _ ->
      let p = SettingsResolver.resolve (VBool false) [ LGlobal, VBool true; LRepo, VBool false ]
      p.Source |> Expect.equal "repo is the source" LRepo
      p.Effective |> Expect.equal "repo's value wins" (VBool false)

    testCase "WHY — session overrides repo and global because it is the highest layer" <| fun _ ->
      let p = SettingsResolver.resolve (VBool false) [ LGlobal, VBool true; LRepo, VBool false; LSession, VBool true ]
      p.Source |> Expect.equal "session is the source" LSession

    testCase "WHY — provenance records every set layer so the UI can show base-vs-override" <| fun _ ->
      let p = SettingsResolver.resolve (VBool false) [ LGlobal, VBool true; LRepo, VBool false ]
      p.PerLayer |> List.map fst
      |> Expect.equal "all three layers, lowest-first" [ LDefault; LGlobal; LRepo ]

    testProperty "WHY — resolve's source is always the highest-ranked layer present because precedence is total" <| fun (useGlobal: bool) (useRepo: bool) (useSession: bool) ->
      let set =
        [ if useGlobal then yield LGlobal, VBool true
          if useRepo then yield LRepo, VBool true
          if useSession then yield LSession, VBool true ]
      let p = SettingsResolver.resolve (VBool false) set
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
