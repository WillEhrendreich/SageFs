module LemDrive.Program

open System
open LemDrive.Calls

let private usage =
  "usage: LemDrive <editor> <command> ...\n\
  \  vsc      drive VS Code (try: LemDrive vsc snapshot)\n\
  \  oracle   LemDrive oracle <task> --run-dir D [--port N]   the harness's own check of a finished run"

/// `--flag value` pairs.
let private flags (args: string list) : Map<string, string> =
  args
  |> List.chunkBySize 2
  |> List.choose (function [ k; v ] when k.StartsWith "--" -> Some(k.Substring 2, v) | _ -> None)
  |> Map.ofList

let private oracle (args: string list) : Outcome =
  match args with
  | taskName :: rest ->
    let m = flags rest
    match Oracle.LemTask.tryParse taskName, Map.tryFind "run-dir" m with
    | Result.Error e, _ -> BadUsage e
    | _, None -> BadUsage "oracle needs --run-dir D"
    | Ok task, Some runDir ->
      let port = Map.tryFind "port" m |> Option.bind (fun p -> match Int32.TryParse p with | true, n -> Some n | _ -> None) |> Option.defaultValue Oracle.DefaultDaemonPort
      match (Oracle.run task runDir port).GetAwaiter().GetResult() with
      | 0 -> Output "oracle: every check passed"
      | _ -> DriveFailed "oracle: at least one check failed"
  | [] -> BadUsage usage

let private fellOver (args: string list) : Outcome =
  let m = flags args
  match Map.tryFind "run-dir" m with
  | None -> BadUsage "fellover needs --run-dir D [--expect-session true|false] [--sessions N]"
  | Some runDir ->
    let expect = Map.tryFind "expect-session" m <> Some "false"
    let sessions = Map.tryFind "sessions" m |> Option.bind (fun s -> match Int32.TryParse s with | true, n -> Some n | _ -> None) |> Option.defaultValue 0
    Output(sprintf "fellover: %d driver call(s) read" (FellOver.write runDir expect sessions))

/// One case per editor the driver speaks to. The Neovim commands add their own
/// case here, and their file (Nvim.fs) goes before this one in the fsproj.
let private run (argv: string list) : Outcome =
  match argv with
  | "vsc" :: rest -> (Vsc.cli rest).GetAwaiter().GetResult()
  | "oracle" :: rest -> oracle rest
  | "fellover" :: rest -> fellOver rest
  | "host" :: rest -> VscHost.cli rest
  | _ -> BadUsage usage

/// `LemDrive shim sagefs <args>` is the `sagefs` on PATH inside the VS Code sandbox. It
/// speaks to the real build, or refuses, and its exit code is the call's exit code.
let private shim (argv: string list) : int option =
  match argv with
  | "shim" :: "sagefs" :: rest -> Some(Shim.run rest)
  | _ -> None

[<EntryPoint>]
let main argv =
  match shim (List.ofArray argv) with
  | Some code -> code
  | None ->
    let outcome = run (List.ofArray argv)
    printfn "%s" (Outcome.text outcome)
    Outcome.exitCode outcome
