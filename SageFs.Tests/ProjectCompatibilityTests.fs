module SageFs.Tests.ProjectCompatibilityTests

open Expecto
open Expecto.Flip
open SageFs.ProjectCompatibility

let private fsprojWithTfm (tfm: string) =
  sprintf
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>%s</TargetFramework>
  </PropertyGroup>
</Project>"""
    tfm

let private fsprojWithTfms (tfms: string) =
  sprintf
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>%s</TargetFrameworks>
  </PropertyGroup>
</Project>"""
    tfms

let private fsprojWithNoTfm =
  """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>"""

[<Tests>]
let tests =
  testList "ProjectCompatibility" [

    testCase "WHY — net10.0 is hostable because it's a modern .NET (Core) TFM the FSI host can load" <| fun () ->
      classify (fsprojWithTfm "net10.0")
      |> Expect.equal "should be Hostable net10.0" (ProjectHostability.Hostable "net10.0")

    testCase "WHY — net8.0 is hostable because it's a modern .NET (Core) TFM, not just the host's own version" <| fun () ->
      classify (fsprojWithTfm "net8.0")
      |> Expect.equal "should be Hostable net8.0" (ProjectHostability.Hostable "net8.0")

    testCase "WHY — net48 is NOT hostable because the FSI host runs on modern .NET and cannot load .NET Framework assemblies" <| fun () ->
      classify (fsprojWithTfm "net48")
      |> Expect.equal "should be NotHostable" (ProjectHostability.NotHostable([ "net48" ], UnsupportedTfmReason.NetFramework))

    testCase "WHY — net472 is NOT hostable for the same .NET Framework reason as net48" <| fun () ->
      classify (fsprojWithTfm "net472")
      |> Expect.equal "should be NotHostable" (ProjectHostability.NotHostable([ "net472" ], UnsupportedTfmReason.NetFramework))

    testCase "WHY — netstandard2.0 alone is Indeterminate, never a refusal, because hostability depends on what loads it and this classifier can't see that" <| fun () ->
      match classify (fsprojWithTfm "netstandard2.0") with
      | ProjectHostability.Indeterminate _ -> ()
      | other -> failtestf "expected Indeterminate, got %A" other

    testCase "WHY — multi-target net48;net8.0 is hostable via net8.0 because ANY supported TFM makes the whole project hostable" <| fun () ->
      classify (fsprojWithTfms "net48;net8.0")
      |> Expect.equal "should be Hostable net8.0" (ProjectHostability.Hostable "net8.0")

    testCase "WHY — multi-target net47;net48 is NOT hostable because every declared TFM is confidently .NET Framework" <| fun () ->
      match classify (fsprojWithTfms "net47;net48") with
      | ProjectHostability.NotHostable(tfms, UnsupportedTfmReason.NetFramework) ->
        tfms |> List.sort |> Expect.equal "should list both TFMs" [ "net47"; "net48" ]
      | other -> failtestf "expected NotHostable(NetFramework), got %A" other

    testCase "WHY — a missing <TargetFramework> element is Indeterminate, never a refusal, because we can't say anything without it" <| fun () ->
      match classify fsprojWithNoTfm with
      | ProjectHostability.Indeterminate _ -> ()
      | other -> failtestf "expected Indeterminate, got %A" other

    testCase "WHY — malformed XML is Indeterminate, never a refusal, because a false positive refusal is worse than falling through" <| fun () ->
      match classify "<Project><TargetFramework>net48</TargetFramework" with
      | ProjectHostability.Indeterminate _ -> ()
      | other -> failtestf "expected Indeterminate, got %A" other

    testCase "WHY — an empty file is Indeterminate, never a refusal, same conservative rule as malformed XML" <| fun () ->
      match classify "" with
      | ProjectHostability.Indeterminate _ -> ()
      | other -> failtestf "expected Indeterminate, got %A" other

    testCase "WHY — findUnhostable returns None when every project is Hostable, so a normal net10.0 session is never touched" <| fun () ->
      let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), sprintf "sagefs-pc-test-%s" (System.Guid.NewGuid().ToString("N")))
      System.IO.Directory.CreateDirectory dir |> ignore
      try
        let path = System.IO.Path.Combine(dir, "App.fsproj")
        System.IO.File.WriteAllText(path, fsprojWithTfm "net10.0")
        findUnhostable [ path ]
        |> Expect.isNone "should find no unhostable project"
      finally
        System.IO.Directory.Delete(dir, true)

    testCase "WHY — findUnhostable reports the offending project's path and TFM when one is confidently NET Framework" <| fun () ->
      let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), sprintf "sagefs-pc-test-%s" (System.Guid.NewGuid().ToString("N")))
      System.IO.Directory.CreateDirectory dir |> ignore
      try
        let path = System.IO.Path.Combine(dir, "Legacy.fsproj")
        System.IO.File.WriteAllText(path, fsprojWithTfm "net48")
        match findUnhostable [ path ] with
        | Some(project, tfms, UnsupportedTfmReason.NetFramework) ->
          project |> Expect.equal "should be the offending project's path" path
          tfms |> Expect.equal "should carry the declared TFM" [ "net48" ]
        | other -> failtestf "expected Some(path, [net48], NetFramework), got %A" other
      finally
        System.IO.Directory.Delete(dir, true)

    testCase "WHY — findUnhostable treats an unreadable path as Indeterminate, never a refusal" <| fun () ->
      findUnhostable [ "/nonexistent/does-not-exist/Foo.fsproj" ]
      |> Expect.isNone "an unreadable project must not be blocked"

    testCase "WHY — describeUnhostable names the project, the TFM found, and why, without speculating or blaming the user" <| fun () ->
      let message = describeUnhostable "/src/Legacy.fsproj" [ "net48" ] UnsupportedTfmReason.NetFramework
      message |> Expect.stringContains "should name the project" "/src/Legacy.fsproj"
      message |> Expect.stringContains "should name the TFM found" "net48"
      message |> Expect.stringContains "should explain the FSI host constraint" "cannot load .NET Framework assemblies"
  ]
