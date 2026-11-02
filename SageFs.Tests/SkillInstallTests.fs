module SageFs.Tests.SkillInstallTests

/// #161: an agent can end up on a different `sagefs` skill than the build it runs against, or none, and nothing said so. The
/// skill ships inside the tool, its version is a hash of its files, and `sagefs check` and `sagefs install-skill` compare an
/// installed copy with it. The comparison is pure.

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.SkillInstall
open SageFs.SkillInstallCommand
open SageFs.Tests.TestInfrastructure

let private file (path: string) (content: string) : SkillFile = { Path = path; Content = content }

let private skillMd (name: string) = sprintf "---\nname: %s\ndescription: \"x\"\n---\n\n# Body\n" name

let private shipped =
  [ file "SKILL.md" (skillMd "sagefs")
    file "design.md" "# Design\n"
    file "loop.md" "# Loop\n" ]

[<Tests>]
let tests =
  testList "SkillInstall" [
    testList "version" [
      testCase "WHY — the version is the same for the same files in any order, so listing order never reads as a change" <| fun _ ->
        version shipped |> Expect.equal "order does not matter" (version (List.rev shipped))

      testCase "WHY — a CRLF checkout of the same skill has the same version as the LF one" <| fun _ ->
        let crlf = shipped |> List.map (fun f -> { f with Content = f.Content.Replace("\n", "\r\n") })
        version crlf |> Expect.equal "line endings are not content" (version shipped)

      testCase "WHY — any change to any file, or a renamed file, changes the version" <| fun _ ->
        let edited = shipped |> List.map (fun f -> if f.Path = "loop.md" then { f with Content = "# Loop, edited\n" } else f)
        let renamed = shipped |> List.map (fun f -> if f.Path = "loop.md" then { f with Path = "loops.md" } else f)
        version edited |> Expect.notEqual "edited" (version shipped)
        version renamed |> Expect.notEqual "renamed" (version shipped)

      testCase "the version is twelve lowercase hex characters, safe to print and to compare" <| fun _ ->
        let v = version shipped
        v.Length |> Expect.equal "twelve" 12
        v |> Seq.forall (fun c -> System.Char.IsAsciiHexDigitLower c) |> Expect.isTrue "hex"

      testProperty "WHY — two skills share a version exactly when they have the same files, over random file sets" <| fun () ->
        let generate =
          Gen.listOf (Gen.zip (Gen.elements [ "a.md"; "b.md"; "c.md" ]) (Gen.elements [ "x"; "y"; "z" ]))
          |> Gen.map (fun pairs -> pairs |> List.distinctBy fst |> List.map (fun (p, c) -> file p c))
        Prop.forAll (Arb.fromGen (Gen.zip generate generate)) (fun (a, b) ->
          let same = (a |> List.sortBy (fun f -> f.Path)) = (b |> List.sortBy (fun f -> f.Path))
          (version a = version b) = same)
    ]

    testList "nameOf" [
      testCase "reads the name from the front matter, quoted or not" <| fun _ ->
        nameOf "---\nname: sagefs\n---\n" |> Expect.equal "plain" (Some "sagefs")
        nameOf "---\nname: \"sagefs\"\n---\n" |> Expect.equal "quoted" (Some "sagefs")
        nameOf "---\r\nname: sagefs\r\n---\r\n" |> Expect.equal "CRLF" (Some "sagefs")

      testCase "WHY — a name outside the front matter is not the skill's name" <| fun _ ->
        nameOf "# Title\n\nname: sagefs\n" |> Expect.isNone "no front matter"
        nameOf "---\ndescription: x\n---\nname: sagefs\n" |> Expect.isNone "after the closing line"
    ]

    testList "classify" [
      testCase "nothing installed" <| fun _ ->
        classify shipped None |> Expect.equal "not installed" SkillStatus.NotInstalled

      testCase "WHY — the same files are up to date, and the status carries the version it matched" <| fun _ ->
        classify shipped (Some shipped) |> Expect.equal "up to date" (SkillStatus.UpToDate(version shipped))

      testCase "WHY — an older skill of the same name is Differs, and names the changed, missing and extra files" <| fun _ ->
        let installed =
          [ file "SKILL.md" (skillMd "sagefs" + "\nan older line\n")
            file "design.md" "# Design\n"
            file "old-only.md" "# Gone in this build\n" ]
        match classify shipped (Some installed) with
        | SkillStatus.Differs(installedVersion, changed, missing, extra) ->
          installedVersion |> Expect.equal "the installed copy's own version" (version installed)
          changed |> Expect.equal "SKILL.md changed" [ "SKILL.md" ]
          missing |> Expect.equal "loop.md is new" [ "loop.md" ]
          extra |> Expect.equal "old-only.md is gone from this build" [ "old-only.md" ]
        | other -> failtestf "expected Differs, got %A" other

      testCase "WHY — a folder with another skill's SKILL.md is not this skill, so it is never mistaken for a stale copy" <| fun _ ->
        match classify shipped (Some [ file "SKILL.md" (skillMd "sagefs-workflow") ]) with
        | SkillStatus.NotThisSkill reason -> reason |> Expect.stringContains "names the other skill" "sagefs-workflow"
        | other -> failtestf "expected NotThisSkill, got %A" other

      testCase "a folder with no SKILL.md, and one whose SKILL.md names nothing, are not this skill" <| fun _ ->
        (match classify shipped (Some [ file "notes.md" "x" ]) with SkillStatus.NotThisSkill _ -> true | _ -> false)
        |> Expect.isTrue "no SKILL.md"
        (match classify shipped (Some [ file "SKILL.md" "# no front matter\n" ]) with SkillStatus.NotThisSkill _ -> true | _ -> false)
        |> Expect.isTrue "no name"
    ]

    testList "plan" [
      testCase "WHY — a missing skill is written, a current one is left alone" <| fun _ ->
        plan false SkillStatus.NotInstalled |> Expect.equal "write" InstallPlan.Write
        plan false (SkillStatus.UpToDate "abc") |> Expect.equal "already current" InstallPlan.AlreadyCurrent

      testCase "WHY — this skill from another build is replaced, because that is what the command is for" <| fun _ ->
        plan false (SkillStatus.Differs("abc", [ "SKILL.md" ], [], [])) |> Expect.equal "write" InstallPlan.Write

      testCase "WHY — a different skill in the way is refused without --force, and the refusal says how to override" <| fun _ ->
        match plan false (SkillStatus.NotThisSkill "its SKILL.md names the skill 'other', not 'sagefs'") with
        | InstallPlan.Refuse reason ->
          reason |> Expect.stringContains "says why" "other"
          reason |> Expect.stringContains "says the way out" "--force"
        | other -> failtestf "expected Refuse, got %A" other
        plan true (SkillStatus.NotThisSkill "x") |> Expect.equal "--force replaces it" InstallPlan.Write
    ]

    testList "describe" [
      testCase "WHY — every state that needs action names the one command that fixes it" <| fun _ ->
        let v = version shipped
        describe v SkillStatus.NotInstalled |> Expect.stringContains "not installed" "sagefs install-skill"
        describe v (SkillStatus.Differs("old123", [ "a" ], [ "b"; "c" ], [])) |> Expect.stringContains "differs" "sagefs install-skill"
        describe v (SkillStatus.NotThisSkill "x") |> Expect.stringContains "other skill needs force" "install-skill --force"

      testCase "an installed current skill says so, with its version" <| fun _ ->
        let v = version shipped
        describe v (SkillStatus.UpToDate v) |> Expect.stringContains "matches" "matches this build"
        describe v (SkillStatus.UpToDate v) |> Expect.stringContains "carries the version" v

      testCase "a stale copy's line names both versions and how many files differ" <| fun _ ->
        let line = describe "newver123456" (SkillStatus.Differs("oldver123456", [ "SKILL.md" ], [ "x.md"; "y.md" ], [ "z.md" ]))
        line |> Expect.stringContains "installed version" "oldver123456"
        line |> Expect.stringContains "this build's version" "newver123456"
        line |> Expect.stringContains "counts" "1 changed, 2 missing, 1 extra"
    ]

    testList "the skill that ships inside the tool" [
      testCase "WHY — what ships is the repository's skills/sagefs folder, so an install is never a stale copy of it" <| fun _ ->
        let repoSkill = SkillFiles.readInstalled (RepoPaths.repoPathFull [| "skills"; "sagefs" |])
        repoSkill |> Expect.isSome "the repository has the skill"
        let embedded = SkillFiles.shipped ()
        embedded |> Expect.isNonEmpty "the build embeds the skill"
        classify embedded repoSkill |> Expect.equal "the embedded files are the repository's files" (SkillStatus.UpToDate(version embedded))

      testCase "the shipped entry file declares the sagefs skill" <| fun _ ->
        match SkillFiles.shipped () |> List.tryFind (fun f -> f.Path = SkillEntryFile) with
        | Some entry -> nameOf entry.Content |> Expect.equal "named for what agents load" (Some SkillName)
        | None -> failtest "SKILL.md is not embedded"

      testCase "WHY — install writes exactly the shipped files, reads back as current, and a second install has nothing to do" <| fun _ ->
        let root = System.IO.Directory.CreateTempSubdirectory("sagefs-skill-").FullName
        try
          let shipped = SkillFiles.shipped ()
          let directory = SkillFiles.skillDirectory root
          classify shipped (SkillFiles.readInstalled directory) |> Expect.equal "nothing there yet" SkillStatus.NotInstalled
          SkillFiles.write directory shipped
          let status = classify shipped (SkillFiles.readInstalled directory)
          status |> Expect.equal "installed and current" (SkillStatus.UpToDate(version shipped))
          plan false status |> Expect.equal "so a second run does nothing" InstallPlan.AlreadyCurrent
        finally
          System.IO.Directory.Delete(root, true)

      testCase "WHY — an older copy is replaced whole, so a file this build no longer ships does not linger" <| fun _ ->
        let root = System.IO.Directory.CreateTempSubdirectory("sagefs-skill-").FullName
        try
          let shipped = SkillFiles.shipped ()
          let directory = SkillFiles.skillDirectory root
          SkillFiles.write directory (file "old-only.md" "# gone\n" :: shipped)
          (match classify shipped (SkillFiles.readInstalled directory) with
           | SkillStatus.Differs(_, _, _, extra) -> extra |> Expect.equal "the stale file shows as extra" [ "old-only.md" ]
           | other -> failtestf "expected Differs, got %A" other)
          SkillFiles.write directory shipped
          classify shipped (SkillFiles.readInstalled directory) |> Expect.equal "now exactly the shipped files" (SkillStatus.UpToDate(version shipped))
        finally
          System.IO.Directory.Delete(root, true)

      testCase "WHY — a skill folder that is a link is recognised, so install never writes through it into someone's checkout" <| fun _ ->
        let root = System.IO.Directory.CreateTempSubdirectory("sagefs-skill-").FullName
        try
          let target = System.IO.Path.Combine(root, "checkout", "skills", "sagefs")
          SkillFiles.write target (SkillFiles.shipped ())
          let skills = System.IO.Path.Combine(root, "skills")
          System.IO.Directory.CreateDirectory skills |> ignore
          let link = SkillFiles.skillDirectory skills
          System.IO.Directory.CreateSymbolicLink(link, target) |> ignore
          SkillFiles.linkTarget link |> Expect.equal "the link names its target" (Some target)
          SkillFiles.linkTarget target |> Expect.isNone "a real folder is not a link"
          SkillFiles.linkTarget (System.IO.Path.Combine(root, "nothing-here")) |> Expect.isNone "nor is a folder that is not there"
        finally
          System.IO.Directory.Delete(root, true)
    ]

    testList "install-skill arguments" [
      testCase "WHY — --force and --dir are read wherever they sit, and no flags means the defaults" <| fun _ ->
        SageFs.SkillInstallCommand.parseArgs []
        |> Expect.equal "defaults" { Force = false; Dir = SageFs.SkillInstallCommand.DirArgument.NoDir }
        SageFs.SkillInstallCommand.parseArgs [ "--dir"; "/x/skills"; "--force" ]
        |> Expect.equal "both" { Force = true; Dir = SageFs.SkillInstallCommand.DirArgument.Dir "/x/skills" }

      testCase "WHY — a --dir with no folder after it is reported, never ignored into the default folder" <| fun _ ->
        (SageFs.SkillInstallCommand.parseArgs [ "--dir" ]).Dir |> Expect.equal "last argument" SageFs.SkillInstallCommand.DirArgument.MissingValue
        (SageFs.SkillInstallCommand.parseArgs [ "--dir"; "--force" ]).Dir |> Expect.equal "followed by a flag" SageFs.SkillInstallCommand.DirArgument.MissingValue
    ]
  ]
