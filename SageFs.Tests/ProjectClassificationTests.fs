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
open SageFs.ProjectCompatibility

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

    // The default SDK is what a project says when it has nothing to say.
    // Emitting it would put "Microsoft.NET.Sdk" in the user-visible PackageRefs
    // of every project in the repo, for no information.
    testCase "the default SDK is not a marker" <| fun _ ->
      ProjectFileMarkers.parse plainConsoleFsproj
      |> Expect.isEmpty "a plain Microsoft.NET.Sdk project must contribute no marker at all"

    testCase "a non-default SDK is still a marker" <| fun _ ->
      ProjectFileMarkers.parse """<Project Sdk="Microsoft.NET.Sdk.Razor"></Project>"""
      |> Expect.contains "any SDK other than the default narrows what the project is" "Microsoft.NET.Sdk.Razor"

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

// ─── Fable / browser projects ───────────────────────────────
//
// Every claim asserted below was measured against the real packages before it
// was written down: all five of Fable.Core, Feliz, Fable.Browser.Dom,
// Fable.Elmish and Fable.Elmish.React restore and `dotnet build` with ZERO
// warnings on net10.0, each shipping a real lib/netstandard2.0/*.dll and no
// MSBuild targets. So "a Fable project cannot be hosted" is false, and
// nothing here may refuse one.

[<Tests>]
let fableMarkerTests =
  testList "FableMarkers" [

    testCase "browser-binding packages are browser-only" <| fun _ ->
      for pkg in
        [ "Fable.Core"; "Fable.Browser.Dom"; "Feliz"; "Feliz.Router"
          "Fable.React"; "Fable.Elmish.React"; "Fable.Remoting.Client" ] do
        FableMarkers.isJsOnly pkg
        |> Expect.isTrue (sprintf "%s has no .NET implementation behind its bindings" pkg)

    // Measured: Fable.Elmish's MVU core genuinely runs on .NET —
    // Program.mkSimple ... |> Program.run executes the update/view loop in a
    // plain console app. Flagging it would be a lie.
    testCase "Fable.Elmish is a real .NET library despite its name" <| fun _ ->
      FableMarkers.isJsOnly "Fable.Elmish"
      |> Expect.isFalse "Fable.Elmish's MVU core runs on .NET"

    testCase "Feliz.ViewEngine renders server-side and is not browser-only" <| fun _ ->
      FableMarkers.isJsOnly "Feliz.ViewEngine"
      |> Expect.isFalse "Feliz.ViewEngine renders HTML on the server"

    testCase "the allowlist is exact, so longer prefixes still match" <| fun _ ->
      FableMarkers.isJsOnly "Fable.Elmish.React"
      |> Expect.isTrue "Fable.Elmish.React is the React view layer, not the portable MVU core"

    testCase "server packages are never browser-only" <| fun _ ->
      for pkg in [ "Falco"; "Giraffe"; "Saturn"; "Oxpecker"; "Oxpecker.ViewEngine"; "FSharp.Core" ] do
        FableMarkers.isJsOnly pkg
        |> Expect.isFalse (sprintf "%s is a server-side/.NET package" pkg)

    testCase "matching ignores case" <| fun _ ->
      FableMarkers.isJsOnly "fable.core" |> Expect.isTrue "package id casing must not decide the verdict"
  ]

[<Tests>]
let fableWebSuppressionTests =
  testList "Fable suppression of the web verdict" [

    // Oxpecker.Solid's nuspec: "F# web framework built on top of Solid.js",
    // depending on Fable.Core and Fable.Browser.Dom. Substring-matching the
    // Oxpecker SERVER marker against it would offer browser hot reload to a
    // project with no ASP.NET server in it.
    testCase "Oxpecker.Solid is not an Oxpecker server project" <| fun _ ->
      ProjectKind.classify [ "Oxpecker.Solid"; "Fable.Core" ]
      |> ProjectKind.label
      |> Expect.notEqual "Oxpecker.Solid is a Solid.js client, not an ASP.NET server" "web"

    testCase "Oxpecker.Solid is offered no hot-reload workflow" <| fun _ ->
      WorkflowDetection.suggest [ "Oxpecker.Solid"; "Fable.Browser.Dom" ]
      |> Expect.isNone "a browser-only project must not be nudged toward browser hot reload"

    testCase "a plain Fable client is not a web app" <| fun _ ->
      ProjectKind.classify [ "Fable.Core"; "Feliz"; "Fable.Elmish.React" ]
      |> ProjectKind.label
      |> Expect.notEqual "a Fable client has no ASP.NET server" "web"

    // The SAFE-stack shape: a server project that ALSO carries client
    // references must still be seen as a server. Suppression removes the
    // browser packages as EVIDENCE; it never vetoes real server evidence.
    testCase "a server project keeps its web verdict despite Fable references" <| fun _ ->
      ProjectKind.classify [ "Giraffe"; "Fable.Remoting.Giraffe"; "Fable.Core" ]
      |> ProjectKind.label
      |> Expect.equal "Giraffe is real server evidence that Fable references cannot cancel" "web"

    testCase "a server project with Fable references is still offered hot reload" <| fun _ ->
      WorkflowDetection.suggest [ "Saturn"; "Fable.Core" ]
      |> Expect.isSome "the server half of a SAFE app must still be nudged toward hot reload"
  ]

