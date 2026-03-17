module SageFs.VisualStudio.Core.Tests.BufferChangeRequestTests

open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open SageFs.VisualStudio.Core

let private session id workingDirectory =
  { Id = id
    ProjectNames = []
    State = "Ready"
    WorkingDirectory = workingDirectory
    EvalCount = 0 }

type private CapturingHttpMessageHandler(responseBody: string) =
  inherit HttpMessageHandler()

  let mutable lastRequest: HttpRequestMessage option = None
  let mutable lastBody: string option = None

  member _.LastRequest = lastRequest
  member _.LastBody = lastBody

  override _.SendAsync(request, _) =
    task {
      lastRequest <- Some request
      if not (isNull request.Content) then
        let! body = request.Content.ReadAsStringAsync()
        lastBody <- Some body

      let response = new HttpResponseMessage(HttpStatusCode.Accepted)
      response.Content <- new StringContent(responseBody, Encoding.UTF8, "application/json")
      return response
    }

[<Fact>]
let ``resolveSessionOwnership returns UniqueMatch for uniquely owned file`` () =
  let result =
    BufferChangeRequest.resolveSessionOwnership
      [ session "s1" @"C:\Code\SageFs"
        session "s2" @"C:\Code\Other" ]
      @"C:\Code\SageFs\Features\Game.fs"

  result |> should equal (UniqueMatch "s1")

[<Fact>]
let ``resolveSessionOwnership returns AmbiguousMatch for overlapping session roots`` () =
  let result =
    BufferChangeRequest.resolveSessionOwnership
      [ session "root" @"C:\Code"
        session "child" @"C:\Code\SageFs" ]
      @"C:\Code\SageFs\Features\Game.fs"

  result |> should equal (AmbiguousMatch ["root"; "child"])

[<Fact>]
let ``tryCreate builds request for unique compiled file owner`` () =
  let request =
    BufferChangeRequest.tryCreate
      [ session "s1" @"C:\Code\SageFs"
        session "s2" @"C:\Code\Other" ]
      @"C:\Code\SageFs\Features\Game.fs"
      "module Game"

  match request with
  | Some built ->
      built.SessionId |> should equal "s1"
      built.FilePath |> should equal @"C:\Code\SageFs\Features\Game.fs"
      built.Content |> should equal "module Game"
  | None ->
      failwith "expected a buffer change request"

[<Fact>]
let ``tryCreate accepts signature files`` () =
  let request =
    BufferChangeRequest.tryCreate
      [ session "s1" @"C:\Code\SageFs" ]
      @"C:\Code\SageFs\Features\Game.fsi"
      "module Game"

  match request with
  | Some built ->
      built.SessionId |> should equal "s1"
  | None ->
      failwith "expected a buffer change request"

[<Fact>]
let ``tryCreate refuses script files for compiled buffer bridge`` () =
  let request =
    BufferChangeRequest.tryCreate
      [ session "s1" @"C:\Code\SageFs" ]
      @"C:\Code\SageFs\scratch.fsx"
      "printfn \"hi\""

  request |> should equal None

[<Fact>]
let ``tryCreate refuses prefix neighbor false positives`` () =
  let request =
    BufferChangeRequest.tryCreate
      [ session "s1" @"C:\Code\SageFs" ]
      @"C:\Code\SageFs-Other\Game.fs"
      "module Game"

  request |> should equal None

[<Fact>]
let ``tryCreate refuses ambiguous ownership`` () =
  let request =
    BufferChangeRequest.tryCreate
      [ session "root" @"C:\Code"
        session "child" @"C:\Code\SageFs" ]
      @"C:\Code\SageFs\Features\Game.fs"
      "module Game"

  request |> should equal None

[<Fact>]
let ``toJson serializes filePath and content`` () =
  let json =
    { SessionId = "s1"
      FilePath = @"C:\Code\SageFs\Features\Game.fs"
      Content = "module Game" }
    |> BufferChangeRequest.toJson

  json |> should haveSubstring "\"filePath\":\"C:\\\\Code\\\\SageFs\\\\Features\\\\Game.fs\""
  json |> should haveSubstring "\"content\":\"module Game\""

[<Fact>]
let ``PostBufferChangedAsync posts to session scoped buffer changed route`` () =
  task {
    let handler = new CapturingHttpMessageHandler("""{"success":true}""")
    use client = new SageFsClient(handler)

    let! ok =
      client.PostBufferChangedAsync(
        { SessionId = "abc12345"
          FilePath = @"C:\Code\SageFs\Features\Game.fs"
          Content = "module Game" },
        CancellationToken.None)

    ok |> should equal true
    handler.LastRequest.Value.RequestUri.AbsolutePath |> should equal "/api/sessions/abc12345/buffer-changed"
    handler.LastBody.Value |> should haveSubstring "\"filePath\":\"C:\\\\Code\\\\SageFs\\\\Features\\\\Game.fs\""
    handler.LastBody.Value |> should haveSubstring "\"content\":\"module Game\""
  }
