/// Outcome gates for developing SageFs with SageFs.
///
/// A worktree of SageFs builds its own SageFs.Core, and that Core is routinely NEWER than the
/// daemon serving the session (the daemon is whatever was last published). Two things used to go
/// wrong there, and a user can only see them from the outside:
///
///  1. The build a session runs injected the DAEMON's SageFs.Core as a reference into every project,
///     so a project that used a new Core API compiled against the old Core metadata and every new
///     name was "not defined". The session faulted, and the hint blamed file order.
///  2. `get_session_status wait_seconds` answered `NotNeeded` while a rebuild was running, because
///     the old worker kept serving. The caller had to poll to learn when the rebuild finished.
///
/// The fixture is a stand-in for the SageFs repo (`fixtures/SageFsLikeRepoFixture`): a consumer
/// project that references SageFs.Core and uses an API only the repo's Core has, and a plain
/// project that references nothing. The repo's Core is a REAL copy of SageFs.Core with one extra
/// file, because the worker adopts the project's own SageFs.Core (HostCoreAdoption) and a stand-in
/// Core cannot be adopted by a host that links the real one. Each gate copies what it needs and
/// runs a REAL daemon this file spawns on its own port, so what is asserted is what a user reads
/// from the tools.
module SageFs.Tests.SelfHostCoreOutcomeTests

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol

module Integration = SageFs.Tests.TestInfrastructure.Integration
module Http = SageFs.Tests.HttpApiIntegrationTests

let private fixtureSource =
  Path.Combine(__SOURCE_DIRECTORY__, "fixtures", "SageFsLikeRepoFixture")

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

/// Where a gate's copy lives and whether it carries a repo of its own SageFs.Core.
[<RequireQualifiedAccess>]
type private Copy =
  /// The projects alone, in a temp directory.
  | ProjectsOnly
  /// The projects next to a real copy of SageFs.Core with the fixture's extra file compiled in. It is made
  /// under the fixture folder (ignored by git) so the repo's Directory.Build.props and package versions apply.
  | WithRepoCore

let private copyTree (source: string) (destination: string) : unit =
  let rec copy (from: string) (into: string) =
    Directory.CreateDirectory into |> ignore
    for file in Directory.GetFiles from do
      File.Copy(file, Path.Combine(into, Path.GetFileName file))
    for dir in Directory.GetDirectories from do
      match Path.GetFileName dir with
      | "bin" | "obj" | ".runs" -> ()
      | name -> copy dir (Path.Combine(into, name))
  copy source destination

/// The line after which the extra file is compiled: first in the list, because it depends on nothing.
let private firstCompileItem = "<Compile Include=\"MachineTier.fs\" />"

/// Copies the fixture, without any build output, so a gate builds in its own directory.
let private copyFixture (copy: Copy) : string =
  match copy with
  | Copy.ProjectsOnly ->
    let target = Directory.CreateTempSubdirectory("sagefs-selfhost-").FullName
    copyTree (Path.Combine(fixtureSource, "Plain")) (Path.Combine(target, "Plain"))
    target
  | Copy.WithRepoCore ->
    let target = Path.Combine(fixtureSource, ".runs", Guid.NewGuid().ToString "N")
    copyTree (Path.Combine(fixtureSource, "Consumer")) (Path.Combine(target, "Consumer"))
    let core = Path.Combine(target, "SageFs.Core")
    copyTree (Path.Combine(repoRoot, "SageFs.Core")) core
    // Core embeds the FSI host's sources by relative path, so they sit beside it as in the repo.
    copyTree (Path.Combine(repoRoot, "SageFs.FsiHost")) (Path.Combine(target, "SageFs.FsiHost"))
    // ...and so do the native parser libraries it copies to its output.
    copyTree (Path.Combine(repoRoot, "runtimes")) (Path.Combine(target, "runtimes"))
    File.Copy(Path.Combine(fixtureSource, "FixtureOnly.fs"), Path.Combine(core, "FixtureOnly.fs"))
    let project = Path.Combine(core, "SageFs.Core.fsproj")
    let text = File.ReadAllText project
    match text.Split(firstCompileItem).Length - 1 with
    | 1 -> File.WriteAllText(project, text.Replace(firstCompileItem, firstCompileItem + "\n        <Compile Include=\"FixtureOnly.fs\" />"))
    | found -> failtestf "expected exactly one '%s' in the copied SageFs.Core.fsproj, found %d" firstCompileItem found
    target

