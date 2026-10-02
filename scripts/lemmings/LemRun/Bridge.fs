/// The SageFs the lemming gets: the `sagefs mcp` stdio bridge in its MCP registration, which attaches
/// to the shared daemon (the person running the harness owns that daemon, and the harness never
/// starts or stops it).
///
///   SAGEFS_LEMMING_BIN unset      <main checkout>/SageFs/bin/Release/net11.0/SageFs.dll
///   SAGEFS_LEMMING_BIN=published  the global tool `sagefs`, a baseline
///   SAGEFS_LEMMING_BIN=<path>     a SageFs.dll, or a directory holding one
///
/// A dev build is copied ONCE into the store, keyed by its version (which names the commit) and
/// the hash of its dll, and mounted read-only into every sandbox that uses it. Not into each run:
/// that was hundreds of MB of tmpfs per run, never removed. A rebuild is a new key, so a trial in
/// progress still cannot change.
module LemRun.Bridge

open System
open System.Diagnostics
open System.IO
open LemRun.Failure
open LemScore.Provenance

/// Which SageFs the bridge is.
type Source =
  | Published
  | DevBuild of dll: string

let private defaultDll () = Path.Combine(Env.mainCheckout (), "SageFs", "bin", "Release", "net11.0", "SageFs.dll")

let sourceFromEnv () : Source =
  match Env.var "SAGEFS_LEMMING_BIN" with
  | Some "published" -> Published
  | None -> DevBuild (defaultDll ())
  | Some p when Directory.Exists p -> DevBuild (Path.Combine(p, "SageFs.dll"))
  | Some p -> DevBuild p

/// How a run uses SageFs.
type Bridge =
  { /// The command and arguments the MCP registration runs: `dotnet <stored dll> mcp`, or `sagefs mcp`.
    Command: string list
    Description: string
    /// The bridge's own version, which may name a commit.
    Version: string
    /// The stored build the sandbox mounts read-only, when there is one.
    Mount: string option
    /// `daemon X; bridge <description>`, the line summary.json carries.
    SagefsVersion: string }

/// No bridge at all (an editor lemming has no MCP server): nothing to copy, nothing to compare.
let none (daemonVersion: string) : Bridge =
  { Command = []; Description = ""; Version = "unknown"; Mount = None; SagefsVersion = sprintf "daemon %s" daemonVersion }

/// The product version of a built dll, or "unknown".
let dllVersion (dll: string) : string =
  match FileVersionInfo.GetVersionInfo(dll).ProductVersion with
  | null | "" -> "unknown"
  | v -> v

/// Says it out loud when the bridge and the daemon are different commits: a protocol or tool-surface
/// difference would otherwise be blamed on the model or on SageFs. Refused when
/// LEM_REQUIRE_SAME_VERSION is set, a finding in summary.json otherwise.
let private checkSkew (daemonVersion: string) (bridgeVersion: string) : unit =
  match compareBuilds daemonVersion bridgeVersion with
  | SkewedBuild ->
    let bridge = versionOf bridgeVersion |> Option.defaultValue "unknown"
    say (sprintf "VERSION SKEW: the lemming's bridge is %s but the shared daemon is %s." bridge daemonVersion)
    say "a difference in protocol or tools may be the skew, not the model. Rebuild master and restart the daemon, or point SAGEFS_LEMMING_BIN at the build the daemon runs."
    match Env.isSet "LEM_REQUIRE_SAME_VERSION" with
    | true -> fail (ToolchainMissing "refusing (LEM_REQUIRE_SAME_VERSION is set)")
    | false -> ()
  | SameBuild | UnknownBuild -> ()

/// Resolves the bridge, judges the skew BEFORE anything is copied (so a refusal leaves nothing
/// behind), then makes sure the build is in the store.
let prepare (source: Source) (daemonVersion: string) : Bridge =
  match source with
  | Published ->
    let first = (Proc.run (Proc.spec "sagefs" [ "--version" ]) None).Stdout.Split('\n') |> Array.tryHead |> Option.defaultValue "" |> _.Trim()
    let version = if first = "" then "unknown" else first
    let description = sprintf "published tool: %s" version
    checkSkew daemonVersion version
    { Command = [ "sagefs"; "mcp" ]; Description = description; Version = (versionOf version |> Option.defaultValue "unknown")
      Mount = None; SagefsVersion = sprintf "daemon %s; bridge %s" daemonVersion description }
  | DevBuild dll ->
    match File.Exists dll with
    | false -> fail (ToolchainMissing (sprintf "no SageFs.dll at %s (build master, or set SAGEFS_LEMMING_BIN)" dll))
    | true ->
      let version = dllVersion dll
      checkSkew daemonVersion version
      let key = Store.sanitize version + "-" + Store.hashFile dll 8
      let sourceDir = Path.GetDirectoryName dll |> Option.ofObj |> Option.defaultValue "."
      match Store.ensure Env.storeRoot Store.Bridge key (fun dir -> Store.copyTree sourceDir dir) with
      | Error e -> fail (ToolchainMissing e)
      | Ok stored ->
        Store.collect Env.storeRoot Store.Bridge |> List.iter (fun gone -> say (sprintf "removed an unused stored bridge: %s" gone))
        let description = sprintf "dev build: %s" version
        { Command = [ "dotnet"; Path.Combine(stored, Path.GetFileName dll); "mcp" ]
          Description = description
          Version = (versionOf version |> Option.defaultValue "unknown")
          Mount = Some stored
          SagefsVersion = sprintf "daemon %s; bridge %s" daemonVersion description }
