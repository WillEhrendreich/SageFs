module SageFs.Tests.SettingsTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs

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