let private connect (port: int) : Task<McpClient> =
  let opts = HttpClientTransportOptions(Endpoint = Uri(sprintf "http://localhost:%d/" port))
  let transport = HttpClientTransport(opts, (null: Microsoft.Extensions.Logging.ILoggerFactory))
  McpClient.CreateAsync(transport, null, null, CancellationToken.None)

let private callText (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> =
  task {
    use cts = new CancellationTokenSource(TestTimeouts.toolCallThatRestarts)
    let! result = client.CallToolAsync(name, readOnlyDict args, null, null, cts.Token)
    return
      result.Content
      |> Seq.choose (function :? TextContentBlock as t -> Some t.Text | _ -> None)
      |> String.concat ""
  }

/// A session's id is the first token a create tool prints.
let private sessionIdOf (createText: string) : string =
  createText.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries).[0].Trim()

/// The status JSON is the first line of the reply; the daemon appends an events trailer after it.
let private statusOf (text: string) : JsonDocument =
  match text.TrimStart().StartsWith "{" with
  | true -> JsonDocument.Parse(text.Split('\n').[0])
  | false -> failtestf "expected a status object, the daemon said: %s" text

let private waitSeconds = int TestTimeouts.daemonHeldWait.TotalSeconds

/// Where a long wait on a session ended.
[<RequireQualifiedAccess>]
type private Settled =
  | Ready
  | Faulted of reason: string

/// Waits, on the daemon's side and without a sleep, until the session is Ready or Faulted. A wait that
/// ran out of its own bound asks again: the cold build behind a create can outlast one wait.
let private settle (client: McpClient) (sid: string) : Task<Settled> =
  task {
    let started = Diagnostics.Stopwatch.StartNew()
    let mutable settled = None
    while settled.IsNone && started.Elapsed < TestTimeouts.sessionReadyColdBuild do
      let! text = callText client "get_session_status" [ "session_id", box sid; "wait_seconds", box waitSeconds ]
      use doc = statusOf text
      let root = doc.RootElement
      match root.GetProperty("state").GetString() with
      | "Ready" -> settled <- Some Settled.Ready
      | "Faulted" ->
        let reason =
          match root.TryGetProperty "faultReason" with
          | true, r -> r.GetString()
          | false, _ -> text
        settled <- Some (Settled.Faulted reason)
      | _ -> ()
    match settled with
    | Some outcome -> return outcome
    | None -> return failwithf "session %s never settled within %O" sid TestTimeouts.sessionReadyColdBuild
  }

let private createSession (client: McpClient) (project: string) (workingDir: string) : Task<string> =
  task {
    let! created = callText client "create_project_session" [ "project", box project; "working_directory", box workingDir ]
    let id = sessionIdOf created
    // A create that could not even build says so where the id should be.
    match id.Length = 8 && id |> Seq.forall Uri.IsHexDigit with
    | true -> return id
    | false -> return failtestf "creating the session did not give a session id. The daemon said:\n%s" created
  }

let private evalIn (client: McpClient) (sid: string) (code: string) : Task<string> =
  callText client "send_fsharp_code" [ "agentName", box "selfhost-gate"; "code", box code; "session_id", box sid ]

/// Runs a body against a fresh fixture copy and a real daemon, and cleans up whatever happens.
let private withFixtureDaemon (copy: Copy) (body: McpClient -> string -> Task<unit>) : Task<unit> =
  task {
    let root = copyFixture copy
    let port = Http.reserveLoopbackPort ()
    let! proc, httpClient = Http.startDaemonWithArgs port root [ "--no-resume" ]
    let! client = connect port
    // No try/finally around an await: observe the outcome through a continuation, clean up, then re-raise.
    let! outcome =
      (body client root)
        .ContinueWith(fun (t: Task<unit>) ->
          match t.IsFaulted with
          | true -> Error(t.Exception :> exn)
          | false -> Ok())
    try (client :> IAsyncDisposable).DisposeAsync() |> ignore with _ -> ()
    httpClient.Dispose()
    Http.killDaemon proc
    try Directory.Delete(root, true) with _ -> ()
    match outcome with
    | Error e -> raise e
    | Ok() -> ()
  }

// ---------------------------------------------------------------------------------------------
// 1. A project whose repo builds its own SageFs.Core compiles against that Core.
// ---------------------------------------------------------------------------------------------

let private coreOfItsOwnRepo (client: McpClient) (root: string) : Task<unit> =
  task {
    let consumer = Path.Combine(root, "Consumer", "Consumer.fsproj")
    let! sid = createSession client consumer root
    let! settled = settle client sid
    match settled with
    | Settled.Faulted reason ->
      failtestf
        "a session on a project that uses an API only ITS repo's SageFs.Core has must go Ready, because it builds against that Core. It faulted: %s"
        reason
    | Settled.Ready -> ()
    let! answer = evalIn client sid "SageFsLike.Consumer.Api.probe ();;"
    answer |> Expect.stringContains "the session ran code that uses the repo's own Core API" "consumer sees from-fixture-core"
  }

