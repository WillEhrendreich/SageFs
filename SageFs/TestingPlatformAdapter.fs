namespace SageFs.TestingPlatform

open System
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
type Capability =
  | Available
  | Unavailable of missing: string list

type Surface = {
  BaseArguments: string list
  Capability: Capability
}

[<RequireQualifiedAccess>]
type Termination =
  | Completed of exitCode: int
  | TimedOut of after: TimeSpan
  | Cancelled of reason: string
  | TransportFailed of message: string

type ProcessResult = {
  ExitCode: int
  StandardOutput: string
  StandardError: string
}

module Adapter =
  let private requiredOptions =
    [ "--list-tests"; "--filter-uid"; "--treenode-filter"; "--output"; "--no-banner" ]

  let private optionPattern = Regex(@"--[A-Za-z0-9-]+", RegexOptions.Compiled)

  let private optionNames (help: string) : Set<string> =
    optionPattern.Matches(help)
    |> Seq.map (fun token -> token.Value)
    |> Set.ofSeq

  let probeHelp (help: string) : Capability =
    let names = optionNames help
    let missing = requiredOptions |> List.filter (fun option -> not (Set.contains option names))
    match missing with
    | [] -> Capability.Available
    | missing -> Capability.Unavailable missing

  let surface (baseArguments: string list) (help: string) = {
    BaseArguments = baseArguments
    Capability = probeHelp help
  }

  let commonArguments = [ "--no-banner"; "--output"; "Detailed" ]

  let listArguments (surface: Surface) =
    surface.BaseArguments @ commonArguments @ [ "--list-tests" ]

  let runArguments (surface: Surface) (testNodeUids: string list) =
    match surface.Capability, testNodeUids with
    | Capability.Unavailable missing, _ ->
      Error(sprintf "Microsoft Testing Platform is missing: %s" (String.concat ", " missing))
    | Capability.Available, [] -> Error "Microsoft Testing Platform run requires at least one test node UID"
    | Capability.Available, uids ->
      Ok(surface.BaseArguments @ commonArguments @ [ "--filter-uid" ] @ uids)

  let terminationOf (result: ProcessResult) : Termination =
    match result.ExitCode with
    | 0 -> Termination.Completed 0
    | exitCode -> Termination.TransportFailed(sprintf "test process exited with %d: %s" exitCode (result.StandardError.Trim()))
