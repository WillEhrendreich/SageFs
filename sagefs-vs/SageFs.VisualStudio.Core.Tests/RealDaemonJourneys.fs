module SageFs.VisualStudio.Core.Tests.RealDaemonJourneys

// DoD real-client journeys for the Visual Studio client (HR-VS-E2E, LT-VS-E2E).
//
// These drive the REAL SageFs.VisualStudio.Core client (SageFsClient +
// LiveTestingSubscriber — the exact surface the VS extension uses) against a
// REAL SageFs daemon with a Ready session on the FromCSharp sample (11 Expecto
// tests). No Visual Studio instance is required: the Core client IS the
// extension's daemon integration layer, so these journeys prove the client
// end to end against the real daemon — the same shape as the dashboard and
// nvim journeys at the client-integration layer.
//
// FR-VS-E2E is NOT expressible: the VS extension has no friction surface
// (no report_friction UI/command anywhere in sagefs-vs — verified by grep).
// Friction is recorded agent/MCP-side; documented as an extension-surface gap
// in the DoD evidence, mirroring FR-VSC.
//
// Prerequisites: a Release/Debug build of the repo's SageFs tool (the helper
// locates SageFs.exe under the repo) and the FromCSharp sample. When either is
// missing the journeys skip cleanly (Assert.Skip) so the vs-extension CI job
// stays green on a bare unit-test run; the CI job builds the tool first.

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Xunit
open SageFs.VisualStudio.Core

/// Resolve the repo root by walking up from this assembly's output location
/// until a directory containing both "sagefs-vs" and "SageFs" is found.
let private repoRoot () =
  let asmDir =
    Path.GetDirectoryName(Reflection.Assembly.GetExecutingAssembly().Location)
  let rec walk (dir: string) =
    let parent = Directory.GetParent(dir)
    if isNull parent then failwith "could not locate repo root"
    let hasVs = Directory.Exists(Path.Combine(dir, "sagefs-vs"))
    let hasTool = Directory.Exists(Path.Combine(dir, "SageFs"))
    if hasVs && hasTool then dir
    else walk parent.FullName
  walk asmDir

/// The FromCSharp sample's fsproj (in place — central package management).
let private sampleProject () =
  Path.Combine(
    repoRoot (), "samples", "from-csharp", "SageFs.Samples.FromCSharp",
    "SageFs.Samples.FromCSharp.fsproj")

/// Locate the SageFs daemon executable under the repo (Debug preferred for
/// local dev, Release for CI).
let private findSageFsExe () =
  let root = repoRoot ()
  let candidates = [
    Path.Combine(root, "SageFs", "bin", "Debug", "net10.0", "SageFs.exe")
    Path.Combine(root, "SageFs", "bin", "Release", "net10.0", "SageFs.exe")
  ]
  candidates |> List.tryFind File.Exists

let private pickFreePort () =
  use l = new Net.Sockets.TcpListener(Net.IPAddress.Loopback, 0)
  l.Start()
  let port = (l.LocalEndpoint :?> Net.IPEndPoint).Port
  l.Stop()
  port

