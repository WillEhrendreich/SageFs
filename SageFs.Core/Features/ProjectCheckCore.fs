namespace SageFs.Features

open System
open System.IO
open System.Security.Cryptography
open System.Text
open SageFs

/// The decisions behind `check_project`: type-check a whole project with some files replaced, without building it and
/// without a session. Everything here is a function of values, so a test and the simulation fold the real rules.
///
/// The effects (the MSBuild process, the compiler, the files) are in `ProjectCheck`; they hand these functions what they
/// read. The compiler's arguments come from a design-time MSBuild run (`CoreCompile` with `ProvideCommandLineArgs` and
/// `SkipCompilerExecution`), are kept per project and configuration, and are read again when a file that shaped them
/// changed. A referenced project whose output is older than its inputs is built once, deepest first. Overlays replace
/// project files by stable files the compiler is pointed at, and a diagnostic in one is reported under the project's own
/// path.
module ProjectCheckCore =

  // ---- paths ----

  let pathComparison : StringComparison =
    match OperatingSystem.IsWindows() with
    | true -> StringComparison.OrdinalIgnoreCase
    | false -> StringComparison.Ordinal

  /// A path made absolute against `baseDir`. Never throws: a path that cannot be resolved stays as written.
  let fullPath (baseDir: string) (path: string) : string =
    try Path.GetFullPath(path, baseDir) with _ -> path

  let samePath (a: string) (b: string) : bool = String.Equals(a, b, pathComparison)

  // ---- diagnostics ----

  [<RequireQualifiedAccess>]
  type Severity =
    | Error
    | Warning
    | Info
    | Hidden

  module Severity =
    let toWord (severity: Severity) : string =
      match severity with
      | Severity.Error -> "error"
      | Severity.Warning -> "warning"
      | Severity.Info -> "info"
      | Severity.Hidden -> "hidden"

    /// Errors first.
    let rank (severity: Severity) : int =
      match severity with
      | Severity.Error -> 0
      | Severity.Warning -> 1
      | Severity.Info -> 2
      | Severity.Hidden -> 3

  /// One thing the compiler said, in the path the caller knows the file by. Lines and columns are 1-based and 0-based as
  /// the compiler gives them (line from 1, column from 0).
  type CheckDiagnostic =
    { Path: string
      Line: int
      Column: int
      EndLine: int
      EndColumn: int
      Severity: Severity
      Code: string
      Message: string }

  // ---- compiler arguments ----

  [<RequireQualifiedAccess>]
  type ArgumentsRefusal =
    /// The MSBuild run ended without a usable `CoreCompile` result.
    | TargetFailed of detail: string
    /// The MSBuild output was not the JSON the run asks for.
    | Unreadable of detail: string
    /// The run answered Success with no source files.
    | NoSources
    /// The run answered Success with no reference to FSharp.Core. The reference targets did not run, and a check against
    /// no references would report every name as undefined.
    | NoFSharpCore

  module ArgumentsRefusal =
    let describe (refusal: ArgumentsRefusal) : string =
      match refusal with
      | ArgumentsRefusal.TargetFailed detail -> sprintf "MSBuild could not produce the compiler arguments: %s" detail
      | ArgumentsRefusal.Unreadable detail -> sprintf "MSBuild's answer could not be read: %s" detail
      | ArgumentsRefusal.NoSources -> "MSBuild produced no source files for the project, so there is nothing to check."
      | ArgumentsRefusal.NoFSharpCore ->
        "MSBuild produced no reference to FSharp.Core, so the reference targets did not run. Restore the project (dotnet restore) and check again."

  /// What the compiler is given: every option, and the source files in compile order as full paths.
  type CompilerArguments =
    { ProjectFile: string
      Options: string list
      Sources: string list }

  module CompilerArguments =
    let isOption (identity: string) : bool = identity.StartsWith("-", StringComparison.Ordinal)

    /// The path of a `-r:` or `--reference:` option, or none for any other option.
    let referencePath (option: string) : string option =
      match option.StartsWith("-r:", StringComparison.Ordinal), option.StartsWith("--reference:", StringComparison.Ordinal) with
      | true, _ -> Some(option.Substring 3)
      | _, true -> Some(option.Substring 12)
      | false, false -> None

    let references (arguments: CompilerArguments) : string list = arguments.Options |> List.choose referencePath

    /// The identities of `CoreCompile`'s result are the arguments, one each. Sources are relative to the project directory.
    /// The output path (`-o:`) is dropped: nothing is written.
    let ofIdentities (projectFile: string) (identities: string list) : Result<CompilerArguments, ArgumentsRefusal> =
      let directory = Path.GetDirectoryName projectFile
      let options =
        identities
        |> List.filter isOption
        |> List.filter (fun o -> not (o.StartsWith("-o:", StringComparison.Ordinal)))
      let sources = identities |> List.filter (isOption >> not) |> List.map (fullPath directory)
      match sources with
      | [] -> Error ArgumentsRefusal.NoSources
      | _ ->
        let arguments = { ProjectFile = projectFile; Options = options; Sources = sources }
        let hasFSharpCore =
          references arguments
          |> List.exists (fun r -> String.Equals(Path.GetFileName r, "FSharp.Core.dll", StringComparison.OrdinalIgnoreCase))
        match hasFSharpCore with
        | true -> Ok arguments
        | false -> Error ArgumentsRefusal.NoFSharpCore

  // ---- what the MSBuild run said ----

  /// A project the compiler reads the output of, and the file it reads (a reference assembly under that project's `obj`).
  type ReferencedProject = { Project: string; Output: string }

  type MsBuildFacts =
    { Arguments: CompilerArguments
      Configuration: string
      References: ReferencedProject list
      /// Every project, props and targets file the evaluation read (`MSBuildAllProjects`).
      Inputs: string list }

  /// The shape of `dotnet msbuild -getTargetResult -getItem -getProperty` output, only the parts that are read.
  type MsBuildItem =
    { Identity: string
      MSBuildSourceProjectFile: string option }

  type MsBuildTargetResult =
    { Result: string
      Items: MsBuildItem[] option }

  type MsBuildTargetResults = { CoreCompile: MsBuildTargetResult option }

  type MsBuildItemGroups = { ReferencePathWithRefAssemblies: MsBuildItem[] option }

  type MsBuildProperties =
    { MSBuildAllProjects: string option
      Configuration: string option }

  type MsBuildOutput =
    { TargetResults: MsBuildTargetResults option
      Items: MsBuildItemGroups option
      Properties: MsBuildProperties option }

  module MsBuildFacts =
    /// The arguments to pass `dotnet msbuild` after the project file. The targets that resolve references and the
    /// reference assemblies run first, because without them `CoreCompile` answers Success with no `-r:` at all.
    let msbuildArguments : string list =
      [ "-nodeReuse:false"
        "-t:ResolveReferences,FindReferenceAssembliesForReferences,BeforeBuild,BeforeCompile,CoreCompile"
        "-p:DesignTimeBuild=true"
        "-p:SkipCompilerExecution=true"
        "-p:ProvideCommandLineArgs=true"
        "-p:BuildProjectReferences=false"
        "-getTargetResult:CoreCompile"
        "-getItem:ReferencePathWithRefAssemblies"
        "-getProperty:MSBuildAllProjects,Configuration" ]

    /// The arguments for one configuration.
    let msbuildArgumentsFor (configuration: string) : string list =
      msbuildArguments @ [ sprintf "-p:Configuration=%s" configuration ]

    /// Reads the JSON the run printed. `projectFile` is the project the run was for.
    let read (projectFile: string) (json: string) : Result<MsBuildFacts, ArgumentsRefusal> =
      match Json.deserialize<MsBuildOutput> (Json.omitNulls Json.standard) json with
      | Error why -> Error(ArgumentsRefusal.Unreadable(JsonError.describe why))
      | Ok output ->
        match output.TargetResults |> Option.bind (fun t -> t.CoreCompile) with
        | None -> Error(ArgumentsRefusal.TargetFailed "CoreCompile did not run")
        | Some target when target.Result <> "Success" ->
          Error(ArgumentsRefusal.TargetFailed(sprintf "CoreCompile ended %s" target.Result))
        | Some target ->
          let identities = target.Items |> Option.defaultValue [||] |> Array.map (fun i -> i.Identity) |> Array.toList
          CompilerArguments.ofIdentities projectFile identities
          |> Result.map (fun arguments ->
            let references =
              output.Items
              |> Option.bind (fun g -> g.ReferencePathWithRefAssemblies)
              |> Option.defaultValue [||]
              |> Array.choose (fun i ->
                i.MSBuildSourceProjectFile |> Option.map (fun project -> { Project = project; Output = i.Identity }))
              |> Array.toList
            let properties = output.Properties
            let inputs =
              properties
              |> Option.bind (fun p -> p.MSBuildAllProjects)
              |> Option.map (fun all -> all.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries) |> Array.toList)
              |> Option.defaultValue []
            let configuration = properties |> Option.bind (fun p -> p.Configuration) |> Option.defaultValue ""
            { Arguments = arguments; Configuration = configuration; References = references; Inputs = inputs })

  // ---- arguments kept between calls ----

  /// What a file was when a run read it. A file that was not there is part of the fingerprint too: creating a
  /// `Directory.Build.props` above the project changes the arguments.
  [<RequireQualifiedAccess>]
  type StampState =
    | Absent
    | Present of lastWriteTicks: int64 * length: int64

  type FileStamp = { Path: string; State: StampState }

  type CacheKey = { Project: string; Configuration: string }

  type CacheEntry =
    { Key: CacheKey
      Facts: MsBuildFacts
      Stamps: FileStamp list }

  module Fingerprint =
    let ancestors (directory: string) : string list =
      let rec up (dir: string) (acc: string list) =
        match Path.GetDirectoryName dir with
        | null
        | "" -> List.rev (dir :: acc)
        | parent when String.Equals(parent, dir, pathComparison) -> List.rev (dir :: acc)
        | parent -> up parent (dir :: acc)
      up directory []

    /// The files whose change can change the arguments: every file the evaluation read, the project file, the restore's
    /// assets, and the places a file would be read from if it existed (props, targets, central package versions and
    /// `global.json`, in the project's directory and every one above it).
    let paths (facts: MsBuildFacts) : string list =
      let projectFile = facts.Arguments.ProjectFile
      let directory = Path.GetDirectoryName projectFile
      let probed =
        [ for dir in ancestors directory do
            for name in [ "Directory.Build.props"; "Directory.Build.targets"; "Directory.Packages.props"; "global.json" ] do
              yield Path.Combine(dir, name) ]
      facts.Inputs @ [ projectFile; Path.Combine(directory, "obj", "project.assets.json") ] @ probed
      |> List.distinctBy (fun p -> match OperatingSystem.IsWindows() with true -> p.ToLowerInvariant() | false -> p)

    let stamp (current: string -> StampState) (facts: MsBuildFacts) : FileStamp list =
      paths facts |> List.map (fun p -> { Path = p; State = current p })

  [<RequireQualifiedAccess>]
  type RefreshReason =
    | NotCached
    | FileChanged of path: string
    | FileAppeared of path: string
    | FileGone of path: string

  module RefreshReason =
    let describe (reason: RefreshReason) : string =
      match reason with
      | RefreshReason.NotCached -> "no arguments were kept for this project and configuration"
      | RefreshReason.FileChanged path -> sprintf "%s changed" path
      | RefreshReason.FileAppeared path -> sprintf "%s appeared" path
      | RefreshReason.FileGone path -> sprintf "%s is gone" path

  [<RequireQualifiedAccess>]
  type CacheDecision =
    | Reuse of MsBuildFacts
    | Refresh of RefreshReason

  module ArgumentCache =
    /// Reuse the kept arguments when every file of their fingerprint is as it was, and otherwise say which file moved.
    let decide (entry: CacheEntry option) (current: string -> StampState) : CacheDecision =
      match entry with
      | None -> CacheDecision.Refresh RefreshReason.NotCached
      | Some kept ->
        let moved =
          kept.Stamps
          |> List.tryPick (fun stamp ->
            match stamp.State, current stamp.Path with
            | before, now when before = now -> None
            | StampState.Absent, StampState.Present _ -> Some(RefreshReason.FileAppeared stamp.Path)
            | StampState.Present _, StampState.Absent -> Some(RefreshReason.FileGone stamp.Path)
            | StampState.Present _, StampState.Present _ -> Some(RefreshReason.FileChanged stamp.Path)
            | StampState.Absent, StampState.Absent -> None)
        match moved with
        | Some reason -> CacheDecision.Refresh reason
        | None -> CacheDecision.Reuse kept.Facts

  /// How a run that read the arguments ended.
  [<RequireQualifiedAccess>]
  type RunEnd =
    | Completed
    | Cancelled

  type ArgumentStore = Map<CacheKey, CacheEntry>

  module ArgumentStore =
    let empty : ArgumentStore = Map.empty

    let tryFind (key: CacheKey) (store: ArgumentStore) : CacheEntry option = Map.tryFind key store

    /// A run that was cancelled read half a picture, so it keeps nothing.
    let record (entry: CacheEntry) (ending: RunEnd) (store: ArgumentStore) : ArgumentStore =
      match ending with
      | RunEnd.Completed -> Map.add entry.Key entry store
      | RunEnd.Cancelled -> store

  [<RequireQualifiedAccess>]
  type Joined =
    /// Nobody is running this key: start the run.
    | Starts
    /// A run for this key is in flight: wait for it.
    | Follows

  type Inflight = Set<CacheKey>

  module Inflight =
    let empty : Inflight = Set.empty

    let join (key: CacheKey) (inflight: Inflight) : Joined * Inflight =
      match Set.contains key inflight with
      | true -> Joined.Follows, inflight
      | false -> Joined.Starts, Set.add key inflight

    let finish (key: CacheKey) (inflight: Inflight) : Inflight = Set.remove key inflight

  // ---- referenced projects ----

  [<RequireQualifiedAccess>]
  type OutputState =
    | NoOutput of path: string
    | OutputAt of path: string * lastWriteTicks: int64

  [<RequireQualifiedAccess>]
  type Staleness =
    | Current
    | Missing of output: string
    /// The newest input that is newer than the output.
    | OlderThan of input: string

  module Staleness =
    /// An output with no file is missing, and an output older than any input is behind it. The same time is current.
    let decide (output: OutputState) (inputs: (string * int64) list) : Staleness =
      match output with
      | OutputState.NoOutput path -> Staleness.Missing path
      | OutputState.OutputAt(_, built) ->
        inputs
        |> List.filter (fun (_, written) -> written > built)
        |> List.sortByDescending snd
        |> List.tryHead
        |> function
          | Some(path, _) -> Staleness.OlderThan path
          | None -> Staleness.Current

    /// What a project is built from, with the write time of each file that exists: the files named, and the output of each
    /// project it references (it is compiled against them, so a newer one puts it behind). `writeTime` is none for a file
    /// that is not there.
    let inputsOf (writeTime: string -> int64 option) (files: string list) (referenceOutputs: string list) : (string * int64) list =
      files @ referenceOutputs |> List.choose (fun path -> writeTime path |> Option.map (fun ticks -> path, ticks))

    /// A file under the project's own `obj` directory is written by the evaluation (the assembly info, the attributes file),
    /// not edited, so it is not something the output can be behind.
    let isGeneratedIn (projectDirectory: string) (path: string) : bool =
      let objDirectory = Path.Combine(projectDirectory, "obj") + string Path.DirectorySeparatorChar
      path.StartsWith(objDirectory, pathComparison)

    /// The projects to build, deepest first. A project is built when it is stale itself or when anything it references
    /// is, because its inputs include the output of what it references. `references` says what each project references
    /// (every project of the closure is a key).
    let buildOrder (references: Map<string, string list>) (own: Map<string, Staleness>) : string list =
      let isStale (project: string) =
        match Map.tryFind project own with
        | Some Staleness.Current
        | None -> false
        | Some(Staleness.Missing _)
        | Some(Staleness.OlderThan _) -> true
      let rec visit (visited: Set<string>, order: string list, behind: Set<string>) (project: string) =
        match Set.contains project visited with
        | true -> visited, order, behind
        | false ->
          let deps = Map.tryFind project references |> Option.defaultValue []
          let visited, order, behind = deps |> List.fold visit (Set.add project visited, order, behind)
          let needs = isStale project || deps |> List.exists (fun d -> Set.contains d behind)
          match needs with
          | true -> visited, project :: order, Set.add project behind
          | false -> visited, order, behind
      let roots = references |> Map.toList |> List.map fst
      let _, order, _ = roots |> List.fold visit (Set.empty, [], Set.empty)
      List.rev order

  // ---- overlays ----

  type Overlay = { Path: string; Text: string }

  [<RequireQualifiedAccess>]
  type OverlayRefusal =
    /// The project does not compile this file. The list is what it does compile.
    | NotInProject of path: string * sources: string list
    | Duplicate of path: string

  module OverlayRefusal =
    let describe (refusal: OverlayRefusal) : string =
      match refusal with
      | OverlayRefusal.NotInProject(path, sources) ->
        let shown = sources |> List.truncate 20
        let more =
          match List.length sources - List.length shown with
          | 0 -> ""
          | n -> sprintf " and %d more" n
        sprintf "%s is not a source file of the project. The project compiles: %s%s" path (String.concat ", " shown) more
      | OverlayRefusal.Duplicate path -> sprintf "%s is given twice. Send one overlay for each file." path

  /// What the compiler is given when files are overlaid.
  type OverlayPlan =
    { /// The project's sources in compile order, each overlaid file at its overlay path.
      Sources: string list
      /// (project path, overlay path) for each file replaced.
      Replaced: (string * string) list
      /// (overlay path, text) to write before the check.
      Writes: (string * string) list }

  module Overlays =
    /// A short stable name for a project, so its overlays have a directory of their own.
    let projectKey (projectFile: string) : string =
      let bytes = SHA256.HashData(Encoding.UTF8.GetBytes projectFile)
      Convert.ToHexString(bytes).Substring(0, 12).ToLowerInvariant()

    /// The overlay file for the source at `index` in compile order. The same project and position always give the same
    /// path, so the compiler's per-file cache sees only a changed timestamp, and the file keeps its name because an
    /// implicit module is named for it.
    let overlayPath (root: string) (projectFile: string) (index: int) (source: string) : string =
      Path.Combine(root, projectKey projectFile, string index, Path.GetFileName source)

    let resolve (root: string) (arguments: CompilerArguments) (overlays: Overlay list) : Result<OverlayPlan, OverlayRefusal> =
      let directory = Path.GetDirectoryName arguments.ProjectFile
      let named = overlays |> List.map (fun o -> fullPath directory o.Path, o.Text)
      let duplicate =
        named
        |> List.map fst
        |> List.tryFind (fun p -> (named |> List.filter (fun (q, _) -> samePath p q) |> List.length) > 1)
      let outside =
        named |> List.map fst |> List.tryFind (fun p -> not (arguments.Sources |> List.exists (samePath p)))
      match duplicate, outside with
      | Some path, _ -> Error(OverlayRefusal.Duplicate path)
      | None, Some path -> Error(OverlayRefusal.NotInProject(path, arguments.Sources))
      | None, None ->
        let placed =
          arguments.Sources
          |> List.mapi (fun index source ->
            match named |> List.tryFind (fun (p, _) -> samePath p source) with
            | Some(_, text) ->
              let overlay = overlayPath root arguments.ProjectFile index source
              overlay, Some(source, overlay, text)
            | None -> source, None)
        Ok
          { Sources = placed |> List.map fst
            Replaced = placed |> List.choose snd |> List.map (fun (source, overlay, _) -> source, overlay)
            Writes = placed |> List.choose snd |> List.map (fun (_, overlay, text) -> overlay, text) }

    /// The project's own path for a diagnostic's file, so the caller reads the path it sent.
    let mapBack (plan: OverlayPlan) (path: string) : string =
      plan.Replaced
      |> List.tryFind (fun (_, overlay) -> samePath overlay path)
      |> Option.map fst
      |> Option.defaultValue path

  // ---- what a caller sends ----

  module Request =
    [<Literal>]
    let defaultConfiguration = "Debug"

    /// The configuration to check, or Debug when the caller named none.
    let configurationOf (given: string) : string =
      match String.IsNullOrWhiteSpace given with
      | true -> defaultConfiguration
      | false -> given.Trim()

    /// The overlays as the tool receives them: a JSON array of `{ "path": ..., "text": ... }`, or nothing for none.
    let parseOverlays (json: string) : Result<Overlay list, string> =
      match String.IsNullOrWhiteSpace json with
      | true -> Ok []
      | false ->
        match Json.deserialize<Overlay[]> (Json.omitNulls Json.camelCase) json with
        | Ok items -> Ok(Array.toList items)
        | Error why ->
          Error(sprintf "overlays must be a JSON array of { \"path\": \"...\", \"text\": \"...\" } objects: %s" (JsonError.describe why))

  // ---- the outcome ----

  [<RequireQualifiedAccess>]
  type Stopped =
    | ArgumentsRun
    | ReferenceBuild of project: string
    | Compiler

  [<RequireQualifiedAccess>]
  type ArgumentsSource =
    | Kept
    | Read of RefreshReason

  type RunNotes =
    { Arguments: ArgumentsSource
      /// The referenced projects built before the check, in the order they were built.
      Built: string list }

  [<RequireQualifiedAccess>]
  type CheckRefusal =
    | Overlay of OverlayRefusal
    | Arguments of ArgumentsRefusal
    | ReferenceBuildFailed of project: string * detail: string
    | CompilerFailed of detail: string

  [<RequireQualifiedAccess>]
  type CheckOutcome =
    | Checked of diagnostics: CheckDiagnostic list * notes: RunNotes
    | Refused of CheckRefusal
    | Cancelled of Stopped
    | TimedOut of Stopped * after: TimeSpan

  module Report =
    let stoppedWords (stopped: Stopped) : string =
      match stopped with
      | Stopped.ArgumentsRun -> "the MSBuild run that reads the compiler arguments"
      | Stopped.ReferenceBuild project -> sprintf "the build of referenced project %s" project
      | Stopped.Compiler -> "the compiler check"

    let describeRefusal (refusal: CheckRefusal) : string =
      match refusal with
      | CheckRefusal.Overlay r -> OverlayRefusal.describe r
      | CheckRefusal.Arguments r -> ArgumentsRefusal.describe r
      | CheckRefusal.ReferenceBuildFailed(project, detail) ->
        sprintf "Referenced project %s did not build, so the project cannot be checked: %s" project detail
      | CheckRefusal.CompilerFailed detail -> sprintf "The compiler could not check the project: %s" detail

    /// Errors first, then by path, line and column.
    let ordered (diagnostics: CheckDiagnostic list) : CheckDiagnostic list =
      diagnostics |> List.sortBy (fun d -> Severity.rank d.Severity, d.Path, d.Line, d.Column)

    let errorCount (diagnostics: CheckDiagnostic list) : int =
      diagnostics |> List.filter (fun d -> d.Severity = Severity.Error) |> List.length

    let line (d: CheckDiagnostic) : string =
      sprintf "%s(%d,%d): %s %s: %s" d.Path d.Line d.Column (Severity.toWord d.Severity) d.Code (d.Message.Replace("\r", "").Replace("\n", " "))

    let notesLine (notes: RunNotes) : string =
      let arguments =
        match notes.Arguments with
        | ArgumentsSource.Kept -> "compiler arguments kept from an earlier call"
        | ArgumentsSource.Read why -> sprintf "compiler arguments read (%s)" (RefreshReason.describe why)
      match notes.Built with
      | [] -> arguments
      | built -> sprintf "%s; built first: %s" arguments (String.concat ", " built)

    /// The one place the reply's text is written. At most `maxLines` diagnostics are listed, and what was cut is counted.
    let render (maxLines: int) (outcome: CheckOutcome) : string =
      match outcome with
      | CheckOutcome.Refused refusal -> describeRefusal refusal
      | CheckOutcome.Cancelled stopped -> sprintf "Cancelled: stopped %s." (stoppedWords stopped)
      | CheckOutcome.TimedOut(stopped, after) ->
        sprintf "Timed out after %.0f s: stopped %s. Nothing was concluded about the project." after.TotalSeconds (stoppedWords stopped)
      | CheckOutcome.Checked(diagnostics, notes) ->
        let sorted = ordered diagnostics
        let errors = errorCount sorted
        let warnings = sorted |> List.filter (fun d -> d.Severity = Severity.Warning) |> List.length
        let headline =
          match errors with
          | 0 -> sprintf "Clean: the project compiles (%d warnings). %s." warnings (notesLine notes)
          | n -> sprintf "%d errors, %d warnings. %s." n warnings (notesLine notes)
        let shown = sorted |> List.truncate maxLines |> List.map line
        let cut = List.length sorted - List.length shown
        let tail =
          match cut with
          | 0 -> []
          | n -> [ sprintf "... %d more not shown" n ]
        String.concat "\n" (headline :: shown @ tail)
