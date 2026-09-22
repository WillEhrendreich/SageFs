module SageFs.Tests.SessionDisplayStatusUnificationTests

open Expecto
open Expecto.Flip
open Microsoft.FSharp.Reflection

/// Roast-4 #6: the sidebar and the MCP/TUI event stream each grew their own
/// `SessionDisplayStatus` DU (SessionDisplay.fs: Errored/Suspended/Stale/
/// Restarting; DashboardTypes.fs: Faulted/Lost/Stopped). One name, two
/// vocabularies for "what the user sees" — a status can be Errored on one
/// surface and Faulted on another for the same session. These pin the
/// unified shape.
[<Tests>]
let sessionDisplayStatusUnificationTests =
  let assembly = typeof<SageFs.SessionDisplayStatus>.Assembly
  let named =
    assembly.GetTypes()
    |> Array.filter (fun t -> t.Name = "SessionDisplayStatus")
  testList "SessionDisplayStatus unification" [
    testCase "WHY — exactly one SessionDisplayStatus type exists because two DUs with one name let the sidebar and the event stream disagree about the same session" <| fun _ ->
      named
      |> Array.map (fun t -> t.FullName)
      // A type declared directly in `namespace SageFs` has a dotted FullName;
      // "+" would mean it was nested in a module, which it must not be.
      |> Expect.equal "one definition, in SageFs.SessionDisplayStatus" [| "SageFs.SessionDisplayStatus" |]

    testCase "WHY — the unified DU's Faulted case carries the reason because both former shapes needed it: Errored had one, Faulted had none, and a card must say why" <| fun _ ->
      let cases = FSharpType.GetUnionCases(typeof<SageFs.SessionDisplayStatus>)
      let faulted = cases |> Array.tryFind (fun c -> c.Name = "Faulted")
      match faulted with
      | None -> failtestf "no Faulted case; cases are %A" (cases |> Array.map (fun c -> c.Name))
      | Some c ->
        c.GetFields()
        |> Array.map (fun f -> f.Name, f.PropertyType)
        |> Expect.equal "Faulted of reason: string" [| "reason", typeof<string> |]

    testCase "WHY — the unified DU covers every state either surface rendered so nothing is silently collapsed" <| fun _ ->
      let names =
        FSharpType.GetUnionCases(typeof<SageFs.SessionDisplayStatus>)
        |> Array.map (fun c -> c.Name)
        |> Set.ofArray
      let required = set [ "Running"; "Starting"; "Restarting"; "Faulted"; "Lost"; "Stopped"; "Idle" ]
      Set.difference required names
      |> Expect.isEmpty "missing cases"
  ]
