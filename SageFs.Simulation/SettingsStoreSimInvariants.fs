namespace SageFs.Simulation

open SageFs
open SageFs.Simulation.SettingsStoreSim

/// Invariants over `SettingsStoreSim`'s trace. Each state is quiescent: every step has finished before the next one
/// starts, so the file has to be exactly what the accepted edits, in schedule order, say it is.
module SettingsStoreSimInvariants =

  type Violation = { Index: int; Why: string }

  /// (a) No lost update. After any step the file holds the last accepted edit of each key. Two tabs saving different
  /// keys at the same moment must both land.
  let noLostUpdate (states: State list) : Violation list =
    states
    |> List.indexed
    |> List.choose (fun (i, s) ->
      match s.File with
      | FileState.Valid layer when layer = s.Expected -> None
      | FileState.Valid layer -> Some { Index = i; Why = sprintf "file holds %A, the accepted edits say %A" layer s.Expected }
      | FileState.Unreadable reason -> Some { Index = i; Why = sprintf "file is unreadable after a step: %s" reason })

  /// (b) A crash in the middle of a write leaves the old file or the new one, never a partial one: after it the file is
  /// still a readable layer and holds what it held before.
  let crashLeavesTheOldFile (states: State list) : Violation list =
    states
    |> List.indexed
    |> List.choose (fun (i, s) ->
      match s.Op, s.File with
      | Some Op.CrashDuringWrite, FileState.Valid layer when layer = s.Expected -> None
      | Some Op.CrashDuringWrite, other -> Some { Index = i; Why = sprintf "after a crash the file is %A, expected the old layer %A" other s.Expected }
      | _ -> None)

  /// (c) A corrupt layer resolves to an error naming the file for every setting, never to a value, and a save over it
  /// repairs the file and keeps the person's bytes beside it.
  let corruptLayerIsNeverAValue (states: State list) : Violation list =
    states
    |> List.indexed
    |> List.collect (fun (i, s) ->
      match s.Corrupt with
      | None -> []
      | Some observed ->
        [ for key, resolution in observed.Resolutions do
            match resolution with
            | Ok p -> yield { Index = i; Why = sprintf "%s resolved to %A from a corrupt file" key p.Effective }
            | Error why ->
              match (ConfigError.describe why).Contains observed.Path with
              | true -> ()
              | false -> yield { Index = i; Why = sprintf "%s: the error does not name %s: %s" key observed.Path (ConfigError.describe why) }
          if not observed.Repaired then yield { Index = i; Why = "a save over a corrupt file left it unreadable" }
          if not observed.BytesKept then yield { Index = i; Why = "a save over a corrupt file lost its bytes: no copy was kept" } ])

  /// (d) After a restart plus `applyAtStart`, every Live setting that a layer set is in use at the saved value.
  let restartAppliesWhatWasSaved (states: State list) : Violation list =
    states
    |> List.indexed
    |> List.choose (fun (i, s) ->
      match s.RestartMismatches with
      | Some (_ :: _ as keys) -> Some { Index = i; Why = sprintf "after a restart these Live settings were not in use at their saved value: %A" keys }
      | Some [] | None -> None)