[<Tests>]
let toolchainFitTests =
  testList "ToolchainFit" [

    testCase "an ordinary .NET project is DotNet" <| fun _ ->
      classifyToolchain [ "Giraffe"; "FSharp.Core"; "Expecto" ]
      |> Expect.equal "nothing browser-only here" ToolchainFit.DotNet

    testCase "an empty package list is DotNet" <| fun _ ->
      classifyToolchain []
      |> Expect.equal "absence of evidence is not evidence of a browser project" ToolchainFit.DotNet

    testCase "a Fable client is FableClient, carrying the references that said so" <| fun _ ->
      match classifyToolchain [ "Fable.Core"; "FSharp.Core"; "Feliz" ] with
      | ToolchainFit.FableClient markers ->
        markers |> Expect.equal "the verdict must name its own evidence" [ "Fable.Core"; "Feliz" ]
      | ToolchainFit.DotNet -> failtest "a project referencing Fable.Core and Feliz is a Fable client"

    // The severe case the brief asked about: it must NOT be a refusal.
    testCase "a Fable client is never refused as unhostable" <| fun _ ->
      let fableClientFsproj =
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <DefineConstants>FABLE_COMPILER</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Fable.Core" />
    <PackageReference Include="Feliz" />
  </ItemGroup>
</Project>"""
      match classify fableClientFsproj with
      | ProjectHostability.Hostable _ -> ()
      | ProjectHostability.Indeterminate _ -> ()
      | ProjectHostability.NotHostable _ ->
        failtest "a Fable client project builds and loads — refusing it would be a lie"

    testCase "the advisory names the project, the evidence and what to do instead" <| fun _ ->
      let message = describeFableClient "Client.fsproj" [ "Fable.Core"; "Feliz" ]
      message |> Expect.stringContains "must name the project" "Client.fsproj"
      message |> Expect.stringContains "must name its evidence" "Fable.Core"
      message |> Expect.stringContains "must say what DOES work" "shared"
      message |> Expect.stringContains "must hand the client off to the right tool" "Vite"
      message |> Expect.stringContains "must quote the real runtime failure" "JS only"
  ]

[<Tests>]
let paketReferenceTests =
  testList "PaketReferences" [

    // The real SAFE template Client.fsproj carries NO <PackageReference> — it
    // imports Paket.Restore.targets and lists packages in paket.references. A
    // package-element-only reader therefore sees an empty list for exactly the
    // project shape we most need to say something about.
    testCase "package ids are read one per line" <| fun _ ->
      PaketReferences.parse "Fable.Core\nFeliz\nFable.Elmish.React\n"
      |> Expect.equal "every non-empty line is a package id" [ "Fable.Core"; "Feliz"; "Fable.Elmish.React" ]

    testCase "group headers, comments and blank lines are dropped" <| fun _ ->
      PaketReferences.parse "group Client\n\n# the view layer\nFeliz\n// noise\nFable.Core\n"
      |> Expect.equal "only package ids survive" [ "Feliz"; "Fable.Core" ]

    testCase "a framework restriction suffix is stripped" <| fun _ ->
      PaketReferences.parse "Fable.Core framework: net10.0\n"
      |> Expect.equal "the id is the first token" [ "Fable.Core" ]

    testCase "empty text yields no packages" <| fun _ ->
      PaketReferences.parse "" |> Expect.isEmpty "nothing to read"

    testCase "a missing paket.references yields no packages rather than throwing" <| fun _ ->
      PaketReferences.readForProject "/nonexistent/definitely/not/here.fsproj"
      |> Expect.isEmpty "absence must degrade to [], never throw"

    testCase "a Paket-managed Fable client is still recognised" <| fun _ ->
      PaketReferences.parse "group Client\nFable.Core\nFeliz\nFable.Elmish.React\n"
      |> classifyToolchain
      |> function
        | ToolchainFit.FableClient _ -> ()
        | ToolchainFit.DotNet -> failtest "a Paket-managed SAFE client must not look like a plain .NET project"
  ]