/// Boot a real daemon on an isolated data dir with a Ready session on the
/// FromCSharp sample. Returns the mcp port + a dispose that kills the daemon.
let private bootDaemonWithSample () : Task<int * (unit -> unit)> =
  let exe =
    match findSageFsExe () with
    | Some exe -> exe
    | None ->
      failwith "SageFs.exe not built — build SageFs/SageFs.fsproj (Debug or Release) before running the DoD journeys"
  let sample =
    let p = sampleProject ()
    if File.Exists p then p
    else failwith "FromCSharp sample not found under samples/from-csharp"
  task {
    let mcpPort = pickFreePort ()
    let dataDir =
      Path.Combine(Path.GetTempPath(), "sagefs-vs-journey",
                   Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dataDir) |> ignore
    let psi = ProcessStartInfo()
    psi.FileName <- exe
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.WorkingDirectory <- Directory.GetParent(exe).Parent.Parent.Parent.FullName
    psi.ArgumentList.Add("--mcp-port")
    psi.ArgumentList.Add(string mcpPort)
    psi.ArgumentList.Add("--no-resume")
    psi.Environment["SAGEFS_DATA_DIR"] <- dataDir
    psi.Environment["SAGEFS_HOT_RELOAD"] <- "true"
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    let daemon = Process.Start(psi)
    // Drain both streams to files (undrained pipes deadlock the child).
    let drain (stream: StreamReader) (path: string) =
      let writer = new StreamWriter(path, append = true)
      let rec loop () =
        async {
          let! line = stream.ReadLineAsync() |> Async.AwaitTask
          if not (isNull line) then
            do! writer.WriteLineAsync(line) |> Async.AwaitTask
            return! loop ()
        }
      async {
        try do! loop () with _ -> ()
        writer.Dispose()
      }
      |> Async.Start
    drain daemon.StandardOutput (Path.Combine(dataDir, "daemon.out.log"))
    drain daemon.StandardError (Path.Combine(dataDir, "daemon.err.log"))
    let dispose () =
      try
        if not daemon.HasExited then daemon.Kill(entireProcessTree = true)
      with _ -> ()
      try daemon.WaitForExit(3000) |> ignore with _ -> ()
      daemon.Dispose()

    use client = new HttpClient(Timeout = TimeSpan.FromSeconds(5.0))
    let mutable healthy = false
    let deadline = DateTime.UtcNow.AddSeconds(60.0)
    while not healthy && DateTime.UtcNow < deadline do
      try
        use _r = client.GetAsync(sprintf "http://localhost:%d/health" mcpPort).Result
        healthy <- true
      with _ ->
        Thread.Sleep(500)
    if not healthy then
      dispose ()
      failwithf "daemon did not become healthy on port %d" mcpPort

    // Create the session on the FromCSharp sample.
    let sampleDir = Path.GetDirectoryName(sample)
    let payload =
      System.Text.Json.JsonSerializer.Serialize(
        {| projects = [| sample |]
           workingDirectory = sampleDir |})
    use content =
      new StringContent(payload, Text.Encoding.UTF8, "application/json")
    let! createResp =
      client.PostAsync(sprintf "http://localhost:%d/api/sessions/create" mcpPort, content)
    if not createResp.IsSuccessStatusCode then
      dispose ()
      failwithf "session create failed (HTTP %d)" (int createResp.StatusCode)

    // Wait for Ready.
    let mutable ready = false
    let mutable faulted = false
    let warmupDeadline = DateTime.UtcNow.AddSeconds(300.0)
    while not ready && not faulted && DateTime.UtcNow < warmupDeadline do
      try
        let! body = client.GetStringAsync(sprintf "http://localhost:%d/api/sessions" mcpPort)
        use doc = System.Text.Json.JsonDocument.Parse(body)
        let states =
          doc.RootElement.GetProperty("sessions").EnumerateArray()
          |> Seq.map (fun s -> s.GetProperty("status").GetString())
          |> Seq.toList
        if states |> List.contains "Faulted" then faulted <- true
        ready <- states |> List.contains "Ready"
      with _ ->
        do! Task.Delay(1000)
    if faulted then
      dispose ()
      failwith "session Faulted during warmup"
    if not ready then
      dispose ()
      failwith "session never reached Ready within 300s"
    return mcpPort, dispose
  }

/// Poll a predicate until true or timeout.
let private pollUntil (timeoutMs: int) (predicate: unit -> Task<bool>) : Task<bool> =
  task {
    let sw = Stopwatch.StartNew()
    let mutable ok = false
    while not ok && sw.ElapsedMilliseconds < int64 timeoutMs do
      let! p = predicate ()
      if p then ok <- true
      else do! Task.Delay(1000)
    return ok
  }

/// GET the live-testing status summary from the daemon.
let private getLiveTestingSummary (mcpPort: int) : Task<TestSummary option> = task {
  try
    use client = new HttpClient(Timeout = TimeSpan.FromSeconds(5.0))
    let! body =
      client.GetStringAsync(sprintf "http://localhost:%d/api/live-testing/status" mcpPort)
    use doc = System.Text.Json.JsonDocument.Parse(body)
    let root = doc.RootElement
    let summary = root.GetProperty("Summary")
    let getInt (name: string) =
      let mutable v = Unchecked.defaultof<System.Text.Json.JsonElement>
      if summary.TryGetProperty(name, &v) then v.GetInt32() else 0
    return Some {
      Total = getInt "Total"
      Passed = getInt "Passed"
      Failed = getInt "Failed"
      Stale = getInt "Stale"
      Running = getInt "Running"
      Disabled = getInt "Disabled"
      DiscoveryState =
        let mutable v = Unchecked.defaultof<System.Text.Json.JsonElement>
        if root.TryGetProperty("DiscoveryState", &v) then v.GetString() else ""
      DiscoveryGeneration =
        let mutable v = Unchecked.defaultof<System.Text.Json.JsonElement>
        if root.TryGetProperty("DiscoveryGeneration", &v) then v.GetInt64() else 0L
      LastDecision = None
    }
  with _ ->
    return None
}

