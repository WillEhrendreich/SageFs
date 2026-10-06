/// What running the daemon as a launchd user agent on macOS trips over. launchd
/// expands no variables in a plist, so the plist we ship holds placeholders that
/// the README's install fills in, and every one of them has to be filled. The
/// rest mirrors the systemd unit: restart on failure, start where SageFs will
/// watch from, and name flags the CLI really has.
module SageFs.Tests.FirstRunMacOSTests

open System
open System.IO
open System.Text.RegularExpressions
open System.Xml
open System.Xml.Linq
open Expecto
open Expecto.Flip
open SageFs

let private repoRoot =
  let rec up (dir: DirectoryInfo) =
    match dir with
    | null -> failwith "repo root (the folder holding Readme.md and contrib/) not found above the test assembly"
    | d when File.Exists(Path.Combine(d.FullName, "Readme.md")) && Directory.Exists(Path.Combine(d.FullName, "contrib")) -> d.FullName
    | d -> up d.Parent
  up (DirectoryInfo AppContext.BaseDirectory)

let private label = "io.github.willehrendreich.sagefs"

let private plistPath = Path.Combine(repoRoot, "contrib", "launchd", label + ".plist")

/// The plist values a launchd agent uses.
type private PlistValue =
  | PString of string
  | PBool of bool
  | PInteger of int64
  | PArray of PlistValue list
  | PDict of (string * PlistValue) list

let rec private parseValue (element: XElement) : PlistValue =
  match element.Name.LocalName with
  | "string" -> PString element.Value
  | "true" -> PBool true
  | "false" -> PBool false
  | "integer" -> PInteger(int64 element.Value)
  | "array" -> element.Elements() |> Seq.map parseValue |> Seq.toList |> PArray
  | "dict" ->
    element.Elements()
    |> Seq.chunkBySize 2
    |> Seq.map (fun pair ->
      match pair with
      | [| key; value |] when key.Name.LocalName = "key" -> key.Value, parseValue value
      | _ -> failwithf "a dict holds <key> and value pairs, got %A" pair)
    |> Seq.toList
    |> PDict
  | other -> failwithf "a plist element an agent does not use: <%s>" other

/// The agent's top-level dict. The DOCTYPE names Apple's DTD by URL, so it is ignored rather than fetched.
let private agent () : Map<string, PlistValue> =
  let settings = XmlReaderSettings(DtdProcessing = DtdProcessing.Ignore)
  use reader = XmlReader.Create(plistPath, settings)
  let root = XDocument.Load(reader).Root
  match root.Name.LocalName, root.Elements() |> Seq.toList with
  | "plist", [ single ] ->
    match parseValue single with
    | PDict pairs -> Map.ofList pairs
    | other -> failwithf "the plist's one child is a dict, got %A" other
  | name, children -> failwithf "expected <plist> holding one <dict>, got <%s> with %d children" name children.Length

/// The plist's leading XML comment, which repeats the README's install for someone reading the file on its own.
let private headerComment () : string =
  let settings = XmlReaderSettings(DtdProcessing = DtdProcessing.Ignore)
  use reader = XmlReader.Create(plistPath, settings)
  XDocument.Load(reader).Nodes()
  |> Seq.tryPick (fun node ->
    match node with
    | :? XComment as comment -> Some comment.Value
    | _ -> None)
  |> Option.defaultWith (fun () -> failwith "the plist opens with a comment that says how to install it")

/// Non-blank lines with their indentation dropped, so a block reads the same at any indent.
let private trimmedLines (text: string) =
  text.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
  |> Array.map (fun line -> line.Trim())
  |> Array.filter (fun line -> line <> "")

let private stringAt key (values: Map<string, PlistValue>) =
  match values |> Map.tryFind key with
  | Some(PString s) -> s
  | other -> failwithf "%s is a <string>, got %A" key other

