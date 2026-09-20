/// Tests for project classification — the path that decides whether a session's
/// projects are a web app, and therefore whether the user is offered the
/// hot-reload workflow.
///
/// Two defects motivated this file:
///
/// 1. The "web" marker list was duplicated: `ProjectKind.classify`'s
///    `webPackages` and `WorkflowDetection.suggest`'s own near-copy. Two lists
///    that must agree, don't, and nothing forced them to. They HAD already
///    drifted — `StarFederation.Datastar` was only in the detection copy, so a
///    raw-Datastar (non-Falco) project was told "web, switch to hot reload"
///    while `ProjectKind` called it a console app.
///
/// 2. Both lists were PackageReference-only. A modern ASP.NET Core / Minimal API
///    project gets ASP.NET from `Sdk="Microsoft.NET.Sdk.Web"` plus a
///    `<FrameworkReference Include="Microsoft.AspNetCore.App" />` — NOT from any
///    `<PackageReference>`. Verified in-tree against this repo's own
///    `SageFs.Tests/fixtures/WebAppFixture/WebAppFixture.fsproj`, which the live
///    daemon reports with `PackageRefs: []`. So every plain ASP.NET, Minimal API
///    and Oxpecker project classified as Console and was never offered hot reload.
///
/// The drift test below is the structural guard: it iterates the ONE shared
/// marker list, so a marker added to `WebMarkers` is automatically required to
/// behave identically on both classification paths.
module SageFs.Tests.ProjectClassificationTests

open Expecto
open Expecto.Flip
open SageFs.WorkflowTypes

// ─── Fixture XML ────────────────────────────────────────────

/// The canonical modern ASP.NET Core / Minimal API shape: Web SDK, no
/// PackageReference at all. This is byte-for-byte the shape of this repo's own
/// WebAppFixture.
let private minimalApiFsproj =
  """<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.fs" />
  </ItemGroup>
</Project>"""

/// A library that opts into ASP.NET via FrameworkReference on the plain SDK —
/// the other way a project reaches ASP.NET without a PackageReference.
let private frameworkRefFsproj =
  """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>"""

let private plainConsoleFsproj =
  """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FSharp.Core" />
  </ItemGroup>
</Project>"""

[<Tests>]
let projectFileMarkerTests =
  testList "ProjectFileMarkers" [

    testCase "the Web SDK attribute is a marker" <| fun _ ->
      ProjectFileMarkers.parse minimalApiFsproj
      |> Expect.contains "Sdk=\"Microsoft.NET.Sdk.Web\" must surface as a marker" "Microsoft.NET.Sdk.Web"

    testCase "an ASP.NET FrameworkReference is a marker" <| fun _ ->
      ProjectFileMarkers.parse frameworkRefFsproj
      |> Expect.contains
        "<FrameworkReference Include=\"Microsoft.AspNetCore.App\" /> must surface as a marker"
        "Microsoft.AspNetCore.App"

    testCase "a plain console project yields no web marker" <| fun _ ->
      ProjectFileMarkers.parse plainConsoleFsproj
      |> List.filter (fun m -> WebMarkers.matchesAny WebMarkers.all [ m ])
      |> Expect.isEmpty "a plain SDK console project must contribute no web marker"

    testCase "malformed XML yields no markers rather than throwing" <| fun _ ->
      ProjectFileMarkers.parse "<Project Sdk=\"broken"
      |> Expect.isEmpty "an unreadable project file must degrade to no markers, never throw"

    testCase "empty text yields no markers" <| fun _ ->
      ProjectFileMarkers.parse "" |> Expect.isEmpty "empty project text must yield no markers"

    testCase "reading a missing file yields no markers rather than throwing" <| fun _ ->
      ProjectFileMarkers.read "/nonexistent/definitely/not/here.fsproj"
      |> Expect.isEmpty "a missing project file must degrade to no markers, never throw"
  ]

[<Tests>]
let minimalApiClassificationTests =
  testList "Minimal API / plain ASP.NET classification" [

    testCase "a Web SDK project with zero packages classifies as web" <| fun _ ->
      ProjectFileMarkers.parse minimalApiFsproj
      |> ProjectKind.classify
      |> ProjectKind.label
      |> Expect.equal "a Microsoft.NET.Sdk.Web project is a web app even with no PackageReference" "web"

    testCase "a Web SDK project is offered the hot-reload workflow" <| fun _ ->
      ProjectFileMarkers.parse minimalApiFsproj
      |> WorkflowDetection.suggest
      |> Expect.isSome "a Minimal API project must be nudged toward hot reload"

    testCase "an ASP.NET FrameworkReference project classifies as web" <| fun _ ->
      ProjectFileMarkers.parse frameworkRefFsproj
      |> ProjectKind.classify
      |> ProjectKind.label
      |> Expect.equal "a FrameworkReference to Microsoft.AspNetCore.App is a web app" "web"

    testCase "a plain console project stays console" <| fun _ ->
      ProjectFileMarkers.parse plainConsoleFsproj
      |> ProjectKind.classify
      |> ProjectKind.label
      |> Expect.equal "a plain SDK console project must not be promoted to web" "console"

    testCase "a plain console project is offered no workflow switch" <| fun _ ->
      ProjectFileMarkers.parse plainConsoleFsproj
      |> WorkflowDetection.suggest
      |> Expect.isNone "a console project must not be nudged toward hot reload"
  ]

