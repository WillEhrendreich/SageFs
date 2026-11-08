module SageFs.Tests.ExceptionChainTests

open System
open System.IO
open System.Net.Http
open System.Net.Sockets
open Expecto
open Expecto.Flip
open FsCheck
open SageFs

/// A failure the way a worker call produced it in the release gate: an AggregateException around an HttpRequestException
/// around the IO error that says why.
let private resetWhileSending : exn =
  AggregateException(
    HttpRequestException("An error occurred while sending the request.", IOException("The response ended prematurely.", SocketException(104))))

[<Tests>]
let tests =
  testList "ExceptionChain" [
    testCase "WHY - the message of a wrapped HTTP failure names the cause, which exn.Message leaves out" <| fun _ ->
      let said = ExceptionChain.describe resetWhileSending
      said |> Expect.stringContains "names the wrapper's own words" "An error occurred while sending the request."
      said |> Expect.stringContains "names what the connection did" "The response ended prematurely."
      said |> Expect.stringContains "names the socket's own error" "Connection reset by peer"
      resetWhileSending.Message.Contains "Connection reset" |> Expect.isFalse "the plain Message did not"

    testCase "the chain reads from the outside in, so the cause is last" <| fun _ ->
      ExceptionChain.causes resetWhileSending
      |> List.map (fun cause -> cause.GetType().Name)
      |> Expect.equal "outermost first, the aggregate replaced by what it holds" [ "HttpRequestException"; "IOException"; "SocketException" ]

    testCase "an AggregateException with several failures lists every one" <| fun _ ->
      let many = AggregateException(InvalidOperationException "first", ArgumentException "second")
      let said = ExceptionChain.describe many
      said |> Expect.stringContains "the first" "first"
      said |> Expect.stringContains "the second" "second"

    testCase "an AggregateException with nothing inside is itself" <| fun _ ->
      ExceptionChain.causes (AggregateException()) |> List.length |> Expect.equal "one entry" 1

    testCase "an exception with no inner exception is its own type and message" <| fun _ ->
      ExceptionChain.describe (InvalidOperationException "plain")
      |> Expect.equal "just the one" "InvalidOperationException: plain"

    testProperty "every message in a chain of any depth is in the description, outermost first" <| fun (depthSeed: byte) ->
      let depth = 1 + int depthSeed % 8
      // Distinct messages that are no part of one another or of a type name, so a position says which one it found.
      let texts = [ for level in 1 .. depth -> sprintf "level-%d-says-ZZ" level ]
      let chain =
        List.foldBack
          (fun (text: string) (inner: exn option) ->
            match inner with
            | None -> Some (InvalidOperationException text :> exn)
            | Some cause -> Some (InvalidOperationException(text, cause) :> exn))
          texts
          None
      let said = ExceptionChain.describe chain.Value
      let positions = texts |> List.map (fun text -> said.IndexOf(text, StringComparison.Ordinal))
      (positions |> List.forall (fun p -> p >= 0)) && positions = List.sort positions
  ]