[<Tests>]
let tests =
  testList "first run on macOS" [
    testSequenced <| testList "contrib/launchd/io.github.willehrendreich.sagefs.plist" [
      test "exists and parses as a plist dict whose Label matches the file name" {
        Expect.isTrue "the plist is shipped" (File.Exists plistPath)
        agent () |> stringAt "Label" |> Expect.equal "launchctl finds the agent by this label" label
      }

      test "starts at login and restarts the supervisor after a crash" {
        let values = agent ()
        values |> Map.tryFind "RunAtLoad" |> Expect.equal "starts when the agent loads, at login" (Some(PBool true))
        values
        |> Map.tryFind "KeepAlive"
        |> Expect.equal "restarts on a failed exit only, like Restart=on-failure" (Some(PDict [ "SuccessfulExit", PBool false ]))
      }

      test "sets DOTNET_ROOT, because launchd reads no shell profile" {
        match agent () |> Map.tryFind "EnvironmentVariables" with
        | Some(PDict vars) -> vars |> List.map fst |> Expect.contains "the tool's launcher finds the runtime through it" "DOTNET_ROOT"
        | other -> failtestf "EnvironmentVariables is a dict, got %A" other
      }

      test "WorkingDirectory is one SageFs will watch from, outside its data folder, and one the README install creates" {
        // A daemon started in $HOME hands $HOME out as a session root (a request that names no directory falls
        // back to the daemon's cwd), and SageFs refuses to watch a home directory. Inside the data folder, the
        // daemon would watch its own log.
        let raw = agent () |> stringAt "WorkingDirectory"
        let home = Path.Combine(Path.DirectorySeparatorChar.ToString(), "Users", "someone")
        let workingDirectory = raw.Replace("@HOME@", home)
        FileWatcher.classifyWatchRoot home workingDirectory
        |> Expect.equal "the daemon's cwd is not the home directory or above it" FileWatcher.WatchRootVerdict.Watchable
        // On macOS, LocalApplicationData is ~/Library/Application Support, and DaemonState keeps SageFs under it.
        // APFS is case-insensitive by default, so the comparison is too.
        let dataFolder = Path.Combine(home, "Library", "Application Support", "SageFs")
        let inside =
          workingDirectory.Equals(dataFolder, StringComparison.OrdinalIgnoreCase)
          || workingDirectory.StartsWith(dataFolder + "/", StringComparison.OrdinalIgnoreCase)
        Expect.isFalse "the working directory is outside the folder the daemon writes its log to" inside
        // launchd creates no working directory, so the install has to.
        let readme = File.ReadAllText(Path.Combine(repoRoot, "Readme.md"))
        readme
        |> Expect.stringContains "the README install makes the working directory" (sprintf "\"%s\"" (raw.Replace("@HOME@", "$HOME")))
      }

      test "every placeholder is one the README install fills in" {
        let plist = File.ReadAllText plistPath
        let placeholders =
          Regex.Matches(plist, "@[A-Z_]+@") |> Seq.map (fun m -> m.Value) |> Seq.distinct |> Seq.toList
        Expect.isNonEmpty "the plist holds placeholders, since launchd expands no variables" placeholders
        let readme = File.ReadAllText(Path.Combine(repoRoot, "Readme.md"))
        for placeholder in placeholders do
          readme |> Expect.stringContains (sprintf "the README's sed fills in %s" placeholder) (sprintf "s|%s|" placeholder)
      }

      test "the plist's comment carries the README's install block, line for line" {
        // The other tests read the README's install. The copy in the plist's comment is the one a reader of
        // the file follows, so it must not drift from the README's.
        let readme = File.ReadAllText(Path.Combine(repoRoot, "Readme.md"))
        let block = Regex.Match(readme, @"### Run it as a service \(macOS\)[\s\S]*?```bash\r?\n([\s\S]*?)```")
        Expect.isTrue "the README's macOS section has a bash install block" block.Success
        let install = trimmedLines block.Groups[1].Value |> String.concat "\n"
        Expect.isNotEmpty "the install block has commands" install
        headerComment () |> trimmedLines |> String.concat "\n"
        |> Expect.stringContains "the plist's comment repeats the README's install exactly" install
      }

      test "ProgramArguments runs the installed tool with a flag the CLI help really lists" {
        let arguments =
          match agent () |> Map.tryFind "ProgramArguments" with
          | Some(PArray items) ->
            items
            |> List.map (fun item ->
              match item with
              | PString s -> s
              | other -> failwithf "each argument is a <string>, got %A" other)
          | other -> failwithf "ProgramArguments is an array, got %A" other
        List.head arguments |> Expect.stringEnds "the installed global tool path" ".dotnet/tools/sagefs"
        let flags = List.tail arguments
        Expect.isNonEmpty "ProgramArguments passes at least one flag" flags
        let original = Console.Out
        use writer = new StringWriter()
        Console.SetOut writer
        let help =
          try
            Program.main [| "--help" |] |> ignore
            writer.ToString()
          finally
            Console.SetOut original
        for flag in flags do
          help |> Expect.stringContains (sprintf "--help lists %s" flag) flag
      }
    ]
  ]
