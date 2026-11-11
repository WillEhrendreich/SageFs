module SageFs.Tests.OrphanSourceTests

open System.IO
open System.Xml.Linq
open Expecto
open Expecto.Flip

// A `.fs` file that no project compiles is dead text that reads like code: `FsiRewrite.fs` and `FsiCompatibility.fs` sat in
// SageFs.Core for weeks after the fsproj stopped compiling them, and the only thing that knew was a reflection test that
// asserted their types were absent. This rule finds them by the file system, so a file that is dropped from a project, or added
// without being included, fails here.

/// The product projects, each with the source files it may keep on disk without compiling. The list starts empty and a file
/// is added to it only with the reason it stays.
let projects : (string * string list) list =
  [ "SageFs.Core", []
    "SageFs", []
    "SageFs.Host", []
    "SageFs.Simulation", [] ]

let normalize (path: string) : string = path.Replace('\\', '/')

/// The files a project compiles, relative to the project directory.
let compiled (projectDir: string) (projectName: string) : Set<string> =
  let fsproj = Path.Combine(projectDir, projectName + ".fsproj")
  XDocument.Load(fsproj).Descendants(XName.Get "Compile")
  |> Seq.choose (fun element ->
    match element.Attribute(XName.Get "Include") with
    | null -> None
    | include' -> Some (normalize include'.Value))
  |> Set.ofSeq

/// Every `.fs` file under the project directory, outside the build output.
let onDisk (projectDir: string) : Set<string> =
  Directory.EnumerateFiles(projectDir, "*.fs", SearchOption.AllDirectories)
  |> Seq.map (fun file -> normalize (Path.GetRelativePath(projectDir, file)))
  |> Seq.filter (fun relative -> not (relative.StartsWith "obj/" || relative.StartsWith "bin/"))
  |> Set.ofSeq

/// The files on disk that the project does not compile and that are not on its allow-list.
let orphans (projectName: string) (allowed: string list) : string list =
  let projectDir = RepoPaths.repoPath [| projectName |]
  Set.difference (onDisk projectDir) (compiled projectDir projectName)
  |> Set.filter (fun file -> not (List.contains file allowed))
  |> Set.toList

[<Tests>]
let orphanSourceTests =
  testList "orphan source files" [
    for (projectName, allowed) in projects do
      testCase (sprintf "WHY — every .fs file under %s is compiled by %s.fsproj, or is on the allow-list with a reason" projectName projectName) <| fun _ ->
        orphans projectName allowed
        |> Expect.isEmpty (sprintf "no source file under %s is left out of the project (delete it, or include it)" projectName)

    testCase "the rule sees an orphan: a file on disk that the project list lacks is reported" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "orphan-rule-" + System.Guid.NewGuid().ToString("N"))
      Directory.CreateDirectory dir |> ignore
      try
        File.WriteAllText(Path.Combine(dir, "Kept.fs"), "module Kept")
        File.WriteAllText(Path.Combine(dir, "Left.fs"), "module Left")
        File.WriteAllText(Path.Combine(dir, "P.fsproj"), """<Project><ItemGroup><Compile Include="Kept.fs" /></ItemGroup></Project>""")
        Set.difference (onDisk dir) (compiled dir "P") |> Set.toList |> Expect.equal "only the file the project lacks" [ "Left.fs" ]
      finally
        Directory.Delete(dir, true)
  ]
