namespace SageFs.Tests.Integration

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Test fixture that manages daemon and MCP server instances
type SharedSageFsFixture() =
  let mutable mcpProcess: Process option = None
  let mutable mcpClient: HttpClient option = None

  member val McpPort = 54321
  member val IsReady = false with get, set

  /// Start sagefs-web with daemon mode
  /// Start MCP server (connects to daemon)
  member this.StartMcp() =
    task {
      let startInfo = ProcessStartInfo()
      startInfo.FileName <- "sagefs-mcp"
      startInfo.UseShellExecute <- false
      startInfo.RedirectStandardOutput <- true
      startInfo.RedirectStandardError <- true
      startInfo.CreateNoWindow <- true

      let proc = Process.Start(startInfo)
      mcpProcess <- Some proc

      // Give MCP server time to initialize and connect to daemon
      do! Task.Delay(5000)

      let mutable attempts = 0
      let mutable ready = false

      // Try for 30 seconds (60 attempts * 500ms)
      while not ready && attempts < 60 do
        do! Task.Delay(500)
        let! health = this.CheckMcpHealth()

        if health then
          ready <- true
          this.IsReady <- true
        else
          attempts <- attempts + 1

      if not ready then
        // Check if process is still running
        let processStatus = 
          if proc.HasExited then 
            $"Process exited with code {proc.ExitCode}"
          else 
            "Process still running but not responding"
        failwith $"MCP server failed to start within 30 seconds. Status: {processStatus}"
    }

  /// Send code via MCP HTTP endpoint
  member this.SendViaMcp(code: string, agentName: string) =
    task {
      let client =
        match mcpClient with
        | Some c -> c
        | None ->
          let c = new HttpClient()
          c.BaseAddress <- Uri($"http://localhost:{this.McpPort}")
          c.Timeout <- TimeSpan.FromSeconds(10.0)
          mcpClient <- Some c
          c

      let request = {|
        jsonrpc = "2.0"
        id = 1
        method = "tools/call"
        ``params`` = {|
          name = "send_fsharp_code"
          arguments = {| code = code; agentName = agentName |}
        |}
      |}

      let json = JsonSerializer.Serialize(request)
      let content = new StringContent(json, Encoding.UTF8, "application/json")

      let! response = client.PostAsync("/mcp", content)
      let! responseText = response.Content.ReadAsStringAsync()

      return responseText
    }

  /// Check if MCP server is responsive
  member this.CheckMcpHealth() =
    task {
      try
        let client =
          match mcpClient with
          | Some c -> c
          | None ->
            let c = new HttpClient()
            c.BaseAddress <- Uri($"http://localhost:{this.McpPort}")
            c.Timeout <- TimeSpan.FromSeconds(2.0)
            mcpClient <- Some c
            c

        let! response = client.GetAsync("/health")
        return response.IsSuccessStatusCode
      with _ ->
        return false
    }

  interface IDisposable with
    member this.Dispose() =
      // Cleanup
      match mcpClient with
      | Some client -> client.Dispose()
      | None -> ()

      match mcpProcess with
      | Some proc ->
        if not proc.HasExited then
          proc.Kill(entireProcessTree = true)

        proc.Dispose()
      | None -> ()

