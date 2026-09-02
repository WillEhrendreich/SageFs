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

[<Fact>]
let ``ResolveSessionId prefers document-owning session over first session`` () =
  let sessions =
    [ session "first" @"C:\Code\Other"
      session "owner" @"C:\Code\SageFs" ]

  BufferChangeRequestInterop.ResolveSessionId(sessions, @"C:\Code\SageFs\Features\Game.fs")
  |> should equal "owner"

[<Fact>]
let ``ResolveSessionId falls back to first session when nothing owns the file`` () =
  let sessions =
    [ session "first" @"C:\Code\Other"
      session "second" @"C:\Code\SageFs" ]

  BufferChangeRequestInterop.ResolveSessionId(sessions, @"C:\Code\Unrelated\Notes.md")
  |> should equal "first"

[<Fact>]
let ``ResolveSessionId returns null when no sessions exist`` () =
  BufferChangeRequestInterop.ResolveSessionId([], @"C:\Code\SageFs\Game.fs")
  |> should equal null

[<Fact>]
let ``ResolveSessionId breaks ambiguous ownership toward the owning session`` () =
  let sessions =
    [ session "root" @"C:\Code"
      session "child" @"C:\Code\SageFs" ]

  BufferChangeRequestInterop.ResolveSessionId(sessions, @"C:\Code\SageFs\Features\Game.fs")
  |> should equal "root"

[<Fact>]
let ``WatchPath treats paths under directory as watched`` () =
  WatchPath.pathUnderDirectory @"C:\Code\SageFs" @"C:\Code\SageFs\src\App.fs"
  |> should equal true

[<Fact>]
let ``WatchPath treats sibling-prefix as not watched`` () =
  WatchPath.pathUnderDirectory @"C:\Code\SageFs" @"C:\Code\SageFs-Other\src\App.fs"
  |> should equal false

[<Fact>]
let ``WatchPath treats exact directory match as watched`` () =
  WatchPath.pathUnderDirectory @"C:\Code\SageFs" @"C:\Code\SageFs"
  |> should equal true

[<Fact>]
let ``WatchPath normalizes trailing separators and forward slashes`` () =
  WatchPath.pathUnderDirectory @"C:/Code/SageFs/" @"C:\Code\SageFs\src\App.fs"
  |> should equal true

// ── Truthful failure semantics (plan Phase 7: stop swallowing HTTP failures) ──

type private ThrowingHttpMessageHandler() =
  inherit HttpMessageHandler()
  override _.SendAsync(_, _) =
    raise (System.Net.Http.HttpRequestException "Connection refused")

[<Fact>]
let ``WatchAllAsync reports failure when the daemon is unreachable`` () =
  task {
    use client = new SageFsClient(new ThrowingHttpMessageHandler())
    let! ok = client.WatchAllAsync("abc12345", CancellationToken.None)
    ok |> should equal false
  }

[<Fact>]
let ``UnwatchAllAsync reports failure when the daemon is unreachable`` () =
  task {
    use client = new SageFsClient(new ThrowingHttpMessageHandler())
    let! ok = client.UnwatchAllAsync("abc12345", CancellationToken.None)
    ok |> should equal false
  }

[<Fact>]
let ``ToggleHotReloadAsync reports failure when the daemon is unreachable`` () =
  task {
    use client = new SageFsClient(new ThrowingHttpMessageHandler())
    let! ok = client.ToggleHotReloadAsync("abc12345", @"C:\Code\SageFs\Game.fs", CancellationToken.None)
    ok |> should equal false
  }

[<Fact>]
let ``DisableLiveTestingAsync fails closed when the daemon is unreachable`` () =
  task {
    // The old code returned true ("still enabled") on exception — a phantom
    // success that told the user live testing was on when the daemon was
    // down. Fail-closed: an unreachable daemon reports NOT enabled.
    use client = new SageFsClient(new ThrowingHttpMessageHandler())
    let! enabled = client.DisableLiveTestingAsync(CancellationToken.None)
    enabled |> should equal false
  }

[<Fact>]
let ``ToggleDirectoryWatchAsync reports failure as null when the daemon is unreachable`` () =
  task {
    use client = new SageFsClient(new ThrowingHttpMessageHandler())
    let! result = client.ToggleDirectoryWatchAsync("abc12345", @"C:\Code\SageFs", CancellationToken.None)
    result |> should equal None
  }
