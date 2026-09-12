/// `sagefs-demos` CLI entry point (demo-gif-plan.md §8): `doctor | list |
/// storyboard | record | check | measure | compose`. Standalone tool — this
/// project never references SageFs.Core/SageFs; it drives the daemon over
/// HTTP and Playwright at runtime instead (the host-closure lesson, §4/roast
/// §1). Wave-2 work wires each verb to the planners in this project; today
/// every verb just reports it is not implemented yet.
module SageFs.Demos.Program

[<RequireQualifiedAccess>]
type Verb =
  | Doctor
  | List
  | Storyboard
  | Record
  | Check
  | Measure
  | Compose
  | Unknown of string

let private parseVerb (arg: string) : Verb =
  match arg with
  | "doctor" -> Verb.Doctor
  | "list" -> Verb.List
  | "storyboard" -> Verb.Storyboard
  | "record" -> Verb.Record
  | "check" -> Verb.Check
  | "measure" -> Verb.Measure
  | "compose" -> Verb.Compose
  | other -> Verb.Unknown other

let private notImplemented (name: string) =
  printfn "%s: not implemented" name
  0

[<EntryPoint>]
let main argv =
  match argv |> Array.tryHead |> Option.map parseVerb with
  | Some Verb.Doctor -> notImplemented "doctor"
  | Some Verb.List -> notImplemented "list"
  | Some Verb.Storyboard -> notImplemented "storyboard"
  | Some Verb.Record -> notImplemented "record"
  | Some Verb.Check -> notImplemented "check"
  | Some Verb.Measure -> notImplemented "measure"
  | Some Verb.Compose -> notImplemented "compose"
  | Some (Verb.Unknown other) ->
    eprintfn "sagefs-demos: unknown verb '%s'" other
    eprintfn "usage: sagefs-demos <doctor|list|storyboard|record|check|measure|compose> [args]"
    1
  | None ->
    eprintfn "usage: sagefs-demos <doctor|list|storyboard|record|check|measure|compose> [args]"
    1
