module SageFs.VisualStudio.Core.Tests.CompletionRequestTests

open System.Text.Json
open Xunit
open FsUnit.Xunit
open SageFs.VisualStudio.Core

let private parse (json: string) = JsonDocument.Parse(json).RootElement

[<Fact>]
let ``CompletionRequest create keeps working directory when present`` () =
  let request = CompletionRequest.create "System.Con" 10 @"C:\Code\Repos\SageFs"

  request.IsSessionAware |> should equal true
  request.WorkingDirectory |> should equal (Some @"C:\Code\Repos\SageFs")

[<Fact>]
let ``CompletionRequest create omits session awareness when working directory is null`` () =
  let request = CompletionRequest.create "System.Con" 10 null

  request.IsSessionAware |> should equal false
  request.WorkingDirectory |> should equal None

[<Theory>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``CompletionRequest create omits session awareness when working directory is blank`` (workingDirectory: string) =
  let request = CompletionRequest.create "System.Con" 10 workingDirectory

  request.IsSessionAware |> should equal false
  request.WorkingDirectory |> should equal None

[<Fact>]
let ``CompletionRequest toJson includes working_directory when session aware`` () =
  let json =
    CompletionRequest.create "System.Con" 10 @"C:\Code\Repos\SageFs"
    |> CompletionRequest.toJson

  let root = parse json
  root.GetProperty("code").GetString() |> should equal "System.Con"
  root.GetProperty("cursor_position").GetInt32() |> should equal 10
  root.GetProperty("working_directory").GetString() |> should equal @"C:\Code\Repos\SageFs"

[<Fact>]
let ``CompletionRequest toJson omits working_directory when session awareness is unavailable`` () =
  let json =
    CompletionRequest.create "System.Con" 10 " "
    |> CompletionRequest.toJson

  let root = parse json
  let mutable workingDirectory = Unchecked.defaultof<JsonElement>

  root.TryGetProperty("working_directory", &workingDirectory) |> should equal false
  root.GetProperty("cursor_position").GetInt32() |> should equal 10