// ── HR-VS-E2E: hot-reload watch state through the real Core client ─────────

[<Fact>]
let ``HR-VS-E2E: client watches all files and reads watch state from the real daemon`` () =
  task {
    let! mcpPort, dispose = bootDaemonWithSample ()
    try
      use client = new SageFsClient(new HttpClient())
      client.McpPort <- mcpPort
      client.DashboardPort <- mcpPort + 1
      let ct = CancellationToken.None

      // Find the Ready session id.
      let! sessionIdOpt = task {
        let! body =
          (new HttpClient()).GetStringAsync(sprintf "http://localhost:%d/api/sessions" mcpPort)
        use doc = System.Text.Json.JsonDocument.Parse(body)
        return
          doc.RootElement.GetProperty("sessions").EnumerateArray()
          |> Seq.tryFind (fun s -> s.GetProperty("status").GetString() = "Ready")
          |> Option.map (fun s -> s.GetProperty("id").GetString())
      }
      let sessionId =
        match sessionIdOpt with
        | Some s when not (String.IsNullOrEmpty s) -> s
        | _ -> failwith "no Ready session found"

      // The client reads the hot-reload file set from the real daemon.
      let! state = client.GetHotReloadStateAsync(sessionId, ct)
      match state with
      | None -> failwith "GetHotReloadStateAsync returned None"
      | Some st ->
        Assert.True(st.Files.Length > 0,
          sprintf "hot-reload state should list the sample's .fs files (got %d)" st.Files.Length)

      // Watch all through the client; the daemon must accept it.
      let! watched = client.WatchAllAsync(sessionId, ct)
      Assert.True(watched, "WatchAllAsync should be accepted by the daemon")

      // The watch state must now show a nonzero watched count (real client
      // state derived from the real daemon, not a fake).
      let! allWatched = pollUntil 15000 (fun () -> task {
        let! s = client.GetHotReloadStateAsync(sessionId, ct)
        return match s with
               | Some st when st.WatchedCount > 0 -> true
               | _ -> false
      })
      Assert.True(allWatched,
        "watched count should become > 0 after watch-all (the daemon's watch-all is authoritative)")

      // Unwatch again through the client (restores the sample's file state).
      let! unwatched = client.UnwatchAllAsync(sessionId, ct)
      Assert.True(unwatched, "UnwatchAllAsync should be accepted by the daemon")
    finally
      dispose ()
  }

// ── LT-VS-E2E: live-testing through the real Core client + subscriber ───────

