namespace SageFs

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks

/// The worker's own credential. A worker listens on `127.0.0.1:<ephemeral>` and runs F# for whoever sends `/eval`, so
/// before this any process that could reach loopback (a sandboxed agent with host networking, another user on the
/// machine) could run code as the user without ever touching the daemon. The daemon draws a random secret for each
/// worker it starts, hands it over in the worker's spawn environment (`secretEnvVar`) and sends it as
/// `Authorization: Bearer` on every call. The worker refuses a request without it on every route but the dev-reload
/// stream, which a browser page reads and cannot add a header to.
///
/// The secret never reaches the FSI host the user's code runs in: the worker takes it out of its own environment when
/// it reads it, and `ProcessEnvironment.poisonedVariables` strips it from every spawn.
[<RequireQualifiedAccess>]
module WorkerAuth =

  /// The variable the daemon puts the secret in, for the worker it starts and nothing else.
  let secretEnvVar = ProcessEnvironment.workerSecretEnvVar

  /// 32 random bytes, base64url. Never logged, never shown to a client.
  [<Struct>]
  type WorkerSecret = WorkerSecret of string

  let textOf (WorkerSecret text) = text

  /// A fresh secret from the OS random source.
  let generate () : WorkerSecret =
    RandomNumberGenerator.GetBytes 32
    |> Convert.ToBase64String
    |> fun text -> text.TrimEnd('=').Replace('+', '-').Replace('/', '_')
    |> WorkerSecret

  /// What the worker was started with.
  [<RequireQualifiedAccess>]
  type Expected =
    /// The daemon started this worker and gave it a secret: every route but the browser stream needs it.
    | Required of WorkerSecret
    /// No secret in the environment: a worker started by hand or a test's in-process server. The daemon never starts
    /// one like this, and a caller cannot take a secret away from a worker that has one.
    | NotConfigured

  /// Who may call a route.
  [<RequireQualifiedAccess>]
  type RouteCredential =
    /// Only the daemon calls it, so it carries the secret.
    | DaemonOnly
    /// The dev-reload SSE stream: the script injected into the user's own app opens it with `EventSource`, which
    /// cannot send a header. It stays behind the origin gate alone and only streams reload notices.
    | BrowserStream

  let credentialOf (route: WorkerRoute) : RouteCredential =
    match route with
    | WorkerRoute.DevReloadStream -> RouteCredential.BrowserStream
    | WorkerRoute.DiagThreadpool
    | WorkerRoute.Status
    | WorkerRoute.LiveValues
    | WorkerRoute.LiveValuesEvaluate
    | WorkerRoute.LiveValuesMode
    | WorkerRoute.Eval
    | WorkerRoute.Check
    | WorkerRoute.TypecheckSymbols
    | WorkerRoute.Completions
    | WorkerRoute.References
    | WorkerRoute.HostProcess
    | WorkerRoute.Cancel
    | WorkerRoute.CancelRequest
    | WorkerRoute.LoadScript
    | WorkerRoute.Reset
    | WorkerRoute.HardReset
    | WorkerRoute.LevelRepl
    | WorkerRoute.RunTests
    | WorkerRoute.RunTestsStream
    | WorkerRoute.TestDiscovery
    | WorkerRoute.EvalLiveTestFile
    | WorkerRoute.InstrumentationMaps
    | WorkerRoute.Shutdown
    | WorkerRoute.SessionWarmupContext
    | WorkerRoute.HotReload
    | WorkerRoute.HotReloadToggle
    | WorkerRoute.HotReloadResetState
    | WorkerRoute.HotReloadReflectionMode
    | WorkerRoute.HotReloadWatchAll
    | WorkerRoute.HotReloadUnwatchAll
    | WorkerRoute.HotReloadWatchProject
    | WorkerRoute.HotReloadUnwatchProject
    | WorkerRoute.HotReloadWatchDirectory
    | WorkerRoute.HotReloadUnwatchDirectory
    | WorkerRoute.RunApp
    | WorkerRoute.StopApp
    | WorkerRoute.AwaitAppChange
    | WorkerRoute.DebugTest
    | WorkerRoute.DebugTestContinue
    | WorkerRoute.SaveSource
    | WorkerRoute.ApplySaves
    | WorkerRoute.HotReloadLastOutcome -> RouteCredential.DaemonOnly

  /// A request the route table does not know is judged as a daemon route: a 404 for the daemon, a 401 for anyone else.
  let credentialOfRequest (request: RequestRoute) : RouteCredential =
    match WorkerRoute.tryOfRequest request with
    | Some route -> credentialOf route
    | None -> RouteCredential.DaemonOnly

  /// Why a request was refused.
  [<RequireQualifiedAccess>]
  type Refusal =
    | NoCredential
    | NotBearer
    | WrongSecret

  [<RequireQualifiedAccess>]
  type Verdict =
    | Admit
    /// The browser stream: the origin gate decides alone.
    | Exempt
    /// A worker with no secret configured admits what the origin gate admits.
    | Unsecured
    | Refuse of Refusal

  let bearerPrefix = "Bearer "

  let sameSecret (expected: WorkerSecret) (presented: string) : bool =
    CryptographicOperations.FixedTimeEquals(
      ReadOnlySpan(Encoding.UTF8.GetBytes(textOf expected)),
      ReadOnlySpan(Encoding.UTF8.GetBytes presented))

  /// Pure: whether one request may reach its route. `authorization` is the request's `Authorization` header, if any.
  let decide (expected: Expected) (credential: RouteCredential) (authorization: string option) : Verdict =
    match credential, expected with
    | RouteCredential.BrowserStream, _ -> Verdict.Exempt
    | RouteCredential.DaemonOnly, Expected.NotConfigured -> Verdict.Unsecured
    | RouteCredential.DaemonOnly, Expected.Required secret ->
      match authorization with
      | None -> Verdict.Refuse Refusal.NoCredential
      | Some header when not (header.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)) ->
        Verdict.Refuse Refusal.NotBearer
      | Some header ->
        match sameSecret secret (header.Substring(bearerPrefix.Length).Trim()) with
        | true -> Verdict.Admit
        | false -> Verdict.Refuse Refusal.WrongSecret

  module Refusal =
    /// The status a refusal answers with.
    let statusCode (_: Refusal) : int = 401

    /// What the refused caller reads: what the route needs, and where to go instead.
    let describe (refusal: Refusal) : string =
      let what =
        match refusal with
        | Refusal.NoCredential -> "the request carried no Authorization header"
        | Refusal.NotBearer -> "the Authorization header is not a Bearer credential"
        | Refusal.WrongSecret -> "the Bearer credential is not this worker's"
      sprintf
        "Unauthorized: %s. This worker answers only the SageFs daemon that started it, which sends `Authorization: Bearer <secret>`, the daemon's own per-worker credential that no client is given. Reach the session through the daemon instead: its MCP tools or its HTTP API."
        what

  /// The worker's side: the secret its environment holds, read once and then taken out of the process environment so
  /// nothing the worker runs or starts can read it. `run_app` runs the user's app inside the worker process, so the
  /// poison list alone (which covers what the worker starts) is not enough.
  let readExpectedOnce : Lazy<Expected> =
    lazy (
      match Environment.GetEnvironmentVariable secretEnvVar with
      | null -> Expected.NotConfigured
      | text when String.IsNullOrWhiteSpace text -> Expected.NotConfigured
      | text ->
        Environment.SetEnvironmentVariable(secretEnvVar, null)
        Expected.Required(WorkerSecret(text.Trim())))

  /// The daemon's side: which secret each running worker holds. A secret is known by the worker's process id from the
  /// moment it is spawned, and by its port once the worker reports it; `forget` drops both when the process exits.
  module Registry =
    let byProcess = ConcurrentDictionary<int, WorkerSecret>()
    let byPort = ConcurrentDictionary<int, WorkerSecret>()

    let rememberProcess (processId: int) (secret: WorkerSecret) : unit = byProcess[processId] <- secret

    /// The worker reported `baseUrl`: requests to that port carry its secret from now on.
    let bindUrl (processId: int) (baseUrl: string) : unit =
      match byProcess.TryGetValue processId, Uri.TryCreate(baseUrl, UriKind.Absolute) with
      | (true, secret), (true, uri) when uri.IsLoopback -> byPort[uri.Port] <- secret
      | _ -> ()

    let forget (processId: int) : unit =
      match byProcess.TryRemove processId with
      | true, secret ->
        for entry in byPort do
          match entry.Value = secret with
          | true -> byPort.TryRemove(KeyValuePair(entry.Key, entry.Value)) |> ignore
          | false -> ()
      | false, _ -> ()

    /// The secret for a request to `uri`, when it is a worker this daemon started.
    let tryFor (uri: Uri) : WorkerSecret option =
      match uri with
      | null -> None
      | uri when uri.IsAbsoluteUri && uri.IsLoopback ->
        match byPort.TryGetValue uri.Port with
        | true, secret -> Some secret
        | false, _ -> None
      | _ -> None

  /// Adds the worker's secret to every request this client sends to a worker the daemon started, and leaves every
  /// other request alone. Every daemon `HttpClient` that talks to a worker is built on `newHandler`.
  type Handler(inner: HttpMessageHandler) =
    inherit DelegatingHandler(inner)
    override _.SendAsync(request: HttpRequestMessage, cancellationToken) : Task<HttpResponseMessage> =
      match isNull request.Headers.Authorization, Registry.tryFor request.RequestUri with
      | true, Some secret -> request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", textOf secret)
      | _ -> ()
      base.SendAsync(request, cancellationToken)

  /// The handler a daemon client to workers is built on: decompression as before, plus the worker's secret.
  let newHandler () : HttpMessageHandler =
    new Handler(new HttpClientHandler(AutomaticDecompression = System.Net.DecompressionMethods.All))
