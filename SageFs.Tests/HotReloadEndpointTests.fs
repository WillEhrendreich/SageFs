module SageFs.Tests.HotReloadEndpointTests

open System.Net.Http
open System.Text
open System.Text.Json
open Expecto
open SageFs
open SageFs.Core
open SageFs.Server

let private startTestServer () = task {
  let handler (_msg: WorkerProtocol.WorkerMessage) = async {
    return WorkerProtocol.WorkerResponse.WorkerShuttingDown
  }
  let stateRef = ref HotReloadState.empty
  let projectFiles = [
    @"C:\proj\src\Lib.fs"
    @"C:\proj\src\Main.fs"
    @"C:\proj\tests\Tests.fs"
  ]
  let! server = WorkerHttpTransport.startServer handler stateRef projectFiles (fun () -> WarmupContext.empty) 0
  return server, stateRef
}

let private httpClient = new HttpClient()

let private getJson (url: string) = task {
  let! resp = httpClient.GetStringAsync(url)
  return JsonDocument.Parse(resp)
}

let private postJson (url: string) (body: string) = task {
  use content = new StringContent(body, Encoding.UTF8, "application/json")
  let! resp = httpClient.PostAsync(url, content)
  let! respBody = resp.Content.ReadAsStringAsync()
  return JsonDocument.Parse(respBody)
}

let private getHotReloadRoot (baseUrl: string) = task {
  let! (doc: JsonDocument) = getJson (sprintf "%s/hotreload" baseUrl)
  return doc.RootElement
}

[<Tests>]
let hotReloadEndpointTests =
  testList "HotReload HTTP endpoints" [
    testTask "GET /hotreload returns all files with watched status" {
      let! (result: WorkerHttpTransport.HttpWorkerServer * HotReloadState.T ref) = startTestServer ()
      let server : WorkerHttpTransport.HttpWorkerServer = fst result
      try
        let! (root: JsonElement) = getHotReloadRoot server.BaseUrl
        root.GetProperty("files").GetArrayLength()
        |> Flip.Expect.equal "3 project files" 3
        root.GetProperty("watchedCount").GetInt32()
        |> Flip.Expect.equal "0 watched initially" 0
      finally (server :> System.IDisposable).Dispose()
    }
    testTask "POST /hotreload/toggle toggles a file on and off" {
      let! (result: WorkerHttpTransport.HttpWorkerServer * HotReloadState.T ref) = startTestServer ()
      let server : WorkerHttpTransport.HttpWorkerServer = fst result
      try
        let! _ = postJson (sprintf "%s/hotreload/toggle" server.BaseUrl) """{"path":"C:\\proj\\src\\Lib.fs"}"""
        let! (root: JsonElement) = getHotReloadRoot server.BaseUrl
        root.GetProperty("watchedCount").GetInt32()
        |> Flip.Expect.equal "1 watched after toggle on" 1
        let! _ = postJson (sprintf "%s/hotreload/toggle" server.BaseUrl) """{"path":"C:\\proj\\src\\Lib.fs"}"""
        let! (root2: JsonElement) = getHotReloadRoot server.BaseUrl
        root2.GetProperty("watchedCount").GetInt32()
        |> Flip.Expect.equal "0 watched after toggle off" 0
      finally (server :> System.IDisposable).Dispose()
    }
    testTask "POST /hotreload/watch-all watches all project files" {
      let! (result: WorkerHttpTransport.HttpWorkerServer * HotReloadState.T ref) = startTestServer ()
      let server : WorkerHttpTransport.HttpWorkerServer = fst result
      try
        let! _ = postJson (sprintf "%s/hotreload/watch-all" server.BaseUrl) "{}"
        let! (root: JsonElement) = getHotReloadRoot server.BaseUrl
        root.GetProperty("watchedCount").GetInt32()
        |> Flip.Expect.equal "all 3 watched" 3
      finally (server :> System.IDisposable).Dispose()
    }
    testTask "POST /hotreload/unwatch-all clears everything" {
      let! (result: WorkerHttpTransport.HttpWorkerServer * HotReloadState.T ref) = startTestServer ()
      let server : WorkerHttpTransport.HttpWorkerServer = fst result
      try
        let! _ = postJson (sprintf "%s/hotreload/watch-all" server.BaseUrl) "{}"
        let! _ = postJson (sprintf "%s/hotreload/unwatch-all" server.BaseUrl) "{}"
        let! (root: JsonElement) = getHotReloadRoot server.BaseUrl
        root.GetProperty("watchedCount").GetInt32()
        |> Flip.Expect.equal "0 watched after unwatch-all" 0
      finally (server :> System.IDisposable).Dispose()
    }
    testTask "POST /hotreload/watch-project watches only matching directory" {
      let! (result: WorkerHttpTransport.HttpWorkerServer * HotReloadState.T ref) = startTestServer ()
      let server : WorkerHttpTransport.HttpWorkerServer = fst result
      try
        let! _ = postJson (sprintf "%s/hotreload/watch-project" server.BaseUrl) """{"project":"C:\\proj\\src"}"""
        let! (root: JsonElement) = getHotReloadRoot server.BaseUrl
        root.GetProperty("watchedCount").GetInt32()
        |> Flip.Expect.equal "2 src files watched" 2
      finally (server :> System.IDisposable).Dispose()
    }
  ]
