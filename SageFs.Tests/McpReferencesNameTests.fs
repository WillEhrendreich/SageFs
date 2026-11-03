module SageFs.Tests.McpReferencesNameTests

/// Slice 4, step 2 (docs/plans/walk-the-rest-of-the-symbol-ladder.md): `list_references` walks the name before it asks
/// the compiler. A name that stops resolving is answered at once with where it stops and what is listed there, and the
/// compiler is never asked, so a typo does not cost the whole references budget. Default tier, no session.
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.Features.AutoCompletion
open SageFs.McpReferencesPage
open SageFs.Tests.CompletionFixtures

let private tree : Map<string, CompletionEntry list> =
  Map.ofList
    [ "System", entriesOf CompletionKind.Namespace [ "IO"; "Text" ]
      "System.IO", entriesOf CompletionKind.Class [ "Path"; "File"; "Directory" ]
      "System.IO.Path", entriesOf CompletionKind.Method [ "Combine" ] ]

let private membersOf (prefix: string) : Task<CompletionAnswer<CompletionEntry>> =
  task { return exactly (Map.tryFind prefix tree |> Option.defaultValue []) }

/// A compiler that must not be asked: the test fails if the route reaches it.
let private compilerNotAsked (name: string) : Task<ReferenceReply> =
  task { return failwithf "the compiler was asked about %s, but the name does not resolve" name }

let private noUses (_: string) : Task<ReferenceReply> =
  task { return ReferenceReply.Counted(CountedScope.ProjectWide [ "Core" ], [], []) }

let private pageFor (referencesOf: string -> Task<ReferenceReply>) (name: string) : Task<Page> =
  McpReferencesRoute.referencesPage membersOf referencesOf name 100 0

[<Tests>]
let tests =
  testList "McpReferencesRoute name walk" [
    testTask "WHY - list_references on a name that stops resolving answers at once and never asks the compiler" {
      let! page = pageFor compilerNotAsked "System.IO.Pth"
      match page with
      | Page.NameStopsAt("System.IO.Pth", "System.IO", "Pth", nearest) ->
        nearest |> List.tryHead |> Expect.equal "the near name is offered" (Some "Path")
      | other -> failtestf "expected NameStopsAt, got %A" other
    }

    testTask "a name whose first segment lists nothing answers at once and never asks the compiler" {
      let! page = pageFor compilerNotAsked "Sytem.IO.Path"
      page |> Expect.equal "nothing is listed" (Page.NameNotListed("Sytem.IO.Path", "Sytem"))
    }

    testTask "the reply for a name that stops starts with a scope line and prints no count" {
      let! page = pageFor compilerNotAsked "System.IO.Pth"
      let text = render page
      text.Split('\n').[0] |> Expect.equal "the scope comes first" "Scope: unavailable, the name does not resolve past System.IO."
      text |> Expect.stringContains "it names the missing segment" "no member named Pth"
      text |> Expect.stringContains "it offers the near name" "Path"
      text |> Expect.stringContains "it says this is not a zero" "not an answer that it has no references"
    }

    testTask "WHY - a name that resolves and has no uses says none were found and that the name resolves" {
      let! page = pageFor noUses "System.IO.Path"
      let text = render page
      text |> Expect.stringContains "none were found" "none found in the checked projects"
      text |> Expect.stringContains "the name resolves" "The name resolves"
    }

    testTask "a name that resolves is counted by the compiler as before" {
      let uses (_: string) =
        task {
          return
            ReferenceReply.Counted(
              CountedScope.ProjectWide [ "Core" ],
              [ { File = "/s/a.fs"; Line = 3; Column = 0; Kind = ReferenceKind.Use } ],
              [])
        }
      let! page = pageFor uses "System.IO.Path"
      match page with
      | Page.Counted c -> c.Total |> Expect.equal "one use" 1
      | other -> failtestf "expected Counted, got %A" other
    }

    testTask "a list that cannot be fetched still asks the compiler, because absence is not known" {
      let down (_: string) = task { return CompletionAnswer.Unavailable CompletionUnavailable.SessionNotActive }
      let! page = McpReferencesRoute.referencesPage down noUses "System.IO.Path" 100 0
      match page with
      | Page.Counted _ -> ()
      | other -> failtestf "expected Counted, got %A" other
    }
  ]
