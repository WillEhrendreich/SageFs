namespace SageFs

open System.IO

/// `sagefs install-skill [--force] [--dir <skills folder>]` (#161): write the `sagefs` agent skill that ships in this build into
/// the user's skills folder, so the install is one command and the copy matches the daemon.
module SkillInstallCommand =

  /// Where the skills folder is, as the arguments said.
  [<RequireQualifiedAccess>]
  type DirArgument =
    /// No `--dir`: the user's Claude Code skills folder.
    | NoDir
    | Dir of path: string
    /// `--dir` was the last argument, with no path after it.
    | MissingValue

  type InstallArgs = { Force: bool; Dir: DirArgument }

  /// The arguments after the command name.
  let parseArgs (args: string list) : InstallArgs =
    let dir =
      match List.tryFindIndex ((=) "--dir") args with
      | None -> DirArgument.NoDir
      | Some index ->
        match List.tryItem (index + 1) args with
        | Some path when not (path.StartsWith("--", System.StringComparison.Ordinal)) -> DirArgument.Dir path
        | _ -> DirArgument.MissingValue
    { Force = List.contains "--force" args; Dir = dir }

  /// Run the command and return its exit code.
  let run (args: InstallArgs) : int =
    match args.Dir, SkillFiles.shipped () with
    | DirArgument.MissingValue, _ ->
      eprintfn "sagefs install-skill: --dir needs the skills folder to install into, for example --dir ~/.claude/skills."
      1
    | _, [] ->
      eprintfn "sagefs install-skill: this build of SageFs carries no skill to install. Install a released SageFs, or read skills/sagefs/SKILL.md from the repository."
      1
    | dirArgument, shipped ->
      let root =
        match dirArgument with
        | DirArgument.Dir path -> path
        | DirArgument.NoDir
        | DirArgument.MissingValue -> SkillFiles.defaultRoot ()
      let directory = SkillFiles.skillDirectory root
      let shippedVersion = SkillInstall.version shipped
      let status = SkillInstall.classify shipped (SkillFiles.readInstalled directory)
      match SkillFiles.linkTarget directory with
      | Some target ->
        printfn "%s is a link to %s, so it was left alone and it follows that folder." directory target
        printfn "%s" (SkillInstall.describe shippedVersion status)
        0
      | None ->
        match SkillInstall.plan args.Force status with
        | SkillInstall.InstallPlan.AlreadyCurrent ->
          printfn "%s" (SkillInstall.describe shippedVersion status)
          0
        | SkillInstall.InstallPlan.Refuse reason ->
          eprintfn "sagefs install-skill: %s" reason
          1
        | SkillInstall.InstallPlan.Write ->
          try
            SkillFiles.write directory shipped
            printfn "Installed the '%s' agent skill (%s, %d files) into %s." SkillInstall.SkillName shippedVersion shipped.Length directory
            match status with
            | SkillInstall.SkillStatus.Differs(installedVersion, _, _, _) ->
              printfn "It replaced the copy that was there (%s). Files in that folder that this build does not ship were removed." installedVersion
            | SkillInstall.SkillStatus.NotThisSkill reason -> printfn "It replaced a folder that was not this skill (%s)." reason
            | SkillInstall.SkillStatus.NotInstalled
            | SkillInstall.SkillStatus.UpToDate _ -> ()
            0
          with
          | :? IOException as ex ->
            eprintfn "sagefs install-skill: could not write %s: %s" directory ex.Message
            1
          | :? System.UnauthorizedAccessException as ex ->
            eprintfn "sagefs install-skill: could not write %s: %s" directory ex.Message
            1
