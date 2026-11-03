/// The last green gate's record, which `--ratchets --tighten` reads to lower a budget to what the gate measured.
///
/// A gate that passes keeps what it recorded under `<gate home>/passed/<sha>/`: the trust ledger, the trust report and
/// the other files `scripts/local-gate.fsx` copies. The gate runs in a per-tier clone, so a file written inside the
/// pipeline's own working tree never reaches this checkout; the passed record is where it lands. One home for the path,
/// so the budget tables that read it cannot disagree about where it is.
module SageFs.Tests.GatePassedRecord

open System
open System.IO

/// `SAGEFS_GATE_HOME`, or `~/.local/share/sagefs-gate`.
let gateHome () : string =
  match Environment.GetEnvironmentVariable "SAGEFS_GATE_HOME" with
  | null
  | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".local", "share", "sagefs-gate")
  | path -> path

/// The path of `fileName` in the newest passed record that has it. None on a machine with no gate pass yet, or one
/// whose passes predate the file.
let newestPath (fileName: string) : string option =
  let passed = Path.Combine(gateHome (), "passed")
  match Directory.Exists passed with
  | false -> None
  | true ->
    Directory.GetDirectories passed
    |> Array.map (fun dir -> Path.Combine(dir, fileName))
    |> Array.filter File.Exists
    |> Array.sortByDescending File.GetLastWriteTimeUtc
    |> Array.tryHead

/// The text of `fileName` in the newest passed record that has it.
let newestText (fileName: string) : string option = newestPath fileName |> Option.map File.ReadAllText
