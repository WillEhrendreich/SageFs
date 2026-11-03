module SageFs.Tests.McpNamePathTests

/// Slice 4 of the progressive map (docs/plans/walk-the-rest-of-the-symbol-ladder.md): where a qualified name stops
/// resolving, and the names the compiler does list there. The walk takes its member lister as a function, so these
/// run with no session. Default tier.
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.AutoCompletion
open SageFs.McpNamePath
open SageFs.Tests.CompletionFixtures

/// A member lister over a fixed tree: the names listed under each prefix. A prefix absent from the tree lists nothing.
let private listerOver (tree: Map<string, CompletionEntry list>) : string -> Task<CompletionAnswer<CompletionEntry>> =
  fun prefix ->
    task {
      match Map.tryFind prefix tree with
      | Some entries -> return exactly entries
      | None -> return exactly []
    }

let private systemTree : Map<string, CompletionEntry list> =
  Map.ofList
    [ "System", entriesOf CompletionKind.Namespace [ "IO"; "Collections"; "Text" ] @ entriesOf CompletionKind.Class [ "String" ]
      "System.IO", entriesOf CompletionKind.Class [ "Path"; "File"; "Directory" ]
      "System.IO.Path", entriesOf CompletionKind.Method [ "Combine"; "GetFileName" ] ]

let private resolveIn (tree: Map<string, CompletionEntry list>) (name: string) : Task<NamePathResolution> =
  resolve SageFsConfig.NearestNamesMax (listerOver tree) name

[<Tests>]
let tests =
  testList "McpNamePath" [
    testTask "WHY - a name whose parent lists everything but the last segment stops at the parent and names the nearest candidates" {
      match! resolveIn systemTree "System.IO.Pth" with
      | NamePathResolution.StopsAt(prefix, missing, nearest) ->
        prefix |> Expect.equal "the longest prefix that lists" "System.IO"
        missing |> Expect.equal "the segment that is not listed" "Pth"
        nearest |> List.tryHead |> Expect.equal "Path is nearest" (Some "Path")
      | other -> failtestf "expected StopsAt, got %A" other
    }

    testTask "a name that resolves reports the kind its parent gives it" {
      let! resolution = resolveIn systemTree "System.IO.Path"
      resolution |> Expect.equal "Path is a class under System.IO" (NamePathResolution.Resolved CompletionKind.Class)
    }

    testTask "a single segment that lists members resolves with no parent to name its kind" {
      let! resolution = resolveIn systemTree "System"
      resolution |> Expect.equal "System lists members" NamePathResolution.ResolvedRoot
    }

    testTask "a first segment that lists nothing is named, and nothing resolves" {
      let! resolution = resolveIn systemTree "Sytem.IO.Path"
      resolution |> Expect.equal "nothing resolves" (NamePathResolution.NothingResolves "Sytem")
    }

    testTask "a path that runs past a leaf stops at the leaf and offers nothing nearer" {
      let! resolution = resolveIn systemTree "System.IO.Path.Combine.Extra"
      resolution
      |> Expect.equal "Combine is a method with no members" (NamePathResolution.StopsAt("System.IO.Path.Combine", "Extra", []))
    }

    testTask "a segment that resembles nothing listed names no candidates" {
      match! resolveIn systemTree "System.IO.Zzzzqqqq" with
      | NamePathResolution.StopsAt(_, _, nearest) -> nearest |> Expect.isEmpty "noise is not offered as a near name"
      | other -> failtestf "expected StopsAt, got %A" other
    }

    testTask "a list cut at the wire ceiling does not claim the name is absent" {
      let cut _ =
        task { return CompletionAnswer.Answered(entriesOf CompletionKind.Class [ "Path" ], CompletionTotal.AtLeast 2000) }
      match! resolve SageFsConfig.NearestNamesMax cut "System.Zed" with
      | NamePathResolution.StopsAtCutList("System", "Zed", _) -> ()
      | other -> failtestf "expected StopsAtCutList, got %A" other
    }

    testTask "a lister that cannot answer makes the resolution unknown, not absent" {
      let down _ = task { return CompletionAnswer.Unavailable CompletionUnavailable.SessionNotActive }
      match! resolve SageFsConfig.NearestNamesMax down "System.IO" with
      | NamePathResolution.Unknown("System", CompletionUnavailable.SessionNotActive) -> ()
      | other -> failtestf "expected Unknown, got %A" other
    }
  ]
