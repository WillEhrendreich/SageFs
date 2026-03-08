module SageFs.Tests.DomainModelSseTests

open System.Text.Json
open System.Text.Json.Serialization
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.DomainModelViz

let private opts =
  let o = JsonSerializerOptions()
  o.Converters.Add(JsonFSharpConverter())
  o

let private extractSseData (sseEvent: string) =
  sseEvent.Split('\n')
  |> Array.tryFind (fun l -> l.StartsWith("data: "))
  |> Option.map (fun l -> l.Substring(6))

[<Tests>]
let domainModelSsePropertyTests = testList "formatDomainModelEvent properties" [
  testProperty "all annotations appear in output" (fun (count: PositiveInt) ->
    let n = min count.Get 20
    let annots : AnnotatedTransition list =
      [ for i in 0 .. n - 1 ->
          { FromState = sprintf "S%d" i
            ToState = sprintf "S%d" ((i + 1) % max n 1)
            FunctionName = Some (sprintf "f%d" i)
            IsErrorBranch = false
            Health = TransitionHealth.Passing } ]
    let result = SageFs.SseWriter.formatDomainModelEvent opts None annots
    let data = extractSseData result |> Option.get
    let doc = JsonDocument.Parse(data)
    doc.RootElement.GetProperty("Transitions").GetArrayLength() = n
  )

  testProperty "SessionId injection is idempotent" (fun (sid: NonEmptyString) ->
    let annots : AnnotatedTransition list =
      [ { FromState = "A"; ToState = "B"; FunctionName = Some "f"
          IsErrorBranch = false; Health = TransitionHealth.Passing } ]
    let result = SageFs.SseWriter.formatDomainModelEvent opts (Some sid.Get) annots
    let data = extractSseData result |> Option.get
    let count = data.Split("SessionId") |> Array.length
    count = 2
  )

  testProperty "None FunctionName serializes as null" (fun (n: PositiveInt) ->
    let annots : AnnotatedTransition list =
      [ for i in 0 .. min n.Get 10 - 1 ->
          { FromState = sprintf "S%d" i
            ToState = sprintf "S%d" (i + 1)
            FunctionName = None
            IsErrorBranch = false
            Health = TransitionHealth.NotImplemented } ]
    let result = SageFs.SseWriter.formatDomainModelEvent opts None annots
    let data = extractSseData result |> Option.get
    let doc = JsonDocument.Parse(data)
    let ts = doc.RootElement.GetProperty("Transitions")
    ts.EnumerateArray() |> Seq.forall (fun t ->
      t.GetProperty("FunctionName").ValueKind = JsonValueKind.Null)
  )
]
