namespace SageFs

open System
open System.Security.Cryptography
open System.Text

/// The `sagefs` agent skill that ships inside the tool, and how an installed copy compares with it (#161).
///
/// The skill is a folder of Markdown the agent loads on every F# task. A copy that is missing, older than the daemon's build
/// or a different skill that happens to share the name sends agents to the wrong instructions, and nothing said so. The
/// version is a hash of the files, so there is no number to forget to bump: the shipped skill and an installed copy have the
/// same version exactly when they have the same files.
///
/// Everything here is pure. Reading the embedded files and the installed folder, and writing the install, are the CLI's job.
module SkillInstall =

  /// One file of the skill: its path relative to the skill folder, with `/` separators, and its text.
  type SkillFile = { Path: string; Content: string }

  /// Git may hand a Windows checkout CRLF line endings. The same skill is the same skill either way.
  let normalise (text: string) : string = text.Replace("\r\n", "\n")

  /// The skill's version: 12 hex characters over every file's path and text, in path order.
  let version (files: SkillFile list) : string =
    let material =
      files
      |> List.sortBy (fun file -> file.Path)
      |> List.map (fun file -> file.Path + "\u0001" + normalise file.Content)
      |> String.concat "\u0002"
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes material)).Substring(0, 12).ToLowerInvariant()

  /// The skill name a SKILL.md declares in its front matter, `None` when it has no `name:` line there.
  let nameOf (skillMarkdown: string) : string option =
    let lines = (normalise skillMarkdown).Split('\n')
    match lines |> Array.tryHead with
    | Some first when first.Trim() = "---" ->
      lines
      |> Array.skip 1
      |> Array.takeWhile (fun line -> line.Trim() <> "---")
      |> Array.tryPick (fun line ->
        match line.StartsWith("name:", StringComparison.Ordinal) with
        | true -> Some(line.Substring(5).Trim().Trim('"'))
        | false -> None)
    | _ -> None

  /// The name the skill goes by, and the file that declares it.
  [<Literal>]
  let SkillName = "sagefs"

  [<Literal>]
  let SkillEntryFile = "SKILL.md"

  /// How an installed copy compares with the one that ships.
  [<RequireQualifiedAccess>]
  type SkillStatus =
    /// Nothing is installed.
    | NotInstalled
    /// A folder is there, but it is not this skill: no SKILL.md, or one that names another skill.
    | NotThisSkill of reason: string
    /// The same files as the one that ships.
    | UpToDate of version: string
    /// This skill from another build, or with local edits: which files differ, are missing, or are extra.
    | Differs of installedVersion: string * changed: string list * missing: string list * extra: string list

  /// Compare the installed folder (`None` when it is not there) with the shipped files.
  let classify (shipped: SkillFile list) (installed: SkillFile list option) : SkillStatus =
    match installed with
    | None -> SkillStatus.NotInstalled
    | Some files ->
      match files |> List.tryFind (fun file -> file.Path = SkillEntryFile) with
      | None -> SkillStatus.NotThisSkill(sprintf "the folder has no %s" SkillEntryFile)
      | Some entry ->
        match nameOf entry.Content with
        | Some name when name = SkillName ->
          let shippedByPath = shipped |> List.map (fun file -> file.Path, normalise file.Content) |> Map.ofList
          let installedByPath = files |> List.map (fun file -> file.Path, normalise file.Content) |> Map.ofList
          let changed =
            shippedByPath
            |> Map.toList
            |> List.filter (fun (path, content) ->
              match Map.tryFind path installedByPath with
              | Some existing -> existing <> content
              | None -> false)
            |> List.map fst
          let missing = shippedByPath |> Map.toList |> List.map fst |> List.filter (fun path -> not (Map.containsKey path installedByPath))
          let extra = installedByPath |> Map.toList |> List.map fst |> List.filter (fun path -> not (Map.containsKey path shippedByPath))
          match changed, missing, extra with
          | [], [], [] -> SkillStatus.UpToDate(version files)
          | _ -> SkillStatus.Differs(version files, changed, missing, extra)
        | Some other -> SkillStatus.NotThisSkill(sprintf "its %s names the skill '%s', not '%s'" SkillEntryFile other SkillName)
        | None -> SkillStatus.NotThisSkill(sprintf "its %s names no skill" SkillEntryFile)

  /// What `install-skill` does about a status.
  [<RequireQualifiedAccess>]
  type InstallPlan =
    /// Write the shipped files into the folder.
    | Write
    /// Already current; nothing to do.
    | AlreadyCurrent
    /// Another skill is in the way. Names why, and that `--force` replaces it.
    | Refuse of reason: string

  /// Decide what to do. This skill from another build is replaced, since that is what the command is for. A folder that is
  /// not this skill is left alone unless `force` says to replace it.
  let plan (force: bool) (status: SkillStatus) : InstallPlan =
    match status with
    | SkillStatus.NotInstalled -> InstallPlan.Write
    | SkillStatus.UpToDate _ -> InstallPlan.AlreadyCurrent
    | SkillStatus.Differs _ -> InstallPlan.Write
    | SkillStatus.NotThisSkill reason ->
      match force with
      | true -> InstallPlan.Write
      | false -> InstallPlan.Refuse(sprintf "A folder named %s is already there and it is not this skill: %s. Use --force to replace it." SkillName reason)

  /// One line for `sagefs check` and `install-skill`, saying where the install stands and the one command that fixes it.
  let describe (shippedVersion: string) (status: SkillStatus) : string =
    let fix = "Run `sagefs install-skill` to install it."
    match status with
    | SkillStatus.NotInstalled -> sprintf "The '%s' agent skill is not installed. Agents that load it by name get an error or an older skill. %s" SkillName fix
    | SkillStatus.UpToDate version -> sprintf "The '%s' agent skill is installed and matches this build (%s)." SkillName version
    | SkillStatus.Differs(installedVersion, changed, missing, extra) ->
      let parts =
        [ yield! (match changed with | [] -> [] | paths -> [ sprintf "%d changed" paths.Length ])
          yield! (match missing with | [] -> [] | paths -> [ sprintf "%d missing" paths.Length ])
          yield! (match extra with | [] -> [] | paths -> [ sprintf "%d extra" paths.Length ]) ]
      sprintf "The '%s' agent skill is installed but is not this build's (installed %s, this build %s; %s). Run `sagefs install-skill` to replace it." SkillName installedVersion shippedVersion (String.concat ", " parts)
    | SkillStatus.NotThisSkill reason ->
      sprintf "A '%s' folder is installed but it is not this skill: %s. Run `sagefs install-skill --force` to replace it." SkillName reason

