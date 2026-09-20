/// Regression guard for one of the two remaining session-status vocabularies
/// this island investigated (sagefs-ux-roast.md §2 "three parallel state
/// models that can disagree" / "`Unchecked.defaultof` tombstones and `isNull`
/// checks to represent a 'faulted' session"). That specific defect is
/// already fixed in the current tree: `AppState.SessionPhase.Faulted` carries
/// only `reason: string` (`AppState.fs`'s own doc comment — "Replaces the old
/// (AppState option × SessionState) pair which could desync" — and
/// `SessionState` is now a pure, total projection off `SessionPhase`, not a
/// second mutable model). This test exists so a REGRESSION back to a null
/// tombstone fails loudly here rather than being re-discovered by a future
/// audit — a deliberately broken variant (reintroducing either literal into
/// `AppState.fs`) fails this test, proving it has teeth.
module SageFs.Tests.SessionPhaseNoNullTombstoneTests

open System.IO
open Expecto
open Expecto.Flip

[<Tests>]
let noNullTombstoneTests =
  let appStatePath = Path.Combine(__SOURCE_DIRECTORY__, "..", "SageFs.Core", "AppState.fs")
  // Code lines only — a doc comment is allowed to NAME the old pattern while
  // explaining the fix (AppState.fs does exactly this, at the Faulted-phase
  // publish site: "This replaces the old tombstone that held
  // Unchecked.defaultof Session/OutStream"), and that history is worth
  // keeping. Only an ACTUAL `Unchecked.defaultof<...>` construction — which a
  // bare mention in prose never spells, since it always needs the generic
  // type argument — would resurrect the bug this test guards against.
  let codeLines =
    File.ReadAllLines appStatePath
    |> Array.filter (fun l -> not (l.TrimStart().StartsWith "//"))
  testList "AppState.fs — SessionPhase carries no null tombstone" [
    testCase "WHY — a Faulted session must be a reason-carrying DU case, never a live AppState/OutStream pair defaulted to null (roast §2's 'impossible states still representable somewhere')" <| fun _ ->
      let hits = codeLines |> Array.filter (fun l -> l.Contains "Unchecked.defaultof<")
      hits
      |> Array.length
      |> Expect.equal
        (sprintf "no Unchecked.defaultof<...> construction in AppState.fs's code; found: %A" hits)
        0

    testCase "WHY — no code path should need an isNull(box ...) guard to avoid touching a tombstoned session/output stream" <| fun _ ->
      let hits = codeLines |> Array.filter (fun l -> l.Contains "isNull (box")
      hits
      |> Array.length
      |> Expect.equal
        (sprintf "no isNull (box ...) guards in AppState.fs's code; found: %A" hits)
        0
  ]
