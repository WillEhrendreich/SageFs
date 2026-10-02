module SageFs.Tests.CoreInjectionTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

/// Which SageFs.Core a session's build compiles against: the daemon's own copy for a project with none
/// of its own, and the project's for one in a repo that builds SageFs.Core. The outcome gate
/// (SelfHostCoreOutcomeTests) proves it through a real daemon; these pin the decision and the evidence.

let private daemonCore = SessionBuild.CoreReference.Available "/opt/sagefs/SageFs.Core.dll"

let private ownCoreByReference =
  CoreEvidence.Evidence.BringsOwnCore (CoreEvidence.OwnCore.ProjectReference ("/r/SageFs/SageFs.fsproj", "/r/SageFs.Core/SageFs.Core.fsproj"))

/// A throwaway directory of project files.
let private withProjects (files: (string * string) list) (body: string -> unit) : unit =
  let root = Directory.CreateTempSubdirectory("sagefs-evidence-").FullName
  try
    for (relative, text) in files do
      let path = Path.Combine(root, relative)
      Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
      File.WriteAllText(path, text)
    body root
  finally
    try Directory.Delete(root, true) with _ -> ()

let private project (inner: string) = sprintf "<Project Sdk=\"Microsoft.NET.Sdk\">\n%s\n</Project>" inner