[<Tests>]
let oxpeckerClassificationTests =
  testList "Oxpecker classification" [

    testCase "Oxpecker classifies as web" <| fun _ ->
      ProjectKind.classify [ "Oxpecker"; "FSharp.Core" ]
      |> ProjectKind.label
      |> Expect.equal "Oxpecker is an ASP.NET Core web framework" "web"

    testCase "Oxpecker satellite packages classify as web" <| fun _ ->
      for pkg in [ "Oxpecker.ViewEngine"; "Oxpecker.Htmx"; "Oxpecker.OpenApi" ] do
        ProjectKind.classify [ pkg ]
        |> ProjectKind.label
        |> Expect.equal (sprintf "%s is a server-side Oxpecker package" pkg) "web"

    testCase "Oxpecker is offered the hot-reload workflow" <| fun _ ->
      [ "Oxpecker"; "FSharp.Core" ]
      |> WorkflowDetection.suggest
      |> Expect.isSome "an Oxpecker project must be nudged toward hot reload"
  ]

[<Tests>]
let webMarkerDriftTests =
  testList "web marker drift" [

    // THE regression test for the two-lists defect. It reads the ONE shared
    // list, so a marker added there is automatically required to behave
    // identically on both classification paths — the two can no longer drift
    // without this failing.
    testCase "every shared web marker classifies as web AND suggests hot reload" <| fun _ ->
      for marker in WebMarkers.all do
        let refs = [ marker ]
        ProjectKind.classify refs
        |> ProjectKind.label
        |> Expect.equal (sprintf "'%s' is in WebMarkers.all, so ProjectKind must call it web" marker) "web"
        WorkflowDetection.suggest refs
        |> Expect.isSome (sprintf "'%s' is in WebMarkers.all, so WorkflowDetection must suggest hot reload" marker)

    testCase "the two classification paths agree on every marker combination" <| fun _ ->
      // Both paths must answer the same question the same way. NativeGui is the
      // one legitimate override (a game wins over a web package), so it is
      // excluded from the pool here and covered by its own test.
      let pool = WebMarkers.all @ [ "FSharp.Core"; "Newtonsoft.Json"; "Expecto" ]
      // Every 2-subset, which is enough to catch a marker present in one list
      // and absent from the other.
      for a in pool do
        for b in pool do
          let refs = [ a; b ]
          let isWeb = ProjectKind.classify refs = ProjectKind.Web BrowserRefreshConfig.defaults
          let isSuggested = (WorkflowDetection.suggest refs) |> Option.isSome
          isSuggested
          |> Expect.equal
            (sprintf "ProjectKind and WorkflowDetection must agree for [%s; %s]" a b)
            isWeb

    testCase "a native GUI marker still wins over a web marker" <| fun _ ->
      ProjectKind.classify [ "Raylib-cs"; "Falco" ]
      |> ProjectKind.label
      |> Expect.equal "a native game dominates the runtime shape" "native-gui"

    // The concrete drift that had already happened: StarFederation.Datastar was
    // only in the detection list, and was spelled with a lowercase 'f' so it
    // never matched the real package id `StarFederation.Datastar.FSharp` anyway.
    testCase "raw StarFederation Datastar is web on both paths" <| fun _ ->
      let refs = [ "StarFederation.Datastar.FSharp"; "FSharp.Core" ]
      ProjectKind.classify refs
      |> ProjectKind.label
      |> Expect.equal "a raw Datastar project is a web app" "web"
      WorkflowDetection.suggest refs
      |> Expect.isSome "a raw Datastar project must be nudged toward hot reload"

    testCase "marker matching is case-insensitive" <| fun _ ->
      ProjectKind.classify [ "falco" ]
      |> ProjectKind.label
      |> Expect.equal "package id casing must not decide classification" "web"
      WorkflowDetection.suggest [ "GIRAFFE" ]
      |> Expect.isSome "package id casing must not decide the suggestion"
  ]