// ---------------------------------------------------------------------------------------------
// 2. wait_seconds waits for a rebuild too.
// ---------------------------------------------------------------------------------------------

let private plainProject (root: string) = Path.Combine(root, "Plain", "Plain.fsproj")

let private plainSource (root: string) = Path.Combine(root, "Plain", "Api.fs")

let private versionSource (version: string) =
  sprintf "module SageFsLike.Plain.Api\n\nlet version () : string = \"%s\"\n" version

/// What the `wait` object says: how it ended, how long it parked, and what the last rebuild did.
/// A field the daemon does not send reads as "absent", so a missing field is a failed expectation
/// and not a crash.
let private waitedOn (text: string) : string * int64 * string =
  use doc = statusOf text
  let wait = doc.RootElement.GetProperty("wait")
  let lastRebuild =
    match wait.TryGetProperty "lastRebuild" with
    | true, value -> value.GetString()
    | false, _ -> "absent"
  wait.GetProperty("outcome").GetString(), wait.GetProperty("waitedMs").GetInt64(), lastRebuild

let private rebuildIsWaitedOn (client: McpClient) (root: string) : Task<unit> =
  task {
    let! sid = createSession client (plainProject root) root
    let! settled = settle client sid
    match settled with
    | Settled.Faulted reason -> failtestf "the plain project must reach Ready first. It faulted: %s" reason
    | Settled.Ready -> ()
    File.WriteAllText(plainSource root, versionSource "plain-v2")
    let! reset = callText client "hard_reset_fsi_session" [ "rebuild", box true; "session_id", box sid ]
    reset |> Expect.stringContains "the rebuild was accepted" "Hard reset initiated"
    // The old worker keeps serving while the build runs. One call, no polling.
    let! text = callText client "get_session_status" [ "session_id", box sid; "wait_seconds", box waitSeconds ]
    let outcome, waitedMs, lastRebuild = waitedOn text
    outcome |> Expect.equal (sprintf "the wait covers the rebuild. Reply: %s" text) "BecameReady"
    (waitedMs > 0L) |> Expect.isTrue (sprintf "it really waited. Reply: %s" text)
    lastRebuild |> Expect.equal "and the answer says what the rebuild did" "Succeeded"
    let! answer = evalIn client sid "SageFsLike.Plain.Api.version ();;"
    answer |> Expect.stringContains "the code that answers is the rebuilt one" "plain-v2"
  }

let private failedRebuildIsNotReady (client: McpClient) (root: string) : Task<unit> =
  task {
    let! sid = createSession client (plainProject root) root
    let! settled = settle client sid
    match settled with
    | Settled.Faulted reason -> failtestf "the plain project must reach Ready first. It faulted: %s" reason
    | Settled.Ready -> ()
    File.WriteAllText(plainSource root, "module SageFsLike.Plain.Api\n\nlet version () : string = this does not compile\n")
    let! reset = callText client "hard_reset_fsi_session" [ "rebuild", box true; "session_id", box sid ]
    reset |> Expect.stringContains "the rebuild was accepted" "Hard reset initiated"
    let! text = callText client "get_session_status" [ "session_id", box sid; "wait_seconds", box waitSeconds ]
    let outcome, _, lastRebuild = waitedOn text
    outcome |> Expect.equal (sprintf "a rebuild that failed is not 'became ready'. Reply: %s" text) "Faulted"
    lastRebuild |> Expect.equal "and the answer says the old build is still serving" "FailedStillServing"
  }

[<Tests>]
let selfHostCoreOutcomeTests =
  Integration.hostList "Self-hosting outcome gate" [
    testTask "WHY — a session on a project in a repo that builds its own SageFs.Core compiles against and runs THAT Core, not the daemon's older one" {
      do! withFixtureDaemon Copy.WithRepoCore coreOfItsOwnRepo
    }

    testTask "WHY — get_session_status wait_seconds waits for an in-flight rebuild and says what it did, instead of answering NotNeeded and making the caller poll" {
      do! withFixtureDaemon Copy.ProjectsOnly rebuildIsWaitedOn
    }

    testTask "WHY — a rebuild that fails while the old worker still serves is not reported as 'became ready'" {
      do! withFixtureDaemon Copy.ProjectsOnly failedRebuildIsNotReady
    }
  ]
