/// What the harness can and cannot promise about a run: whether the stdio bridge and the shared
/// daemon are the same build, the limits of the sandbox that no run can remove, and a tripwire
/// for the one wall the sandbox does not have (the daemon runs the lemming's code outside it).
/// Pure: no files, no network.
module LemScore.Provenance

open System
open System.Text.RegularExpressions
open LemScore.Types
open LemScore.CmdcStream

// ---- bridge vs daemon -------------------------------------------------------------------

/// Do the lemming's stdio bridge and the shared daemon come from the same commit?
type VersionMatch =
  | SameBuild
  | SkewedBuild
  | UnknownBuild

module VersionMatch =
  let toString (m: VersionMatch) : string =
    match m with
    | SameBuild -> "same"
    | SkewedBuild -> "skewed"
    | UnknownBuild -> "unknown"

  let tryParse (text: string) : Result<VersionMatch, string> =
    match text with
    | "same" -> Ok SameBuild
    | "skewed" -> Ok SkewedBuild
    | "unknown" -> Ok UnknownBuild
    | other -> Error (sprintf "'%s' is not one of same, skewed, unknown" other)

let private versionToken = Regex(@"\d+\.\d+\.\d+[0-9A-Za-z.+\-]*", RegexOptions.Compiled)
let private commitOf = Regex(@"\+([0-9a-fA-F]{7,40})", RegexOptions.Compiled)

/// The first version-looking token in free text ("SageFs 0.6.875+72b286a9" gives "0.6.875+72b286a9").
let versionOf (text: string) : string option =
  match versionToken.Match text with
  | m when m.Success -> Some m.Value
  | _ -> None

/// Two builds are the same when their commit hashes agree (one may be a prefix of the other,
/// since a version can carry a short hash), or, with no hash on either side, when the versions
/// are equal. A hash on one side only proves nothing, so it is Unknown.
let compareBuilds (daemon: string) (bridge: string) : VersionMatch =
  match versionOf daemon, versionOf bridge with
  | Some d, Some b ->
    match commitOf.Match d, commitOf.Match b with
    | dm, bm when dm.Success && bm.Success ->
      let dh, bh = dm.Groups.[1].Value.ToLowerInvariant(), bm.Groups.[1].Value.ToLowerInvariant()
      match dh.StartsWith bh || bh.StartsWith dh with
      | true -> SameBuild
      | false -> SkewedBuild
    | dm, bm when not dm.Success && not bm.Success -> if d = b then SameBuild else SkewedBuild
    | _ -> UnknownBuild
  | _ -> UnknownBuild

let skewFellOver (daemon: string) (bridge: string) : FellOver list =
  match compareBuilds daemon bridge with
  | SkewedBuild ->
    [ { Stage = Preflight
        Symptom = "the lemming's bridge and the shared daemon are different builds, so a protocol or tool-surface difference may be the skew, not the model or SageFs"
        Evidence = sprintf "daemon %s; bridge %s" daemon bridge } ]
  | SameBuild | UnknownBuild -> []

// ---- what the sandbox does not wall off -------------------------------------------------

/// Listed in every summary.json, so no verdict is read without them.
let knownLimits (harness: Harness) : string list =
  let always =
    [ "Code the lemming sends to SageFs (send_fsharp_code, check_fsharp_code, hot reload, builds started by create_project_session) runs in the shared daemon's FSI host and build processes, which are NOT inside the sandbox and have the daemon owner's full file access. Through an eval a lemming can read or write anything that user can, and can start a session anywhere. Only the lemming's own processes are walled."
      "The daemon is shared. Its sessions list shows other agents' sessions and project paths to the lemming, and stop_session, switch_session and release_work_lease can reach sessions that are not the lemming's. Cleanup stops only sessions under the run directory, so a session the lemming started elsewhere is not stopped (the Isolation findings list the ones the harness noticed)."
      "The Command Code credential (~/.commandcode/auth.json) is bound read-only into the sandbox because cmdc needs it, the network is open, and the model runs with --yolo, so the model's shell can read and send it."
      "The sandbox stops the lemming's processes from seeing or signalling anything on the host, and from writing outside the run directory. It does not stop what the daemon does for it, and it cannot hide a file the daemon reads for it." ]
  match harness with
  | Cmdc -> always
  | CmdcNvim | CmdcVscode ->
    always @ [ "The editor driver is not a boundary: its verbs are a closed set, but keys, typed text and the command palette can still reach any other editor command, including a terminal. The sandbox is the boundary." ]

type private PathHit = { Turn: int; Tool: string; Paths: string list }

let private systemPath =
  Regex(@"(?<![\w./~-])(?:~|/(?:home|root|etc|var|mnt|opt|run|proc|sys|srv|media))(?:/[^\s""'\;,)\]}`]*)?", RegexOptions.Compiled)

let private pathTools = set [ "send_fsharp_code"; "check_fsharp_code"; "create_project_session"; "create_solution_session"; "create_bare_session" ]

let private under (dir: string) (path: string) : bool =
  let root = dir.TrimEnd('/')
  path = root || path.StartsWith(root + "/", StringComparison.Ordinal)

/// Absolute paths in what a lemming sent to a daemon tool that run outside the sandbox. A tripwire
/// read off the stream, not a wall: code can build a path at runtime, and a path in a comment
/// counts. It exists so a lemming that reached outside its run directory is noticed, not so the
/// daemon is protected.
let outsideRunDir (runDir: string) (calls: ToolCall list) : FellOver list =
  let hits =
    calls
    |> List.filter (fun c -> isSagefsCall c && pathTools.Contains(sagefsToolName c))
    |> List.choose (fun c ->
      let paths =
        systemPath.Matches c.Input
        |> Seq.map _.Value
        |> Seq.filter (fun p -> not (under runDir p))
        |> Seq.distinct
        |> List.ofSeq
      match paths with
      | [] -> None
      | ps -> Some { Turn = c.Turn; Tool = sagefsToolName c; Paths = ps })
  hits
  |> List.map (fun h ->
    { Stage = Isolation
      Symptom = sprintf "%s named a path outside the run directory, and the daemon runs it outside the sandbox (a tripwire, not proof it was read or written)" h.Tool
      Evidence = sprintf "turn %d: %s" h.Turn (h.Paths |> List.truncate 4 |> String.concat ", ") })
