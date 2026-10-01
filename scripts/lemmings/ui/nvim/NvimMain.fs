// Entry point for the standalone Neovim driver build. When the shared LemDrive project
// carries the `nvim` command this file is not needed: its Program.fs calls
// `LemDrive.Nvim.run` and `LemDrive.NvimOracle.run` with the arguments after `nvim`.
module LemDrive.NvimMain

[<EntryPoint>]
let main argv =
  match List.ofArray argv with
  | "nvim" :: "oracle" :: rest -> NvimOracle.run rest
  | "nvim" :: "summarize" :: rest -> NvimOracle.summarize rest
  | "nvim" :: rest -> Nvim.run rest
  | _ ->
    eprintfn "usage: dotnet LemDrive.dll nvim <command>   (nvim keys | nvim-type | nvim-screen | nvim-wait | nvim-messages | nvim-shell)"
    2
