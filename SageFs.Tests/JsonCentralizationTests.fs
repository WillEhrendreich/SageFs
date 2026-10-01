/// SageFs serializes and deserializes JSON in one place: `SageFs.Json` (SageFs.Core/Json.fs).
/// This pins where the rest of the code still does it itself. Each file has a budget, the
/// number of `JsonSerializer.Serialize` / `Deserialize` calls and `JsonSerializerOptions(`
/// constructions it still has, and the budgets only go DOWN: a file that migrates lowers its
/// number, a file that is not listed has a budget of zero, and a file over its budget fails.
/// When the table is empty there is one way to write JSON, and the test says so.
module SageFs.Tests.JsonCentralizationTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let private strayPattern =
  Regex(@"JsonSerializer\.(Serialize|Deserialize)|JsonSerializerOptions\(", RegexOptions.Compiled)

/// What each file may still do itself. Ratchet down, never up.
let private budgets : (string * int) list =
  [ "SageFs.Core/DaemonOwnership.fs", 5
    "SageFs.Core/DaemonState.fs", 1
    "SageFs.Core/DevReload.fs", 1
    "SageFs.Core/Features/KeptState.fs", 2
    "SageFs.Core/SessionOperations.fs", 1
    "SageFs.Core/SettingsStore.fs", 3
    "SageFs.Core/SseWriter.fs", 23
    "SageFs.Core/WarmupReplayCache.fs", 3
    "SageFs.Core/WorkerProtocol.fs", 3
    "SageFs/McpAdapter.fs", 5
    "SageFs/McpFrictionRecorder.fs", 3
    "SageFs/Mcp.fs", 36
    "SageFs/McpResources.fs", 1
    "SageFs/McpServer.fs", 9
    "SageFs/McpStdioBridge.fs", 2
    "SageFs/McpTools.fs", 2
    "SageFs/SessionStatusPayload.fs", 1 ]

/// Every non-test source file with its count of stray JSON calls, except the central module.
let private actual : (string * int) list =
  [ for project in [ "SageFs"; "SageFs.Core"; "SageFs.Host" ] do
      let dir = Path.Combine(repoRoot, project)
      match Directory.Exists dir with
      | false -> ()
      | true ->
        for file in Directory.EnumerateFiles(dir, "*.fs", SearchOption.AllDirectories) do
          let relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/')
          let isBuildOutput = relative.Contains "/obj/" || relative.Contains "/bin/"
          match isBuildOutput || relative = "SageFs.Core/Json.fs" with
          | true -> ()
          | false ->
            let count = strayPattern.Matches(File.ReadAllText file).Count
            match count with
            | 0 -> ()
            | n -> yield relative, n ]

[<Tests>]
let tests =
  testList "JSON centralization" [

    testCase "WHY — no file does more JSON serialization itself than its budget, so the number of places that decide how JSON is written only goes down" <| fun _ ->
      let allowed = Map.ofList budgets
      let over =
        actual
        |> List.choose (fun (file, count) ->
          let budget = Map.tryFind file allowed |> Option.defaultValue 0
          match count > budget with
          | true -> Some (sprintf "%s has %d, budget %d" file count budget)
          | false -> None)
      over |> Expect.isEmpty "every file is within its budget (a file not in the table has a budget of zero: use SageFs.Json)"

    testCase "WHY — a budget that is higher than the file's real count is stale, so the ratchet cannot hide room that was already won back" <| fun _ ->
      let counts = Map.ofList actual
      let stale =
        budgets
        |> List.choose (fun (file, budget) ->
          let count = Map.tryFind file counts |> Option.defaultValue 0
          match count < budget with
          | true -> Some (sprintf "%s has %d but its budget is %d: lower it" file count budget)
          | false -> None)
      stale |> Expect.isEmpty "every budget equals the file's current count"
  ]