let private projectReference (include': string) =
  sprintf "  <ItemGroup>\n    <ProjectReference Include=\"%s\" />\n  </ItemGroup>" include'

let private coreProject = project "  <PropertyGroup><AssemblyName>SageFs.Core</AssemblyName></PropertyGroup>"

let private labelOf (evidence: CoreEvidence.Evidence) : string =
  match evidence with
  | CoreEvidence.Evidence.BringsOwnCore (CoreEvidence.OwnCore.IsCore _) -> "IsCore"
  | CoreEvidence.Evidence.BringsOwnCore (CoreEvidence.OwnCore.ProjectReference _) -> "ProjectReference"
  | CoreEvidence.Evidence.BringsOwnCore (CoreEvidence.OwnCore.ExplicitReference _) -> "ExplicitReference"
  | CoreEvidence.Evidence.BringsNone _ -> "BringsNone"
  | CoreEvidence.Evidence.Unreadable _ -> "Unreadable"

[<Tests>]
let coreInjectionTests =
  testList "Core injection" [

    testList "decideInjection" [
      testCase "a project that brings its own SageFs.Core never gets the daemon's, even though the daemon has one" <| fun _ ->
        match SessionBuild.decideInjection daemonCore ownCoreByReference with
        | SessionBuild.InjectionDecision.UseProjectCore _ -> ()
        | other -> failtestf "expected the project's own Core, got %A" other

      testCase "a project with no SageFs.Core of its own gets the daemon's, from the daemon's path" <| fun _ ->
        SessionBuild.decideInjection daemonCore (CoreEvidence.Evidence.BringsNone [ "/p/App.fsproj" ])
        |> Expect.equal "the daemon's Core" (SessionBuild.InjectionDecision.InjectDaemonCore "/opt/sagefs/SageFs.Core.dll")

      testCase "a project that cannot be read is refused with its path and the reason, never given the daemon's Core" <| fun _ ->
        match SessionBuild.decideInjection daemonCore (CoreEvidence.Evidence.Unreadable ("/p/App.fsproj", "the project file does not exist")) with
        | SessionBuild.InjectionDecision.Refuse because ->
          because |> Expect.stringContains "names the project" "/p/App.fsproj"
          because |> Expect.stringContains "names the reason" "the project file does not exist"
        | other -> failtestf "expected a refusal, got %A" other

      testCase "with no daemon Core to give, a project with none of its own builds without, and says why" <| fun _ ->
        match SessionBuild.decideInjection (SessionBuild.CoreReference.Absent "not on disk") (CoreEvidence.Evidence.BringsNone []) with
        | SessionBuild.InjectionDecision.NothingToInject because -> because |> Expect.stringContains "says why" "not on disk"
        | other -> failtestf "expected nothing to inject, got %A" other

      testCase "own-Core evidence wins over every state of the daemon's Core" <| fun _ ->
        for daemon in [ daemonCore; SessionBuild.CoreReference.Absent "x"; SessionBuild.CoreReference.NotChecked ] do
          match SessionBuild.decideInjection daemon ownCoreByReference with
          | SessionBuild.InjectionDecision.UseProjectCore _ -> ()
          | other -> failtestf "%A: expected the project's own Core, got %A" daemon other
    ]

    testList "evidenceFor" [
      testCase "a project that is SageFs.Core by assembly name is its own Core" <| fun _ ->
        withProjects [ "Core/Engine.fsproj", coreProject ] (fun root ->
          CoreEvidence.evidenceFor (Path.Combine(root, "Core", "Engine.fsproj")) |> labelOf |> Expect.equal "IsCore" "IsCore")

      testCase "a project that is SageFs.Core by file name is its own Core" <| fun _ ->
        withProjects [ "SageFs.Core/SageFs.Core.fsproj", project "" ] (fun root ->
          CoreEvidence.evidenceFor (Path.Combine(root, "SageFs.Core", "SageFs.Core.fsproj")) |> labelOf |> Expect.equal "IsCore" "IsCore")

      testCase "a ProjectReference to a SageFs.Core project is evidence, with either slash" <| fun _ ->
        for include' in [ "..\\SageFs.Core\\SageFs.Core.fsproj"; "../SageFs.Core/SageFs.Core.fsproj" ] do
          withProjects [ "App/App.fsproj", project (projectReference include'); "SageFs.Core/SageFs.Core.fsproj", project "" ] (fun root ->
            CoreEvidence.evidenceFor (Path.Combine(root, "App", "App.fsproj")) |> labelOf |> Expect.equal include' "ProjectReference")

      testCase "a ProjectReference reached through another project is evidence" <| fun _ ->
        withProjects
          [ "Tests/Tests.fsproj", project (projectReference "../App/App.fsproj")
            "App/App.fsproj", project (projectReference "../SageFs.Core/SageFs.Core.fsproj")
            "SageFs.Core/SageFs.Core.fsproj", project "" ]
          (fun root ->
            CoreEvidence.evidenceFor (Path.Combine(root, "Tests", "Tests.fsproj")) |> labelOf |> Expect.equal "through App" "ProjectReference")

      testCase "a reference written with MSBuildThisFileDirectory is followed" <| fun _ ->
        withProjects
          [ "Tests/Tests.fsproj", project (projectReference "$(MSBuildThisFileDirectory)../App/App.fsproj")
            "App/App.fsproj", project (projectReference "../SageFs.Core/SageFs.Core.fsproj")
            "SageFs.Core/SageFs.Core.fsproj", project "" ]
          (fun root ->
            CoreEvidence.evidenceFor (Path.Combine(root, "Tests", "Tests.fsproj")) |> labelOf |> Expect.equal "followed" "ProjectReference")

      testCase "a Reference the project wrote to SageFs.Core is evidence, with or without a version" <| fun _ ->
        for name in [ "SageFs.Core"; "SageFs.Core, Version=0.6.0.0" ] do
          withProjects [ "App/App.fsproj", project (sprintf "  <ItemGroup>\n    <Reference Include=\"%s\"><HintPath>x.dll</HintPath></Reference>\n  </ItemGroup>" name) ] (fun root ->
            CoreEvidence.evidenceFor (Path.Combine(root, "App", "App.fsproj")) |> labelOf |> Expect.equal name "ExplicitReference")

      testCase "a project with no reference to SageFs.Core brings none, and says which files it read" <| fun _ ->
        withProjects [ "App/App.fsproj", project (projectReference "../Lib/Lib.fsproj"); "Lib/Lib.fsproj", project "" ] (fun root ->
          match CoreEvidence.evidenceFor (Path.Combine(root, "App", "App.fsproj")) with
          | CoreEvidence.Evidence.BringsNone read -> read |> List.map Path.GetFileName |> Expect.equal "App then Lib" [ "App.fsproj"; "Lib.fsproj" ]
          | other -> failtestf "expected none, got %A" other)

      testCase "a SageFs.Core.dll in the project's bin is NOT evidence, because an earlier injected build put it there" <| fun _ ->
        withProjects [ "App/App.fsproj", project ""; "App/bin/Debug/net10.0/SageFs.Core.dll", "not a real dll" ] (fun root ->
          CoreEvidence.evidenceFor (Path.Combine(root, "App", "App.fsproj")) |> labelOf |> Expect.equal "the dll vouches for nothing" "BringsNone")

      testCase "a project file that is missing is Unreadable and names itself" <| fun _ ->
        withProjects [] (fun root ->
          match CoreEvidence.evidenceFor (Path.Combine(root, "Nope.fsproj")) with
          | CoreEvidence.Evidence.Unreadable (path, reason) ->
            path |> Expect.stringContains "names the file" "Nope.fsproj"
            reason |> Expect.isNotEmpty "and why"
          | other -> failtestf "expected Unreadable, got %A" other)

      testCase "a referenced project that is missing makes the answer Unreadable, because the Core could be behind it" <| fun _ ->
        withProjects [ "App/App.fsproj", project (projectReference "../Gone/Gone.fsproj") ] (fun root ->
          CoreEvidence.evidenceFor (Path.Combine(root, "App", "App.fsproj")) |> labelOf |> Expect.equal "unreadable" "Unreadable")

      testCase "a project file that is not XML is Unreadable" <| fun _ ->
        withProjects [ "App/App.fsproj", "this is not xml" ] (fun root ->
          CoreEvidence.evidenceFor (Path.Combine(root, "App", "App.fsproj")) |> labelOf |> Expect.equal "unreadable" "Unreadable")

      testCase "a reference cycle ends, and finds the Core on the other side of it" <| fun _ ->
        withProjects
          [ "A/A.fsproj", project (projectReference "../B/B.fsproj")
            "B/B.fsproj", project (projectReference "../A/A.fsproj" + "\n" + projectReference "../SageFs.Core/SageFs.Core.fsproj")
            "SageFs.Core/SageFs.Core.fsproj", project "" ]
          (fun root ->
            CoreEvidence.evidenceFor (Path.Combine(root, "A", "A.fsproj")) |> labelOf |> Expect.equal "found past the cycle" "ProjectReference")

      testCase "a solution is judged by the projects it lists: one member that brings its own Core is enough" <| fun _ ->
        withProjects
          [ "Sol.slnx", "<Solution>\n  <Project Path=\"Lib/Lib.fsproj\" />\n  <Project Path=\"App/App.fsproj\" />\n</Solution>"
            "Lib/Lib.fsproj", project ""
            "App/App.fsproj", project (projectReference "../SageFs.Core/SageFs.Core.fsproj")
            "SageFs.Core/SageFs.Core.fsproj", project "" ]
          (fun root ->
            CoreEvidence.evidenceFor (Path.Combine(root, "Sol.slnx")) |> labelOf |> Expect.equal "slnx" "ProjectReference")

      testCase "a classic .sln lists its projects the same way" <| fun _ ->
        withProjects
          [ "Sol.sln", "Project(\"{F2A71F9B-5D33-465A-A702-920D77279786}\") = \"App\", \"App\\App.fsproj\", \"{1}\"\nEndProject\n"
            "App/App.fsproj", project (projectReference "../SageFs.Core/SageFs.Core.fsproj")
            "SageFs.Core/SageFs.Core.fsproj", project "" ]
          (fun root ->
            CoreEvidence.evidenceFor (Path.Combine(root, "Sol.sln")) |> labelOf |> Expect.equal "sln" "ProjectReference")
    ]

    testList "referencedProjects" [
      testCase "lists the projects a project references, directly and through others, once each, never itself" <| fun _ ->
        withProjects
          [ "A/A.fsproj", project (projectReference "../B/B.fsproj" + "\n" + projectReference "../C/C.fsproj")
            "B/B.fsproj", project (projectReference "../C/C.fsproj" + "\n" + projectReference "../A/A.fsproj")
            "C/C.fsproj", project "" ]
          (fun root ->
            CoreEvidence.referencedProjects (Path.Combine(root, "A", "A.fsproj"))
            |> List.map Path.GetFileName
            |> List.sort
            |> Expect.equal "B and C" [ "B.fsproj"; "C.fsproj" ])
    ]
  ]
