module SageFs.Args

type Arguments =
  | Version
  | Bare
  | No_Watch
  | No_Resume
  | Prune
  | Persist
  | Sln of fileName: string
  | Proj of filename: string
  | Dir of workingDirectory: string
  | Reference of assemblyFileName: string
  | Load of fileName: string
  | Use of fileName: string
  | Lib of folderList: string list
  | Other of args: string list

/// Parse a raw arg array into Arguments list.
/// SageFs-specific flags (--help, --version, --mcp-port, --supervised)
/// are handled in Program.main before this is called.
let rec parseLoop (args: string list) (acc: Arguments list) =
  match args with
  | [] -> List.rev acc
  | "--bare" :: rest -> parseLoop rest (Bare :: acc)
  | "--no-watch" :: rest -> parseLoop rest (No_Watch :: acc)
  | "--no-resume" :: rest -> parseLoop rest (No_Resume :: acc)
  | "--prune" :: rest -> parseLoop rest (Prune :: acc)
  | "--persist" :: rest -> parseLoop rest (Persist :: acc)
  | "--sln" :: file :: rest -> parseLoop rest (Sln file :: acc)
  | "--proj" :: file :: rest -> parseLoop rest (Proj file :: acc)
  | "--dir" :: dir :: rest -> parseLoop rest (Dir dir :: acc)
  | s :: rest when s.StartsWith("-r:") -> parseLoop rest (Reference (s.Substring(3)) :: acc)
  | "--reference" :: file :: rest -> parseLoop rest (Reference file :: acc)
  | s :: rest when s.StartsWith("--load:") -> parseLoop rest (Load (s.Substring(7)) :: acc)
  | "--load" :: file :: rest -> parseLoop rest (Load file :: acc)
  | s :: rest when s.StartsWith("--use:") -> parseLoop rest (Use (s.Substring(6)) :: acc)
  | "--use" :: file :: rest -> parseLoop rest (Use file :: acc)
  | "--lib" :: rest | "-l" :: rest ->
    let libs = rest |> List.takeWhile (fun a -> not (a.StartsWith("--")))
    let remaining = rest |> List.skip libs.Length
    parseLoop remaining (Lib libs :: acc)
  | "--other" :: rest -> List.rev (Other rest :: acc)
  | _ :: rest -> parseLoop rest acc

let parseArgs (args: string array) =
  parseLoop (Array.toList args) []

