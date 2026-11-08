module SageFs.Tests.SettingsSnapshotTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open SageFs

let private withTempDir (run: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-snapshot-" + Guid.NewGuid().ToString("N").[..7])
  Directory.CreateDirectory dir |> ignore
  try run dir
  finally try Directory.Delete(dir, true) with _ -> ()

let private snapshotOf (pairs: (string * string) list) : SettingsSnapshot =
  match SettingsSnapshot.ofPairs pairs with
  | Ok snapshot -> snapshot
  | Error why -> failtestf "the pairs are legal, got %A" why

/// What a process uses for the settings it was not given a variable for: one typed value, resolved once.
[<Tests>]
let snapshotTests =
  testList "Settings snapshot — one typed value carries what the daemon resolved" [

    testProperty "WHY — a snapshot is the same snapshot after it travels as a worker argument" <| fun (pairs: (uint16 * int) list) ->
      let snapshot =
        pairs
        |> List.map (fun (n, v) -> sprintf "SAGEFS_V%d" n, string v)
        |> snapshotOf
      SettingsSnapshot.decode (SettingsSnapshot.encode snapshot) = Ok snapshot

    testCase "WHY — an empty snapshot is an empty argument, so a worker started with no saved settings carries nothing extra" <| fun _ ->
      SettingsSnapshot.encode SettingsSnapshot.empty |> Expect.equal "no text" ""
      SettingsSnapshot.decode "" |> Expect.equal "and no text is no settings" (Ok SettingsSnapshot.empty)

    testCase "WHY — a name or a text that would break the line format is refused when the snapshot is built, so no value can be misread later" <| fun _ ->
      SettingsSnapshot.ofPairs [ "SAGEFS A", "1" ] |> Expect.equal "a space in a name" (Error (IllegalName "SAGEFS A"))
      SettingsSnapshot.ofPairs [ "", "1" ] |> Expect.equal "an empty name" (Error (IllegalName ""))
      SettingsSnapshot.ofPairs [ "SAGEFS_A", "1\n2" ] |> Expect.equal "a newline in a text" (Error (IllegalText "SAGEFS_A"))
      SettingsSnapshot.ofPairs [ "SAGEFS_A", "" ] |> Expect.equal "an empty text is not a value" (Error (IllegalText "SAGEFS_A"))

    testCase "WHY — an argument that is not a snapshot is a named refusal, never a half-read snapshot" <| fun _ ->
      match SettingsSnapshot.decode "!!!" with
      | Error (NotBase64 _) -> ()
      | other -> failtestf "expected NotBase64, got %A" other
      let noEquals = Convert.ToBase64String(Text.Encoding.UTF8.GetBytes "SAGEFS_A")
      SettingsSnapshot.decode noEquals |> Expect.equal "a line with no equals sign" (Error (MalformedLine(1, "SAGEFS_A")))

    testCase "WHY — the snapshot is read first and the environment second, so a value the daemon resolved is the one every process uses" <| fun _ ->
      let environment name = match name with "SAGEFS_A" -> "person" | "SAGEFS_B" -> "person-b" | _ -> null
      let snapshot = snapshotOf [ "SAGEFS_A", "resolved"; "SAGEFS_C", "saved" ]
      SettingsInForce.lookup environment snapshot "SAGEFS_A" |> Expect.equal "the snapshot wins" "resolved"
      SettingsInForce.lookup environment snapshot "SAGEFS_B" |> Expect.equal "the environment answers what the snapshot lacks" "person-b"
      SettingsInForce.lookup environment snapshot "SAGEFS_C" |> Expect.equal "a saved value" "saved"
      SettingsInForce.lookup environment snapshot "SAGEFS_D" |> Expect.isNull "nothing anywhere is unset"
  ]

/// The worker's command line: the session, the port and, when there is one, the snapshot.
[<Tests>]
let workerCommandLineTests =
  testList "Settings snapshot — the worker is handed the snapshot as an argument" [

    testCase "WHY — a worker command line carries the snapshot it was started with" <| fun _ ->
      let snapshot = snapshotOf [ "SAGEFS_WARMUP_MAX_MINUTES", "25"; MachineTier.envVar, "constrained" ]
      let line = WorkerCommandLine.render "abc-123" snapshot
      line |> Expect.stringStarts "the session and the ephemeral port come first" "abc-123 0 "
      WorkerCommandLine.parse (line.Split ' ')
      |> Expect.equal "and the worker reads the same three things back" (Ok { SessionId = "abc-123"; HttpPort = 0; Settings = snapshot })

    testCase "WHY — a worker started with no snapshot is the same two arguments it always was" <| fun _ ->
      WorkerCommandLine.render "abc-123" SettingsSnapshot.empty |> Expect.equal "no third argument" "abc-123 0"
      WorkerCommandLine.parse [| "abc-123"; "0" |]
      |> Expect.equal "and an empty snapshot is read" (Ok { SessionId = "abc-123"; HttpPort = 0; Settings = SettingsSnapshot.empty })

    testCase "WHY — a command line the worker cannot read says which part, so the supervisor's log names the cause" <| fun _ ->
      WorkerCommandLine.parse [||] |> Expect.equal "no session" (Error MissingSessionId)
      WorkerCommandLine.parse [| "s" |] |> Expect.equal "no port" (Error MissingHttpPort)
      WorkerCommandLine.parse [| "s"; "x" |] |> Expect.equal "a port that is not a number" (Error (InvalidHttpPort "x"))
      match WorkerCommandLine.parse [| "s"; "0"; "!!!" |] with
      | Error (InvalidSettings (NotBase64 _)) -> ()
      | other -> failtestf "expected InvalidSettings, got %A" other
  ]

let private saveGlobal (dir: string) (key: string) (raw: string) =
  SettingsStore.setKey (SettingsStore.globalPath dir) key raw |> Expect.isOk "the raw write succeeds"

/// The start-up resolution: what the Settings page saved, folded with what the person set, into one snapshot.
[<Tests>]
let resolveTests =
  testList "Settings snapshot — the start-up resolution" [

    let run (saved: (string * string) list) (set: (string * string) list) (f: SavedSettings -> unit) =
      withTempDir (fun dir ->
        for key, raw in saved do saveGlobal dir key raw
        let read name = match List.tryFind (fun (n, _) -> n = name) set with Some (_, v) -> EnvSet v | None -> EnvUnset
        f (SettingsBootstrap.resolveSavedWith read dir EnvBackedRows.all))

    testCase "WHY — a saved wait is in the snapshot when the person set no variable, and nothing is written to the environment" <| fun _ ->
      run [ "waits.warmupInactivitySeconds", "90" ] [] (fun resolved ->
        SettingsSnapshot.tryFind "SAGEFS_WARMUP_INACTIVITY_SECONDS" resolved.Snapshot |> Expect.equal "the snapshot holds the saved text" (Some "90")
        resolved.Outcomes |> Expect.equal "and the resolution says so" [ ImportOutcome.InSnapshot("SAGEFS_WARMUP_INACTIVITY_SECONDS", "90") ])

    testCase "WHY — a variable the person set wins over the saved value and is not in the snapshot" <| fun _ ->
      run [ "waits.warmupMaxMinutes", "25" ] [ "SAGEFS_WARMUP_MAX_MINUTES", "15" ] (fun resolved ->
        SettingsSnapshot.tryFind "SAGEFS_WARMUP_MAX_MINUTES" resolved.Snapshot |> Expect.isNone "left to the environment"
        resolved.Outcomes |> Expect.equal "the resolution says the environment kept it" [ ImportOutcome.LeftToEnvironment "SAGEFS_WARMUP_MAX_MINUTES" ])

    testCase "WHY — a saved value that does not parse is reported and the snapshot is without it" <| fun _ ->
      run [ "waits.buildTimeoutMinutes", "abc" ] [] (fun resolved ->
        SettingsSnapshot.isEmpty resolved.Snapshot |> Expect.isTrue "nothing in the snapshot"
        match resolved.Outcomes with
        | [ ImportOutcome.NotUsable(variable, reason) ] ->
          variable |> Expect.equal "names the variable" "SAGEFS_BUILD_TIMEOUT_MINUTES"
          reason |> Expect.stringContains "says which key and why" "waits.buildTimeoutMinutes"
        | other -> failtestf "expected one NotUsable, got %A" other)

    testCase "WHY — a saved auto tier leaves the tier out of the snapshot, because auto means let SageFs measure the machine" <| fun _ ->
      run [ "waits.machineTier", "auto" ] [] (fun resolved ->
        SettingsSnapshot.isEmpty resolved.Snapshot |> Expect.isTrue "nothing in the snapshot"
        resolved.Outcomes |> Expect.equal "the resolution says it means unset" [ ImportOutcome.SavedMeansUnset MachineTier.envVar ])

    testCase "WHY — a saved tier is in the snapshot as the tier a person would have forced" <| fun _ ->
      run [ "waits.machineTier", "constrained" ] [] (fun resolved ->
        SettingsSnapshot.tryFind MachineTier.envVar resolved.Snapshot |> Expect.equal "the tier" (Some "constrained"))

    testCase "WHY — a saved reaper bound of 0 is carried as 0, the spelling the variable's reader treats as off" <| fun _ ->
      run [ "waits.abandonedSessionHours", "0" ] [] (fun resolved ->
        SettingsSnapshot.tryFind "SAGEFS_ABANDONED_SESSION_HOURS" resolved.Snapshot |> Expect.equal "zero hours" (Some "0"))

    testCase "WHY — an unreadable layer file is one report and an empty snapshot, not a row per setting" <| fun _ ->
      withTempDir (fun dir ->
        File.WriteAllText(SettingsStore.globalPath dir, "{ this is not json")
        let resolved = SettingsBootstrap.resolveSavedWith (fun _ -> EnvUnset) dir EnvBackedRows.all
        SettingsSnapshot.isEmpty resolved.Snapshot |> Expect.isTrue "nothing in the snapshot"
        match resolved.Outcomes with
        | [ ImportOutcome.NotUsable(_, reason) ] -> reason |> Expect.stringContains "names the file" "settings.json"
        | other -> failtestf "expected one NotUsable, got %A" other)

    testCase "WHY — resolving the saved settings leaves the process environment as it was, so a child daemon inherits only what a person set" <| fun _ ->
      withTempDir (fun dir ->
        saveGlobal dir "waits.warmupInactivitySeconds" "91"
        saveGlobal dir "waits.machineTier" "constrained"
        let sagefsVariables () =
          Environment.GetEnvironmentVariables()
          |> Seq.cast<System.Collections.DictionaryEntry>
          |> Seq.map (fun e -> string e.Key, string e.Value)
          |> Seq.filter (fun (name, _) -> name.StartsWith "SAGEFS_")
          |> Seq.sort
          |> Seq.toList
        let before = sagefsVariables ()
        let resolved = SettingsBootstrap.resolveSaved dir
        sagefsVariables () |> Expect.equal "no SAGEFS_ variable was written" before
        // A person's own variable would beat the saved one; with none set, both are in the snapshot.
        match Environment.GetEnvironmentVariable "SAGEFS_WARMUP_INACTIVITY_SECONDS" with
        | null | "" -> SettingsSnapshot.tryFind "SAGEFS_WARMUP_INACTIVITY_SECONDS" resolved.Snapshot |> Expect.equal "the saved wait" (Some "91")
        | _ -> ())
  ]

let private repoRoot = RepoPaths.repoPathFull [||]

/// The environment of a SageFs process is the person's. Product code does not write settings into it.
[<Tests>]
let environmentIsThePersonsTests =
  testList "Settings snapshot — the environment is not a settings bus" [

    testCase "WHY — no product file sets an environment variable of its own process except the two that run another program's code" <| fun _ ->
      let allowed = [ "SageFs.Core/AspireSetup.fs"; "SageFs.Host/AppRunner.fs" ]
      let offenders =
        [ for project in [ "SageFs"; "SageFs.Core"; "SageFs.Host" ] do
            for file in Directory.EnumerateFiles(Path.Combine(repoRoot, project), "*.fs", SearchOption.AllDirectories) do
              let relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/')
              match relative.Contains "/obj/" || relative.Contains "/bin/" || List.contains relative allowed with
              | true -> ()
              | false ->
                let writes =
                  File.ReadAllLines file
                  |> Array.filter (fun line -> not (line.TrimStart().StartsWith "//") && line.Contains "SetEnvironmentVariable")
                match writes.Length with
                | 0 -> ()
                | n -> yield sprintf "%s (%d)" relative n ]
      offenders |> Expect.isEmpty "a setting reaches a worker through the snapshot argument, never through the daemon's own environment"
  ]
  |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant
