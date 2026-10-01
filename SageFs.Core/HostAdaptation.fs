namespace SageFs

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Text
open System.Text.Json

/// What the FSI host does when the assemblies a project brings are not the ones the host's own process
/// carries.
///
/// FSI resolves an assembly by simple NAME. Once the host has loaded `System.Text.Json` (it does, the
/// protocol uses it) every later request for that name is answered with that copy, whatever version the
/// project asked for, and a member only the project's version has fails with a TypeLoadException far from
/// the cause. Two projects in one session that pin different versions of one package get the same
/// treatment: whichever loads first answers for both.
///
/// This module decides, from the assemblies a session references, which of three things is true for each
/// name, and nothing here touches the disk or the process:
///
///  - the newest version is safe to run (FSharp.Core and the shared framework are backward compatible by
///    policy, and `dotnet run` would run the highest one too): the project's copy goes into the host at
///    LAUNCH, through the same dependency manifest mechanism the runtime itself uses, so the host and the
///    project's code are on one copy from the first instruction;
///  - the host's own copy is the newest: nothing to do;
///  - the name is not safe to run at the newest version and two projects pin different assembly versions:
///    a conflict, which the session is refused for, with a message that names them. (Running one project
///    against the other's version is the wrong answer given silently.)
///
/// What is NOT here, on purpose: swapping the host's FSharp.Compiler.Service. The host's own code is
/// compiled against the SDK's, so a project's compiler service cannot replace it; that case is reported
/// (ProjectLoading.HostConcern.CompilerServiceShadowed), not adapted to.
module HostAdaptation =

  /// FSharp.Core: the one library outside the shared framework that is safe to run at its newest version.
  [<Literal>]
  let FSharpCoreName = "FSharp.Core"

  /// The environment variable the runtime reads for an extra dependency manifest.
  [<Literal>]
  let AdditionalDepsVariable = "DOTNET_ADDITIONAL_DEPS"

  /// The host option that adds a folder the runtime probes for a manifest's assets.
  [<Literal>]
  let AdditionalProbingPathOption = "--additionalprobingpath"

  /// One assembly a session references.
  type ReferencedAssembly =
    { /// Simple name, e.g. `System.Text.Json`.
      Name: string
      /// The assembly version the runtime compares (not the package or file version).
      Version: Version
      Path: string
      /// The project that brought it, by project path.
      Owner: string
      /// What the user knows it as, e.g. `PinLib 1.0.0` (the package).
      Source: string }

  /// A project's copy of a library, put into the host in place of the host's own.
  type Override =
    { Name: string
      Version: Version
      Path: string }

  /// One name that two or more projects pin at different assembly versions.
  type Conflict =
    { Assembly: string
      /// One pin per distinct version, the first project that brought it.
      Pins: ReferencedAssembly list }

  /// What to do for a session.
  type Plan =
    { Overrides: Override list
      Conflicts: Conflict list }

  let emptyPlan: Plan = { Overrides = []; Conflicts = [] }

  /// What the host process is launched with. The runtime major is only meaningful with overrides (the
  /// extra manifest names a target framework), so the two travel together.
  [<RequireQualifiedAccess>]
  type HostLibraries =
    /// The host as built: its own FSharp.Core and the shared framework's.
    | AsBuilt
    /// The project's copies of these libraries are loaded in place of the host's, for a host whose target
    /// framework is .NET `hostMajor`.
    | WithProjectCopies of hostMajor: int * overrides: Override list

  let private sameName (a: string) (b: string) = String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

  /// Pure. `newestWins name` says the library is safe to run at its newest version; `hostVersionOf name` is
  /// the version of the copy the host already carries, when it has one.
  let plan (newestWins: string -> bool) (hostVersionOf: string -> Version option) (referenced: ReferencedAssembly list) : Plan =
    let perName =
      referenced
      |> List.groupBy (fun r -> r.Name.ToLowerInvariant())
      |> List.map (fun (_, group) -> group)
    let decide (group: ReferencedAssembly list) : Choice<Override option, Conflict option> =
      let name = (List.head group).Name
      let distinct = group |> List.distinctBy (fun r -> r.Version)
      match newestWins name with
      | true ->
        let newest = group |> List.maxBy (fun r -> r.Version)
        match hostVersionOf name with
        | Some hostVersion when newest.Version > hostVersion ->
          Choice1Of2(Some { Name = newest.Name; Version = newest.Version; Path = newest.Path })
        | Some _
        | None -> Choice1Of2 None
      | false ->
        match distinct with
        | [ _ ]
        | [] -> Choice2Of2 None
        | pins -> Choice2Of2(Some { Assembly = name; Pins = pins })
    let decisions = perName |> List.map decide
    { Overrides = decisions |> List.choose (function Choice1Of2 o -> o | Choice2Of2 _ -> None)
      Conflicts = decisions |> List.choose (function Choice2Of2 c -> c | Choice1Of2 _ -> None) }

  /// The conflicts in words a reader can act on: which assembly, which version from which package in
  /// which project, and what to do. Never blank for a non-empty list.
  let describeConflicts (conflicts: Conflict list) : string =
    let describeConflict (conflict: Conflict) =
      let pins =
        conflict.Pins
        |> List.sortBy (fun p -> p.Version)
        |> List.map (fun p -> sprintf "%O from %s in %s" p.Version p.Source (Path.GetFileName p.Owner))
        |> String.concat "; "
      sprintf "  - %s: %s" conflict.Assembly pins
    sprintf
      "Projects in this session resolve different versions of the same assembly, and one FSI process loads one version of an assembly, so the project that pinned the other would silently run against the wrong one:\n%s\nAlign the versions (a Directory.Packages.props with central package management is the usual way), or create one session per project."
      (conflicts |> List.map describeConflict |> String.concat "\n")

  /// The folders the runtime has to probe for the overrides' files, each once.
  let probingPaths (overrides: Override list) : string list =
    overrides
    |> List.map (fun o -> Path.GetDirectoryName o.Path)
    |> List.distinct

  /// Host options that go BEFORE the host dll on the `dotnet` command line.
  let muxerOptions (overrides: Override list) : string list =
    probingPaths overrides |> List.collect (fun folder -> [ AdditionalProbingPathOption; folder ])

  /// The extra dependency manifest: each override a package whose asset is the file itself (the library's
  /// path is `.`, so the probing folder is the file's own folder), under the host's target framework.
  let additionalDepsJson (hostMajor: int) (overrides: Override list) : string =
    let targetName = sprintf ".NETCoreApp,Version=v%d.0" hostMajor
    use stream = new MemoryStream()
    (use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
     writer.WriteStartObject()
     writer.WriteStartObject "runtimeTarget"
     writer.WriteString("name", targetName)
     writer.WriteEndObject()
     writer.WriteStartObject "targets"
     writer.WriteStartObject targetName
     for o in overrides do
       writer.WriteStartObject(sprintf "%s/%O" o.Name o.Version)
       writer.WriteStartObject "runtime"
       writer.WriteStartObject(Path.GetFileName o.Path)
       writer.WriteString("assemblyVersion", o.Version.ToString())
       writer.WriteString("fileVersion", o.Version.ToString())
       writer.WriteEndObject()
       writer.WriteEndObject()
       writer.WriteEndObject()
     writer.WriteEndObject()
     writer.WriteEndObject()
     writer.WriteStartObject "libraries"
     for o in overrides do
       writer.WriteStartObject(sprintf "%s/%O" o.Name o.Version)
       writer.WriteString("type", "package")
       writer.WriteBoolean("serviceable", false)
       writer.WriteString("sha512", "")
       writer.WriteString("path", ".")
       writer.WriteEndObject()
     writer.WriteEndObject()
     writer.WriteEndObject())
    Encoding.UTF8.GetString(stream.ToArray())

  /// The environment the host process needs for its extra manifest at `depsFile`. Empty without overrides.
  let additionalDepsEnvironment (depsFile: string) (overrides: Override list) : (string * string) list =
    match overrides with
    | [] -> []
    | _ -> [ AdditionalDepsVariable, depsFile ]

  // ---- what the session is refused for, and what it is told ----

  /// A reason the session must not start. Both would otherwise give a wrong answer, silently or not.
  [<RequireQualifiedAccess>]
  type Refusal =
    | AssemblyVersionConflicts of Conflict list
    | FrameworkNotHostable of project: string * tfm: string * reason: ProjectCompatibility.UnsupportedTfmReason

  let describeRefusal (refusal: Refusal) : string =
    match refusal with
    | Refusal.AssemblyVersionConflicts conflicts -> describeConflicts conflicts
    | Refusal.FrameworkNotHostable(project, tfm, reason) ->
      ProjectCompatibility.describeUnhostable (Path.GetFileName project) [ tfm ] reason

  /// What each project's EVALUATED target framework (MSBuild's answer, props files and all, not the text
  /// of the .fsproj) says: a framework the host cannot load is a refusal; one nobody recognises is a
  /// concern the session carries. A netstandard library is hostable by whatever loads it, so it says
  /// nothing. `projects` is (project path, evaluated target framework).
  let targetFrameworkFindings (projects: (string * string) list) : Refusal list * (string * ProjectLoading.HostConcern) list =
    let findings =
      projects
      |> List.map (fun (project, tfm) ->
        match ProjectCompatibility.classifyTfm tfm with
        | ProjectCompatibility.TfmVerdict.Supported _ -> [], []
        | ProjectCompatibility.TfmVerdict.Unsupported(unsupported, reason) ->
          [ Refusal.FrameworkNotHostable(project, unsupported, reason) ], []
        | ProjectCompatibility.TfmVerdict.Unknown(unknown, _) when unknown.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) ->
          [], []
        | ProjectCompatibility.TfmVerdict.Unknown(unknown, reason) ->
          [], [ project, ProjectLoading.HostConcern.TargetFrameworkUnrecognised(unknown, reason) ])
    findings |> List.collect fst, findings |> List.collect snd

  /// A runtime requirement that could not be read for a reason other than "the project has not been built
  /// or has no runtimeconfig.json" (a library never has one) is a concern: the host started on its default
  /// runtime as a guess.
  let runtimeConcerns
    (requirements: (string * Result<RuntimeCompat.RuntimeRequirement, RuntimeCompat.RuntimeConfigError>) list)
    : (string * ProjectLoading.HostConcern) list =
    requirements
    |> List.choose (fun (project, requirement) ->
      match requirement with
      | Result.Error error when not (RuntimeCompat.isAbsence error) ->
        Some(project, ProjectLoading.HostConcern.RuntimeUndetermined(RuntimeCompat.describeConfigError error))
      | Result.Error _
      | Result.Ok _ -> None)

  [<Literal>]
  let CompilerServiceName = "FSharp.Compiler.Service"

  /// A project's own FSharp.Compiler.Service that is newer than the one the host runs on. The host cannot
  /// run on it (its own code is compiled against the SDK's), so the project is told instead.
  let compilerServiceConcerns (hostVersion: Version) (hostSdk: string) (referenced: ReferencedAssembly list) : (string * ProjectLoading.HostConcern) list =
    referenced
    |> List.filter (fun r -> sameName r.Name CompilerServiceName && r.Version > hostVersion)
    |> List.map (fun r ->
      r.Owner, ProjectLoading.HostConcern.CompilerServiceShadowed(r.Version.ToString(), hostVersion.ToString(), hostSdk))

  // ---- the IO edge: what the host's runtime carries ----

  /// The assemblies one runtime's shared frameworks carry, by simple name, with their assembly versions.
  /// `sharedRoot` is `<dotnet>/shared`; the runtime is the highest installed version of `major`, taken
  /// from Microsoft.NETCore.App and Microsoft.AspNetCore.App (the host references both). A framework or
  /// runtime that is not installed contributes nothing.
  let private readFrameworkAssemblies (sharedRoot: string) (major: int) : IReadOnlyDictionary<string, Version> =
    let result = Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase)
    for framework in [ "Microsoft.NETCore.App"; "Microsoft.AspNetCore.App" ] do
      let frameworkDir = Path.Combine(sharedRoot, framework)
      match Directory.Exists frameworkDir with
      | false -> ()
      | true ->
        let runtimeDir =
          Directory.GetDirectories frameworkDir
          |> Array.choose (fun dir ->
            match Version.TryParse((Path.GetFileName dir).Split('-').[0]) with
            | true, version when version.Major = major -> Some(version, Path.GetFileName dir, dir)
            | _ -> None)
          |> Array.sortBy (fun (version, name, _) -> version, name)
          |> Array.tryLast
        match runtimeDir with
        | None -> ()
        | Some(_, _, dir) ->
          for dll in Directory.GetFiles(dir, "*.dll") do
            try
              let name = AssemblyName.GetAssemblyName dll
              result[name.Name] <- name.Version
            with _ -> () // a native library or a resource assembly: not a managed identity
    result :> IReadOnlyDictionary<string, Version>

  let private frameworkCache = System.Collections.Concurrent.ConcurrentDictionary<struct (string * int), IReadOnlyDictionary<string, Version>>()

  /// `readFrameworkAssemblies`, read once per runtime for the life of the process: a session start plans the
  /// host twice, and opening some three hundred files each time is work the second time does not need. An
  /// empty answer (the runtime is not installed) is not cached, so installing one is seen.
  let frameworkAssemblies (sharedRoot: string) (major: int) : IReadOnlyDictionary<string, Version> =
    match frameworkCache.TryGetValue(struct (sharedRoot, major)) with
    | true, cached -> cached
    | false, _ ->
      let read = readFrameworkAssemblies sharedRoot major
      match read.Count with
      | 0 -> read
      | _ -> frameworkCache.GetOrAdd(struct (sharedRoot, major), read)

  /// The assembly version of a managed file, when it has one.
  let assemblyVersionOf (path: string) : Version option =
    try Some (AssemblyName.GetAssemblyName path).Version
    with _ -> None
