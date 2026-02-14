module SageFs.Args

open Argu
open System

type Sep = CustomAssignmentOrSpacedAttribute
type Alt = AltCommandLineAttribute

//todo check if we can have 2 subtypes, one for cli another for nrepl
//maybe make Other in SageFs.Nrepl?
//and add transport options, like websockets/tcp/tty, with provided ports and other configuration
type Arguments =
  | [<Unique; AltCommandLine("-v")>] Version
  | [<Unique>] Bare
  | [<Unique>] No_Watch
  | Sln of fileName: string
  | Proj of filename: string
  | [<Unique>] Dir of workingDirectory: string
  | [<Alt("-r"); Sep(":")>] Reference of assemblyFileName: string
  | [<Sep(":")>] Load of fileName: string
  | [<Sep(":")>] Use of fileName: string
  | [<Alt("-l")>] Lib of folderList: string list
  | [<Last>] Other of args: string list

  interface IArgParserTemplate with
    member s.Usage =
      match s with
      | Version -> "displays the version of SageFs."
      | Bare -> "start a bare FSI session with no project/solution loading — fast startup for quick one-off use."
      | No_Watch -> "disable file watching — no automatic #load on source file changes."
      | Sln _ -> "loads all sources and dependencies for given solution."
      | Proj _ -> "loads all sources and dependencies for given fsproj file."
      | Dir _ -> "specifies alternative working directory to current directory."
      | Reference _ -> "makes code from an F# or .NET Framework assembly available to the code being compiled."
      | Load _ -> "compiles the given source code at startup and loads the compiled F# constructs into the session."
      | Use _ ->
        "tells the interpreter to use the given file on startup as initial input. If it contains prompt configuration, it'd be used for this REPL."
      | Lib _ -> "specifies a directory to be searched for assemblies that are referenced."
      | Other _ -> "Any other arguments which will be passed to fsi.exe"

let errorHandler =
  ProcessExiter(
    colorizer =
      function
      | ErrorCode.HelpText -> None
      | _ -> Some ConsoleColor.Red
  )

let parser =
  ArgumentParser.Create<Arguments>(programName = "SageFs", errorHandler = errorHandler)

let parseArgs args =
  parser.ParseCommandLine(args).GetAllResults()

