// Entry point for the standalone Neovim driver build. When the shared LemDrive project
// carries the `nvim` command, its Program.fs calls `LemDrive.NvimMain.dispatch` with the
// arguments after `nvim` instead, and this file's `main` is dropped.
module LemDrive.NvimMain

let private usage =
  "usage: dotnet LemDrive.dll nvim <command>\n"
  + "  lemming commands: keys | nvim-type | nvim-screen | nvim-wait | nvim-messages | nvim-shell | nvim-shot\n"
  + "  harness commands: serve | oracle | summarize | annotate | daemon-state | tour | tour-check"

/// The arguments after `nvim`. Returns the process exit code.
let dispatch (args: string list) : int =
  match args with
  | "oracle" :: rest -> NvimOracle.run rest
  | "summarize" :: rest -> NvimOracle.summarize rest
  | "annotate" :: rest -> NvimOracle.annotate rest
  | "daemon-state" :: rest ->
    match rest with
    | [ "--run"; dir ] -> NvimOracle.daemonState dir NvimOracle.Daemon.Port
    | _ ->
      eprintfn "usage: daemon-state --run <run-dir>"
      2
  | [] ->
    eprintfn "%s" usage
    2
  | rest -> Nvim.run rest

[<EntryPoint>]
let main argv =
  match List.ofArray argv with
  | "nvim" :: rest -> dispatch rest
  | _ ->
    eprintfn "%s" usage
    2
