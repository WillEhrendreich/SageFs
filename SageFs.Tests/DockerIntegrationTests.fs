module SageFs.Tests.DockerIntegrationTests

open System
open System.IO
open System.Net.Http
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Containers
open DotNet.Testcontainers.Configurations
open Expecto
open Expecto.Flip

// ---------------------------------------------------------------------------
// Docker Image Builder
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module DockerFixture =

  let repoRoot =
    let env = Environment.GetEnvironmentVariable("SAGEFS_REPO_ROOT")
    if String.IsNullOrEmpty env then @"C:\Code\Repos\SageFs"
    else env

  let fixtureDir =
    Path.Combine(repoRoot, "SageFs.Tests", "fixtures", "TestWorkspace")

  /// Use pre-built sagefs-daemon-test:latest image.
  /// Build with: docker build -f Dockerfile.vscode-test --target sagefs-base -t sagefs-daemon-test:latest .
  let sageFsDaemonImageName = "sagefs-daemon-test:latest"

  /// Use pre-built sagefs-vscode-test:latest image.
  /// Build with: docker build -f Dockerfile.vscode-test --target vscode-test -t sagefs-vscode-test:latest .
  let vscodeTestImageName = "sagefs-vscode-test:latest"

  let isDockerAvailable =
    lazy(
      try
        let psi =
          Diagnostics.ProcessStartInfo(
            "docker", "info",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true)
        use p = Diagnostics.Process.Start(psi)
        p.WaitForExit(5000) |> ignore
        if p.ExitCode <> 0 then false
        else
          let imgPsi =
            Diagnostics.ProcessStartInfo(
              "docker", sprintf "image inspect %s" sageFsDaemonImageName,
              RedirectStandardOutput = true,
              RedirectStandardError = true,
              UseShellExecute = false,
              CreateNoWindow = true)
          use imgP = Diagnostics.Process.Start(imgPsi)
          imgP.WaitForExit(5000) |> ignore
          imgP.ExitCode = 0
      with _ -> false)

// ---------------------------------------------------------------------------
// SageFs Daemon Container
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module SageFsContainer =

  let daemonPort = 37749

  /// Create a SageFs daemon container with the fixture project mounted.
  /// The container exposes a random host port mapped to 37749.
  let create () =
    ContainerBuilder()
      .WithImage(DockerFixture.sageFsDaemonImageName)
      .WithBindMount(
        DockerFixture.fixtureDir,
        "/workspace/TestWorkspace",
        AccessMode.ReadOnly)
      .WithPortBinding(daemonPort, true)
      .WithCommand(
        [| "--proj"
           "/workspace/TestWorkspace/TestWorkspace.fsproj" |])
      .WithWaitStrategy(
        Wait.ForUnixContainer()
          .UntilHttpRequestIsSucceeded(fun r ->
            r.ForPath("/health").ForPort(uint16 daemonPort)))
      .Build()

  /// Start a container and return the host URL for HTTP requests.
  let startAndGetUrl (container: IContainer) = task {
    do! container.StartAsync()
    let hostPort = container.GetMappedPublicPort(daemonPort)
    return sprintf "http://%s:%d" container.Hostname hostPort
  }

// ---------------------------------------------------------------------------
// VS Code Container (with CDP for Playwright)
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module VscodeContainer =

  let cdpPort = 9222

  /// Create a headless VS Code container with CDP exposed.
  /// Expects VSIX extension to be bind-mounted at /tmp/sagefs.vsix.
  let create (vsixPath: string) =
    ContainerBuilder()
      .WithImage(DockerFixture.vscodeTestImageName)
      .WithBindMount(
        DockerFixture.fixtureDir,
        "/workspace/TestWorkspace",
        AccessMode.ReadOnly)
      .WithBindMount(vsixPath, "/tmp/sagefs.vsix", AccessMode.ReadOnly)
      .WithPortBinding(cdpPort, true)
      .WithCommand(
        [| "bash"; "-c"
           sprintf
             "code --install-extension /tmp/sagefs.vsix --force && \
              code --remote-debugging-port=%d \
                   --user-data-dir=/tmp/vscode-test \
                   --no-sandbox \
                   --disable-gpu \
                   /workspace/TestWorkspace"
             cdpPort |])
      .WithWaitStrategy(
        Wait.ForUnixContainer()
          .UntilHttpRequestIsSucceeded(fun r ->
            r.ForPath("/json/version").ForPort(uint16 cdpPort)))
      .Build()

  /// Start container and return the CDP endpoint URL for Playwright.
  let startAndGetCdpUrl (container: IContainer) = task {
    do! container.StartAsync()
    let hostPort = container.GetMappedPublicPort(cdpPort)
    return sprintf "http://%s:%d" container.Hostname hostPort
  }