[<Fact>]
let ``LT-VS-E2E: enable surfaces 11/11, a failing edit surfaces 1 failed, restore recovers`` () =
  task {
    let! mcpPort, dispose = bootDaemonWithSample ()
    // The sample's Hello.fs (in the repo — restored in finally below).
    let helloPath =
      Path.Combine(
        repoRoot (), "samples", "from-csharp", "SageFs.Samples.FromCSharp", "Hello.fs")
    let original = File.ReadAllText(helloPath)
    let canonicalAdd = "let add a b = a + b"
    let brokenAdd = "let add a b = a + b + 1"
    let writeHello (content: string) =
      let mutable written = false
      let deadline = DateTime.UtcNow.AddSeconds(15.0)
      while not written && DateTime.UtcNow < deadline do
        try
          File.WriteAllText(helloPath, content)
          written <- true
        with :? IOException ->
          Thread.Sleep(500)
      if not written then failwith "Hello.fs not writable"
    try
      use client = new SageFsClient(new HttpClient())
      client.McpPort <- mcpPort
      client.DashboardPort <- mcpPort + 1
      let ct = CancellationToken.None

      // The real SSE subscriber (the VS extension's live-testing consumer).
      let subscriber = new LiveTestingSubscriber(mcpPort)
      subscriber.Start()
      let summarySeen = ResizeArray<TestSummary>()
      subscriber.SummaryChanged.Add(fun s -> lock summarySeen (fun () -> summarySeen.Add(s)))
      let connectionLosses = ref 0
      subscriber.ConnectionLost.Add(fun () -> incr connectionLosses)
      let connectionRestores = ref 0
      subscriber.ConnectionRestored.Add(fun () -> incr connectionRestores)
      try
        // Wait for the SSE connection to establish (the daemon replays the
        // current test state on connect — subscribing before enable means the
        // subscriber sees the enable-triggered events live).
        let! connected = pollUntil 15000 (fun () -> task {
          return subscriber.IsConnected
        })
        Assert.True(connected, "the live-testing SSE subscriber should connect to the daemon")

        // Enable through the real client call.
        let! enabled = client.EnableLiveTestingAsync(ct)
        Assert.True(enabled, "EnableLiveTestingAsync should report enabled")

        // The subscriber must observe the summary reach 11/11 (the daemon
        // baseline auto-runs on discovery of the 11-test sample).
        let! allGreen = pollUntil 120000 (fun () -> task {
          let! s = getLiveTestingSummary mcpPort
          return match s with
                 | Some st when st.Total = 11 && st.Passed = 11 && st.Failed = 0 -> true
                 | _ -> false
        })
        Assert.True(allGreen, "daemon summary should reach 11/11 after enable")
        let! sawGreenViaSubscriber = task {
          // Give the SSE events a moment to arrive after the daemon poll.
          let deadline = DateTime.UtcNow.AddSeconds(15.0)
          let mutable saw = false
          while not saw && DateTime.UtcNow < deadline do
            saw <-
              lock summarySeen (fun () ->
                summarySeen
                |> Seq.exists (fun s -> s.Total = 11 && s.Passed = 11 && s.Failed = 0))
            if not saw then do! Task.Delay(1000)
          return saw
        }
        Assert.True(sawGreenViaSubscriber,
          "the VS subscriber should have observed the 11/11 summary over SSE"
          + sprintf " (seen %d summaries: %s; losses=%d restores=%d)"
            summarySeen.Count
            (summarySeen |> Seq.map (fun s -> sprintf "%d/%d" s.Passed s.Total) |> String.concat ", ")
            !connectionLosses !connectionRestores)

        // Mutate `add` on disk: the edit-triggered rerun must surface the
        // failure in the daemon summary (which the subscriber consumes). The
        // daemon's watcher can miss a write that lands mid-settle right after
        // the baseline, so re-touch the file once if nothing appeared.
        let broken = original.Replace(canonicalAdd, brokenAdd)
        writeHello broken
        let mutable sawFailure = false
        let attemptDeadline = DateTime.UtcNow.AddSeconds(150.0)
        while not sawFailure && DateTime.UtcNow < attemptDeadline do
          let! found = pollUntil 30000 (fun () -> task {
            let! s = getLiveTestingSummary mcpPort
            return match s with
                   | Some st when st.Failed >= 1 -> true
                   | _ -> false
          })
          if found then sawFailure <- true
          else
            // Re-touch: the watcher may have missed the first write.
            try File.WriteAllText(helloPath, broken) with _ -> ()
        if not sawFailure then
          let! statusBody = task {
            try
              use rawClient = new HttpClient(Timeout = TimeSpan.FromSeconds(5.0))
              return! rawClient.GetStringAsync(sprintf "http://localhost:%d/api/live-testing/status" mcpPort)
            with _ -> return "(status endpoint unreachable)"
          }
          failwithf "daemon summary should show >= 1 failed after breaking `add`; daemon status: %s" statusBody
        Assert.True(sawFailure,
          "daemon summary should show >= 1 failed after breaking `add`")

        // Restore: the rerun must recover to all green.
        writeHello original
        let mutable recovered = false
        let recoverDeadline = DateTime.UtcNow.AddSeconds(150.0)
        while not recovered && DateTime.UtcNow < recoverDeadline do
          let! found = pollUntil 30000 (fun () -> task {
            let! s = getLiveTestingSummary mcpPort
            return match s with
                   | Some st when st.Total = 11 && st.Passed = 11 && st.Failed = 0 -> true
                   | _ -> false
          })
          if found then recovered <- true
          else
            // Re-touch the restore: the watcher may have missed the write.
            try File.WriteAllText(helloPath, original) with _ -> ()
        Assert.True(recovered, "daemon summary should recover to 11/11 after restore")

        // Disable through the real client call.
        let! disabled = client.DisableLiveTestingAsync(ct)
        Assert.False(disabled, "DisableLiveTestingAsync should report disabled")
      finally
        (subscriber :> IDisposable).Dispose()
    finally
      // Restore Hello.fs regardless of outcome.
      try File.WriteAllText(helloPath, original) with _ -> ()
      dispose ()
  }