/// The edges of `SkillInstall`: the files that ship inside the tool, the folder an agent reads, and the write.
module SkillFiles =
  open System.IO
  open System.Reflection

  /// The resource-name prefix the skill's files are embedded under (`skill/<path>`, see SageFs.Core.fsproj).
  [<Literal>]
  let ResourcePrefix = "skill/"

  /// The skill that ships inside this build, sorted by path. Empty only if the build embedded nothing.
  let shipped () : SkillInstall.SkillFile list =
    let assembly = typeof<SkillInstall.SkillFile>.Assembly
    assembly.GetManifestResourceNames()
    |> Array.filter (fun name -> name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
    |> Array.map (fun name ->
      use stream = assembly.GetManifestResourceStream name
      use reader = new StreamReader(stream, Text.Encoding.UTF8)
      { SkillInstall.SkillFile.Path = name.Substring(ResourcePrefix.Length).Replace('\\', '/')
        SkillInstall.SkillFile.Content = reader.ReadToEnd() })
    |> Array.sortBy (fun file -> file.Path)
    |> List.ofArray

  /// Where Claude Code looks for a user's skills.
  let defaultRoot () : string =
    Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".claude", "skills")

  /// The skill folder under a skills root.
  let skillDirectory (root: string) : string = Path.Combine(root, SkillInstall.SkillName)

  /// Where a folder is a link to, `None` when it is a real folder or is not there. A link is left alone: it points at someone's
  /// checkout, and writing through it would overwrite their files.
  let linkTarget (directory: string) : string option =
    match DirectoryInfo(directory).LinkTarget with
    | null -> None
    | target -> Some target

  /// The files of an installed skill folder, `None` when there is no such folder. Files that cannot be read as text are skipped.
  let readInstalled (directory: string) : SkillInstall.SkillFile list option =
    match Directory.Exists directory with
    | false -> None
    | true ->
      Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
      |> Seq.choose (fun path ->
        try
          Some
            { SkillInstall.SkillFile.Path = Path.GetRelativePath(directory, path).Replace('\\', '/')
              SkillInstall.SkillFile.Content = File.ReadAllText path }
        with
        | :? IOException
        | :? UnauthorizedAccessException -> None)
      |> List.ofSeq
      |> Some

  /// Replace the folder with exactly these files.
  let write (directory: string) (files: SkillInstall.SkillFile list) : unit =
    if Directory.Exists directory then Directory.Delete(directory, true)
    for file in files do
      let path = Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar))
      Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
      File.WriteAllText(path, file.Content)