// ---------------------------------------------------------------------------
// HTTP Test Helpers
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module DockerHttpHelpers =

  let client = new HttpClient(Timeout = TimeSpan.FromSeconds(10.0))

  let getJson (baseUrl: string) (path: string) = task {
    let url = sprintf "%s%s" baseUrl path
    let! resp = client.GetAsync(url)
    let! body = resp.Content.ReadAsStringAsync()
    return (int resp.StatusCode, body)
  }

  let postJson (baseUrl: string) (path: string) (json: string) = task {
    let url = sprintf "%s%s" baseUrl path
    let content =
      new StringContent(json, Text.Encoding.UTF8, "application/json")
    let! resp = client.PostAsync(url, content)
    let! body = resp.Content.ReadAsStringAsync()
    return (int resp.StatusCode, body)
  }

  /// Poll until a condition is met or timeout.
  let waitFor (timeoutMs: int) (intervalMs: int) (check: unit -> Task<bool>) =
    task {
      let sw = Diagnostics.Stopwatch.StartNew()
      let mutable found = false
      while not found && sw.ElapsedMilliseconds < int64 timeoutMs do
        let! ok = check ()
        if ok then found <- true
        else do! Task.Delay(intervalMs)
      return found
    }

// ---------------------------------------------------------------------------
// Test Wrapper — skip if Docker unavailable
// ---------------------------------------------------------------------------

let dockerTest name (body: unit -> Task<unit>) =
  if not DockerFixture.isDockerAvailable.Value then
    ptestCase (sprintf "[Docker] %s" name) ignore
  else
    testCase (sprintf "[Docker] %s" name) (fun () ->
      body().GetAwaiter().GetResult())

// ---------------------------------------------------------------------------
// SageFs Daemon Integration Tests (Docker)
// ---------------------------------------------------------------------------

[<Tests>]
let daemonDockerTests =
  testList "Docker SageFs daemon" [
    dockerTest "container starts and responds to health check" (fun () ->
      task {
        use container = SageFsContainer.create ()
        let! baseUrl = SageFsContainer.startAndGetUrl container
        let! (status, body) = DockerHttpHelpers.getJson baseUrl "/health"
        status |> Expect.equal "health should return 200" 200
        body |> Expect.stringContains "should contain healthy" "healthy"
      })

    dockerTest "status endpoint returns session info" (fun () ->
      task {
        use container = SageFsContainer.create ()
        let! baseUrl = SageFsContainer.startAndGetUrl container
        let! ready =
          DockerHttpHelpers.waitFor 30000 1000 (fun () -> task {
            try
              let! (status, _) = DockerHttpHelpers.getJson baseUrl "/api/status"
              return status = 200
            with _ -> return false
          })
        ready |> Expect.isTrue "status endpoint should respond"
        let! (status, body) = DockerHttpHelpers.getJson baseUrl "/api/status"
        status |> Expect.equal "should return 200" 200
        body |> Expect.stringContains "should contain sessions" "sessions"
      })

    dockerTest "enable live testing" (fun () ->
      task {
        use container = SageFsContainer.create ()
        let! baseUrl = SageFsContainer.startAndGetUrl container
        let! ready =
          DockerHttpHelpers.waitFor 30000 1000 (fun () -> task {
            try
              let! (status, _) = DockerHttpHelpers.getJson baseUrl "/health"
              return status = 200
            with _ -> return false
          })
        ready |> Expect.isTrue "health should respond before enable"
        let! (status, body) =
          DockerHttpHelpers.postJson baseUrl "/api/live-testing/enable" "{}"
        status |> Expect.equal "enable should return 200" 200
        body |> Expect.stringContains "should have enabled field" "enabled"
      })

    dockerTest "run-policy endpoint accepts category updates" (fun () ->
      task {
        use container = SageFsContainer.create ()
        let! baseUrl = SageFsContainer.startAndGetUrl container
        let! ready =
          DockerHttpHelpers.waitFor 30000 1000 (fun () -> task {
            try
              let! (status, _) = DockerHttpHelpers.getJson baseUrl "/health"
              return status = 200
            with _ -> return false
          })
        ready |> Expect.isTrue "health should respond before run-policy"
        let json = """{"category":"unit","policy":"save"}"""
        let! (status, _) =
          DockerHttpHelpers.postJson baseUrl "/api/live-testing/run-policy" json
        status |> Expect.equal "run-policy should return 200" 200
      })

    dockerTest "recent events endpoint returns data" (fun () ->
      task {
        use container = SageFsContainer.create ()
        let! baseUrl = SageFsContainer.startAndGetUrl container
        let! ready =
          DockerHttpHelpers.waitFor 30000 1000 (fun () -> task {
            try
              let! (status, _) = DockerHttpHelpers.getJson baseUrl "/health"
              return status = 200
            with _ -> return false
          })
        ready |> Expect.isTrue "health should respond before recent-events"
        let! (status, body) =
          DockerHttpHelpers.getJson baseUrl "/api/recent-events"
        status |> Expect.equal "should return 200" 200
        body.Trim()
        |> fun b -> b.StartsWith("[")
        |> Expect.isTrue "should return JSON array"
      })

    dockerTest "fixture project has expected test functions" (fun () ->
      task {
        use container = SageFsContainer.create ()
        let! baseUrl = SageFsContainer.startAndGetUrl container
        let! ready =
          DockerHttpHelpers.waitFor 60000 2000 (fun () -> task {
            try
              let! (status, _) =
                DockerHttpHelpers.getJson baseUrl "/api/status"
              return status = 200
            with _ -> return false
          })
        ready |> Expect.isTrue "daemon should be ready"
      })
  ]
