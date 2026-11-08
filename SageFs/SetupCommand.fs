namespace SageFs

open System
open System.Diagnostics
open System.IO

/// `sagefs setup [--client C] [--dir D] [--write] [--skip-skill]`: register SageFs as a stdio server in the agent
/// clients on this machine. Without `--write` it prints what it would do and writes nothing. With `--write` it
/// writes only what it printed: a merged config file (every other server kept, a file it cannot parse left alone),
/// Claude Code's own `claude mcp add`, and the skill. The text it prints is `ClientSetup.page`, the same text as
/// docs/client-setup.md.
module SetupCommand =

  /// Which clients to set up.
  type Target =
    /// Every client that looks installed here.
    | FoundClients
    | OneClient of Client

  /// The directory the project-level config files are for.
  type Where =
    | CurrentDirectory
    | InDirectory of path: string

  type Mode =
    /// Print what would be done and write nothing.
    | Preview
    /// Do it, and say what was done.
    | Write

  type SkillStep =
    | InstallSkill
    | LeaveSkill

  type Args =
    { Target: Target
      Where: Where
      Mode: Mode
      Skill: SkillStep }

  type ArgError =
    | MissingValue of flag: string
    | UnknownClientName of UnknownClient
    | UnknownFlag of flag: string

  let describeArgError (error: ArgError) : string =
    match error with
    | MissingValue flag -> sprintf "%s needs a value. → For example `sagefs setup --client vscode`." flag
    | UnknownClientName unknown -> Client.describeUnknown unknown
    | UnknownFlag flag -> sprintf "%s is not a flag of `sagefs setup`. → The flags are --client, --dir, --write and --skip-skill." flag

  let parseArgs (args: string list) : Result<Args, ArgError> =
    let rec go (state: Args) (rest: string list) : Result<Args, ArgError> =
      match rest with
      | [] -> Ok state
      | "--write" :: tail -> go { state with Mode = Write } tail
      | "--skip-skill" :: tail -> go { state with Skill = LeaveSkill } tail
      | "--client" :: value :: tail when not (value.StartsWith("--", StringComparison.Ordinal)) ->
        match Client.parse value with
        | Ok client -> go { state with Target = OneClient client } tail
        | Error unknown -> Error(UnknownClientName unknown)
      | "--dir" :: value :: tail when not (value.StartsWith("--", StringComparison.Ordinal)) -> go { state with Where = InDirectory value } tail
      | ("--client" | "--dir" as flag) :: _ -> Error(MissingValue flag)
      | flag :: _ -> Error(UnknownFlag flag)
    go { Target = FoundClients; Where = CurrentDirectory; Mode = Preview; Skill = InstallSkill } args

  let private platform () : Platform =
    match OperatingSystem.IsWindows(), OperatingSystem.IsMacOS() with
    | true, _ -> Windows
    | _, true -> MacOs
    | _ -> Linux

  let private onPath (command: string) : bool =
    let names = if OperatingSystem.IsWindows() then [ command + ".exe"; command + ".cmd"; command ] else [ command ]
    (Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
    |> Array.exists (fun folder -> names |> List.exists (fun n -> File.Exists(Path.Combine(folder, n))))

  /// Whether the client looks installed here. A client that is not found is only skipped when none was named.
  let private found (file: Client -> string) (client: Client) : bool =
    match client with
    | ClaudeCode -> onPath "claude"
    | VsCode -> Directory.Exists(Path.GetDirectoryName(file VsCode))
    | Cursor ->
      Directory.Exists(Path.GetDirectoryName(file Cursor))
      || Directory.Exists(Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".cursor"))
    | ClaudeDesktop -> Directory.Exists(Path.GetDirectoryName(file ClaudeDesktop))

  let private runClaude (command: string list) : int =
    let psi = ProcessStartInfo(List.head command)
    for arg in List.tail command do
      psi.ArgumentList.Add arg
    psi.UseShellExecute <- false
    try
      use proc = Process.Start psi
      proc.WaitForExit()
      proc.ExitCode
    with :? ComponentModel.Win32Exception as ex ->
      eprintfn "sagefs setup: could not run `%s`: %s" (String.concat " " command) ex.Message
      1

  /// One client. Returns its exit code (0 done or printed, 1 refused or failed).
  let private setUpOne (args: Args) (file: Client -> string) (client: Client) : int =
    let snippet = ClientSetup.snippet client
    printfn "%s" (Client.label client)
    match args.Mode, ClientSetup.registrationCommand client with
    | Write, Some command ->
      printfn "  running: %s" (String.concat " " command)
      runClaude command
    | _, _ ->
      let path = file client
      match args.Mode with
      | Preview ->
        printfn "  would merge a 'sagefs' server into %s" path
        printfn "  (%s)" snippet.File
        printfn "%s" (snippet.Text.Replace("\n", "\n    ") |> sprintf "    %s")
        0
      | Write ->
        let existing = if File.Exists path then File.ReadAllText path else ""
        match ClientSetup.merge existing snippet with
        | AlreadyPresent ->
          printfn "  %s already registers sagefs. Nothing to write." path
          0
        | Refused reason ->
          eprintfn "  %s: %s" path (ClientSetup.describeRefusal reason)
          1
        | Merged text ->
          Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
          File.WriteAllText(path, text)
          printfn "  wrote %s" path
          0

  let run (args: Args) : int =
    let dir =
      match args.Where with
      | CurrentDirectory -> Environment.CurrentDirectory
      | InDirectory path -> Path.GetFullPath path
    let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
    let appData = Environment.GetFolderPath Environment.SpecialFolder.ApplicationData
    let file = ClientSetup.configFile (platform ()) home appData dir
    let clients =
      match args.Target with
      | OneClient client -> [ client ]
      | FoundClients -> Client.all |> List.filter (found file)
    match clients with
    | [] ->
      printfn "No agent client found here. Name one with --client (%s), or read docs/client-setup.md." (Client.all |> List.map Client.toToken |> String.concat ", ")
      printfn ""
      printfn "%s" ClientSetup.page
      0
    | _ ->
      let codes = clients |> List.map (setUpOne args file)
      printfn ""
      match args.Skill, args.Mode with
      | LeaveSkill, _ -> ()
      | InstallSkill, Preview -> printfn "Skill: run `sagefs install-skill` (or `sagefs setup --write`) so your agent works in the REPL."
      | InstallSkill, Write -> SkillInstallCommand.run (SkillInstallCommand.parseArgs []) |> ignore
      printfn "Why stdio and not HTTP: an HTTP registration loses its tools when the daemon restarts. See docs/client-setup.md."
      match List.contains 1 codes with
      | true -> 1
      | false -> 0
