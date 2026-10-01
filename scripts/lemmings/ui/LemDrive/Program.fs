module LemDrive.Program

open LemDrive.Calls

let private usage =
  "usage: LemDrive <editor> <command> ...\n  vsc      drive VS Code (try: LemDrive vsc)"

/// One case per editor the driver speaks to. The Neovim commands add their own
/// case here, and their file (Nvim.fs) goes before this one in the fsproj.
let private run (argv: string list) : Outcome =
  match argv with
  | "vsc" :: rest ->
    match VscCommand.parse rest with
    | Ok c -> Output(sprintf "parsed %s" (VscCommand.verb c))
    | Result.Error e -> BadUsage e
  | _ -> BadUsage usage

[<EntryPoint>]
let main argv =
  let outcome = run (List.ofArray argv)
  printfn "%s" (Outcome.text outcome)
  Outcome.exitCode outcome
