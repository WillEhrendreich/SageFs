module SageFs.Tests.ProjectCheckTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Features.ProjectCheckCore

/// The decisions behind `check_project`: arguments from a design-time MSBuild run, kept until a file that shaped them
/// changes, overlays that replace project files, a referenced project built once when it is behind, and the report. The
/// compiler and the process are not here; `ProjectCheckCompilerTests` runs the real compiler over a fixture project.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 200 }

let private root = "/work/shop"
let private projectFile = "/work/shop/src/Shop.fsproj"
let private overlayRoot = "/tmp/overlays"

let private framework = [ "-r:/nuget/fsharp.core/lib/FSharp.Core.dll"; "-r:/packs/System.Runtime.dll" ]

/// The identities `CoreCompile` returns: options, then the sources relative to the project directory.
let private identities (sources: string list) : string list =
  [ "-o:obj/Debug/net10.0/Shop.dll"; "--langversion:11"; "--define:DEBUG" ] @ framework @ sources

let private argumentsOf (sources: string list) : CompilerArguments =
  match CompilerArguments.ofIdentities projectFile (identities sources) with
  | Ok a -> a
  | Error e -> failtestf "fixture arguments refused: %A" e

let private sourcesN (n: int) : string list = [ for i in 1..n -> sprintf "File%d.fs" i ]

let private factsOf (sources: string list) : MsBuildFacts =
  { Arguments = argumentsOf sources
    Configuration = "Debug"
    References = []
    Inputs = [ projectFile; "/work/shop/Directory.Build.props"; "/work/shop/Directory.Packages.props" ] }

let private key (facts: MsBuildFacts) : CacheKey = { Project = facts.Arguments.ProjectFile; Configuration = facts.Configuration }

let private present (ticks: int64) : StampState = StampState.Present(ticks, 100L)

/// A disk where every path the fingerprint names has the stamp the map gives it, and `Absent` otherwise.
let private diskOf (stamps: Map<string, StampState>) : string -> StampState =
  fun path -> Map.tryFind path stamps |> Option.defaultValue StampState.Absent

let private entryOn (disk: string -> StampState) (facts: MsBuildFacts) : CacheEntry =
  { Key = key facts; Facts = facts; Stamps = Fingerprint.stamp disk facts }

let private everythingAt (ticks: int64) (facts: MsBuildFacts) : Map<string, StampState> =
  Fingerprint.paths facts |> List.map (fun p -> p, present ticks) |> Map.ofList

let private diag (path: string) (line: int) (column: int) (severity: Severity) (code: string) (message: string) : CheckDiagnostic =
  { Path = path; Line = line; Column = column; EndLine = line; EndColumn = column + 1; Severity = severity; Code = code; Message = message }

let private decided (decision: CacheDecision) : Result<unit, RefreshReason> =
  match decision with
  | CacheDecision.Reuse _ -> Ok()
  | CacheDecision.Refresh reason -> Error reason

[<Tests>]
let tests =
  testList "project check core" [

    testList "overlays" [
      testPropertyWithConfig simConfig "WHY — an overlay for a file the project does not compile is refused with the project's files, so a wrong path never reaches the compiler"
        <| fun (count: byte) ->
          let sources = sourcesN (int count % 8 + 1)
          let args = argumentsOf sources
          match Overlays.resolve overlayRoot args [ { Path = "Elsewhere.fs"; Text = "module E" } ] with
          | Error(OverlayRefusal.NotInProject(path, listed)) ->
            path |> Expect.equal "the path as resolved" (Path.GetFullPath("Elsewhere.fs", "/work/shop/src"))
            listed |> Expect.equal "the project's own files, in order" args.Sources
          | other -> failtestf "expected NotInProject, got %A" other

      testCase "WHY — the same overlay path twice is refused, even when one spelling is relative" <| fun _ ->
        let args = argumentsOf (sourcesN 3)
        match Overlays.resolve overlayRoot args [ { Path = "File2.fs"; Text = "a" }; { Path = "/work/shop/src/File2.fs"; Text = "b" } ] with
        | Error(OverlayRefusal.Duplicate path) -> path |> Expect.equal "the one path" "/work/shop/src/File2.fs"
        | other -> failtestf "expected Duplicate, got %A" other

      testCase "WHY — an overlaid file keeps its place in compile order and every other source is untouched" <| fun _ ->
        let args = argumentsOf (sourcesN 4)
        match Overlays.resolve overlayRoot args [ { Path = "File3.fs"; Text = "module F3" } ] with
        | Ok plan ->
          plan.Sources.Length |> Expect.equal "same count" 4
          plan.Sources |> List.item 0 |> Expect.equal "first untouched" (args.Sources |> List.item 0)
          plan.Sources |> List.item 1 |> Expect.equal "second untouched" (args.Sources |> List.item 1)
          plan.Sources |> List.item 3 |> Expect.equal "fourth untouched" (args.Sources |> List.item 3)
          plan.Sources |> List.item 2 |> Expect.notEqual "third is replaced" (args.Sources |> List.item 2)
          plan.Replaced |> List.map fst |> Expect.equal "what was replaced" [ "/work/shop/src/File3.fs" ]
          plan.Writes |> List.map snd |> Expect.equal "the text to write" [ "module F3" ]
        | Error e -> failtestf "expected a plan, got %A" e

      testPropertyWithConfig simConfig "WHY — the overlay path for a file is the same on every call and ends in the file's own name, so the compiler's cache sees one changed timestamp and an implicit module keeps its name"
        <| fun (count: byte) (pick: byte) ->
          let n = int count % 8 + 1
          let args = argumentsOf (sourcesN n)
          let target = sprintf "File%d.fs" (int pick % n + 1)
          let plan text = Overlays.resolve overlayRoot args [ { Path = target; Text = text } ]
          match plan "one", plan "two" with
          | Ok a, Ok b ->
            (a.Replaced |> List.map snd) |> Expect.equal "same overlay path" (b.Replaced |> List.map snd)
            (a.Replaced |> List.map snd |> List.head |> Path.GetFileName) |> Expect.equal "file name kept" target
          | _ -> failtest "both overlays are for a project file"

      testCase "WHY — a diagnostic in an overlay file is reported under the project's own path, and any other path is left alone" <| fun _ ->
        let args = argumentsOf (sourcesN 2)
        match Overlays.resolve overlayRoot args [ { Path = "File2.fs"; Text = "x" } ] with
        | Ok plan ->
          let overlay = plan.Replaced |> List.head |> snd
          Overlays.mapBack plan overlay |> Expect.equal "back to the project path" "/work/shop/src/File2.fs"
          Overlays.mapBack plan "/work/shop/src/File1.fs" |> Expect.equal "other files unchanged" "/work/shop/src/File1.fs"
        | Error e -> failtestf "expected a plan, got %A" e

      testCase "WHY — two projects get two overlay directories, so their overlays never share a file" <| fun _ ->
        Overlays.projectKey "/work/a/A.fsproj" |> Expect.notEqual "distinct projects, distinct keys" (Overlays.projectKey "/work/b/B.fsproj")
        Overlays.projectKey projectFile |> Expect.equal "stable" (Overlays.projectKey projectFile)
    ]

    testList "compiler arguments" [
      testCase "WHY — the identities of CoreCompile split into options and sources, and sources resolve against the project directory and not the process's" <| fun _ ->
        let args = argumentsOf [ "A.fs"; "sub/B.fs" ]
        args.Sources |> Expect.equal "full paths" [ "/work/shop/src/A.fs"; "/work/shop/src/sub/B.fs" ]
        args.Options |> List.contains "--langversion:11" |> Expect.isTrue "options kept"
        args.Options |> List.exists (fun o -> o.StartsWith "-o:") |> Expect.isFalse "the output path is dropped, nothing is written"

      testCase "WHY — an argument list with no reference to FSharp.Core is refused, because the MSBuild run answers Success with none when the reference targets did not run" <| fun _ ->
        match CompilerArguments.ofIdentities projectFile [ "--langversion:11"; "A.fs" ] with
        | Error ArgumentsRefusal.NoFSharpCore -> ()
        | other -> failtestf "expected NoFSharpCore, got %A" other

      testCase "WHY — an argument list with no source is refused, there is nothing to check" <| fun _ ->
        match CompilerArguments.ofIdentities projectFile (identities []) with
        | Error ArgumentsRefusal.NoSources -> ()
        | other -> failtestf "expected NoSources, got %A" other

      testCase "WHY — every refusal says what to do or what happened in words" <| fun _ ->
        [ ArgumentsRefusal.NoSources; ArgumentsRefusal.NoFSharpCore; ArgumentsRefusal.TargetFailed "x"; ArgumentsRefusal.Unreadable "y" ]
        |> List.map ArgumentsRefusal.describe
        |> List.filter String.IsNullOrWhiteSpace
        |> Expect.isEmpty "no empty sentence"
    ]

    testList "what the MSBuild run said" [
      let json (result: string) (identities: string list) =
        let items = identities |> List.map (fun i -> sprintf """{ "Identity": "%s", "FullPath": "/wrong/%s" }""" i i) |> String.concat ","
        sprintf
          """{ "TargetResults": { "CoreCompile": { "Result": "%s", "Items": [ %s ] } },
               "Items": { "ReferencePathWithRefAssemblies": [
                 { "Identity": "/work/core/obj/Debug/net10.0/ref/Core.dll", "MSBuildSourceProjectFile": "/work/core/Core.fsproj" },
                 { "Identity": "/packs/System.Runtime.dll" } ] },
               "Properties": { "MSBuildAllProjects": "/work/shop/src/Shop.fsproj;;/work/shop/Directory.Packages.props;", "Configuration": "Debug" } }"""
          result items

      testCase "WHY — the run's JSON becomes arguments, the referenced projects with the file the compiler reads, the files MSBuild read, and the configuration" <| fun _ ->
        match MsBuildFacts.read projectFile (json "Success" (identities [ "A.fs" ])) with
        | Ok facts ->
          facts.Arguments.Sources |> Expect.equal "sources from Identity, not from the wrong FullPath" [ "/work/shop/src/A.fs" ]
          facts.References |> Expect.equal "only items that came from a project" [ { Project = "/work/core/Core.fsproj"; Output = "/work/core/obj/Debug/net10.0/ref/Core.dll" } ]
          facts.Inputs |> Expect.equal "empty entries dropped" [ "/work/shop/src/Shop.fsproj"; "/work/shop/Directory.Packages.props" ]
          facts.Configuration |> Expect.equal "configuration" "Debug"
        | Error e -> failtestf "expected facts, got %A" e

      testCase "WHY — a CoreCompile that did not succeed is a refusal naming what it ended as" <| fun _ ->
        match MsBuildFacts.read projectFile (json "Failure" (identities [ "A.fs" ])) with
        | Error(ArgumentsRefusal.TargetFailed detail) -> detail |> Expect.stringContains "says Failure" "Failure"
        | other -> failtestf "expected TargetFailed, got %A" other

      testCase "WHY — an answer with no CoreCompile result is a refusal, not an empty check" <| fun _ ->
        match MsBuildFacts.read projectFile """{ "Properties": { "Configuration": "Debug" } }""" with
        | Error(ArgumentsRefusal.TargetFailed _) -> ()
        | other -> failtestf "expected TargetFailed, got %A" other

      testCase "WHY — text that is not the JSON of the run (MSBuild's error lines) is Unreadable, with the reason" <| fun _ ->
        match MsBuildFacts.read projectFile "error MSB1009: Project file does not exist." with
        | Error(ArgumentsRefusal.Unreadable detail) -> detail |> Expect.isNotEmpty "a reason"
        | other -> failtestf "expected Unreadable, got %A" other

      testCase "WHY — the run asks for the reference targets before CoreCompile and builds nothing" <| fun _ ->
        let targets = MsBuildFacts.msbuildArguments |> List.find (fun a -> a.StartsWith "-t:")
        (targets.IndexOf "FindReferenceAssembliesForReferences" < targets.IndexOf "CoreCompile") |> Expect.isTrue "reference assemblies come first"
        MsBuildFacts.msbuildArguments |> Expect.contains "SkipCompilerExecution" "-p:SkipCompilerExecution=true"
        MsBuildFacts.msbuildArguments |> Expect.contains "no referenced project is built" "-p:BuildProjectReferences=false"
    ]

    testList "arguments kept between calls" [
      testCase "WHY — nothing kept means read" <| fun _ ->
        ArgumentCache.decide None (diskOf Map.empty) |> decided |> Expect.equal "not cached" (Error RefreshReason.NotCached)

      testCase "WHY — every file of the fingerprint as it was means the kept arguments are used" <| fun _ ->
        let facts = factsOf (sourcesN 3)
        let disk = diskOf (everythingAt 10L facts)
        ArgumentCache.decide (Some(entryOn disk facts)) disk |> decided |> Expect.equal "reuse" (Ok())

      testCase "WHY — a change to Directory.Packages.props refreshes the arguments, because the package versions are in them" <| fun _ ->
        let facts = factsOf (sourcesN 3)
        let before = everythingAt 10L facts
        let entry = entryOn (diskOf before) facts
        let after = before |> Map.add "/work/shop/Directory.Packages.props" (present 11L)
        ArgumentCache.decide (Some entry) (diskOf after) |> decided |> Expect.equal "names the file" (Error(RefreshReason.FileChanged "/work/shop/Directory.Packages.props"))

      testCase "WHY — a change to the project file refreshes the arguments" <| fun _ ->
        let facts = factsOf (sourcesN 3)
        let before = everythingAt 10L facts
        let after = before |> Map.add projectFile (StampState.Present(10L, 999L))
        ArgumentCache.decide (Some(entryOn (diskOf before) facts)) (diskOf after) |> decided |> Expect.equal "same time, new length" (Error(RefreshReason.FileChanged projectFile))

      testCase "WHY — a Directory.Build.props created above the project refreshes the arguments, though MSBuild had not read it" <| fun _ ->
        let facts = factsOf (sourcesN 3)
        let before = everythingAt 10L facts |> Map.remove "/Directory.Build.props"
        let entry = entryOn (diskOf before) facts
        let after = before |> Map.add "/Directory.Build.props" (present 12L)
        ArgumentCache.decide (Some entry) (diskOf after) |> decided |> Expect.equal "appeared" (Error(RefreshReason.FileAppeared "/Directory.Build.props"))

      testCase "WHY — a props file that is deleted refreshes the arguments" <| fun _ ->
        let facts = factsOf (sourcesN 3)
        let before = everythingAt 10L facts
        let entry = entryOn (diskOf before) facts
        let after = before |> Map.remove "/work/shop/Directory.Build.props"
        ArgumentCache.decide (Some entry) (diskOf after) |> decided |> Expect.equal "gone" (Error(RefreshReason.FileGone "/work/shop/Directory.Build.props"))

      testCase "WHY — a source file's edit does not refresh the arguments, the compiler rereads sources itself" <| fun _ ->
        let facts = factsOf (sourcesN 3)
        let before = everythingAt 10L facts
        let entry = entryOn (diskOf before) facts
        let after = before |> Map.add "/work/shop/src/File2.fs" (present 99L)
        ArgumentCache.decide (Some entry) (diskOf after) |> decided |> Expect.equal "reuse" (Ok())

      testCase "WHY — the fingerprint covers the project file, the restore's assets, and props, targets, package versions and global.json in every directory above the project" <| fun _ ->
        let paths = Fingerprint.paths (factsOf (sourcesN 1))
        for expected in
          [ projectFile
            "/work/shop/src/obj/project.assets.json"
            "/work/shop/src/Directory.Build.props"
            "/work/shop/Directory.Build.targets"
            "/work/Directory.Packages.props"
            "/global.json" ] do
          paths |> Expect.contains (sprintf "fingerprint holds %s" expected) expected
        paths |> List.length |> Expect.equal "no path twice" (paths |> List.distinct |> List.length)

      testPropertyWithConfig simConfig "WHY — for any single file of the fingerprint that moves, the arguments are read again and the reason names exactly that file"
        <| fun (n: byte) (pick: int) ->
          let facts = factsOf (sourcesN (int n % 6 + 1))
          let paths = Fingerprint.paths facts
          let moved = paths |> List.item (abs (pick % paths.Length))
          let before = everythingAt 10L facts
          let after = before |> Map.add moved (present 77L)
          ArgumentCache.decide (Some(entryOn (diskOf before) facts)) (diskOf after) |> decided
          |> Expect.equal "names the file" (Error(RefreshReason.FileChanged moved))

      testCase "WHY — a run that was cancelled keeps nothing, a run that completed keeps its arguments" <| fun _ ->
        let facts = factsOf (sourcesN 2)
        let entry = entryOn (diskOf (everythingAt 10L facts)) facts
        ArgumentStore.record entry RunEnd.Cancelled ArgumentStore.empty |> ArgumentStore.tryFind entry.Key |> Expect.isNone "cancelled run leaves no entry"
        ArgumentStore.record entry RunEnd.Completed ArgumentStore.empty |> ArgumentStore.tryFind entry.Key |> Expect.isSome "completed run is kept"
        let kept = ArgumentStore.record entry RunEnd.Completed ArgumentStore.empty
        ArgumentStore.record { entry with Stamps = [] } RunEnd.Cancelled kept |> ArgumentStore.tryFind entry.Key |> Expect.equal "a later cancelled run does not overwrite" (Some entry)

      testCase "WHY — two calls for one key share one run, and the key is free again once the run ends" <| fun _ ->
        let k = { Project = projectFile; Configuration = "Debug" }
        let first, afterFirst = Inflight.join k Inflight.empty
        let second, afterSecond = Inflight.join k afterFirst
        let other, _ = Inflight.join { k with Configuration = "Release" } afterSecond
        first |> Expect.equal "first starts" Joined.Starts
        second |> Expect.equal "second follows" Joined.Follows
        other |> Expect.equal "another configuration is its own run" Joined.Starts
        let again, _ = Inflight.join k (Inflight.finish k afterSecond)
        again |> Expect.equal "free after finish" Joined.Starts
    ]

    testList "referenced projects" [
      testCase "WHY — an output with no file is missing, whatever the inputs" <| fun _ ->
        Staleness.decide (OutputState.NoOutput "/work/core/obj/ref/Core.dll") [ "/work/core/Core.fs", 5L ]
        |> Expect.equal "missing" (Staleness.Missing "/work/core/obj/ref/Core.dll")

      testCase "WHY — an output older than an input is behind it, and the reply names the newest input" <| fun _ ->
        Staleness.decide (OutputState.OutputAt("/o.dll", 10L)) [ "/a.fs", 11L; "/b.fs", 30L; "/c.fs", 9L ]
        |> Expect.equal "newest input" (Staleness.OlderThan "/b.fs")

      testCase "WHY — an output as new as its newest input is current" <| fun _ ->
        Staleness.decide (OutputState.OutputAt("/o.dll", 10L)) [ "/a.fs", 10L; "/b.fs", 3L ] |> Expect.equal "same time is current" Staleness.Current
        Staleness.decide (OutputState.OutputAt("/o.dll", 10L)) [] |> Expect.equal "no inputs is current" Staleness.Current

      testCase "WHY — build order puts the deepest stale project first and builds what depends on a rebuilt project" <| fun _ ->
        let references = Map.ofList [ "app", [ "lib"; "core" ]; "lib", [ "core" ]; "core", [] ]
        let own = Map.ofList [ "app", Staleness.Current; "lib", Staleness.Current; "core", Staleness.OlderThan "Core.fs" ]
        Staleness.buildOrder references own |> Expect.equal "core, then what stands on it" [ "core"; "lib"; "app" ]

      testCase "WHY — a project that is current with nothing behind it is not built" <| fun _ ->
        let references = Map.ofList [ "app", [ "lib" ]; "lib", [ "core" ]; "core", [] ]
        let own = Map.ofList [ "app", Staleness.Current; "lib", Staleness.Current; "core", Staleness.Current ]
        Staleness.buildOrder references own |> Expect.isEmpty "nothing to build"

      testCase "WHY — a stale project in the middle is built after what it references and before what references it, and a sibling that is current is left" <| fun _ ->
        let references = Map.ofList [ "app", [ "lib"; "util" ]; "lib", [ "core" ]; "util", []; "core", [] ]
        let own = Map.ofList [ "lib", Staleness.Missing "lib.dll" ]
        Staleness.buildOrder references own |> Expect.equal "lib then app" [ "lib"; "app" ]

      testPropertyWithConfig simConfig "WHY — over any project graph and any staleness, each project is built after every project it references that is built, and a project is built only when it or something it references is stale"
        <| fun (seed: int) ->
          let rnd = Random(seed)
          let n = rnd.Next(1, 9)
          let name i = sprintf "p%d" i
          // project i references only lower indexes, so the graph is acyclic
          let references = Map.ofList [ for i in 0 .. n - 1 -> name i, [ for j in 0 .. i - 1 do if rnd.Next 3 = 0 then yield name j ] ]
          let own =
            Map.ofList
              [ for i in 0 .. n - 1 ->
                  name i, (match rnd.Next 4 with 0 -> Staleness.Missing "x" | 1 -> Staleness.OlderThan "y" | _ -> Staleness.Current) ]
          let order = Staleness.buildOrder references own
          order |> List.distinct |> List.length |> Expect.equal "no project twice" order.Length
          let indexOf p = List.findIndex ((=) p) order
          let rec behind p =
            (match Map.find p own with Staleness.Current -> false | _ -> true) || (Map.find p references |> List.exists behind)
          for p in order do
            behind p |> Expect.isTrue (sprintf "%s is built only when it is behind" p)
            for dep in Map.find p references do
              match List.contains dep order with
              | true -> (indexOf dep < indexOf p) |> Expect.isTrue (sprintf "%s before %s" dep p)
              | false -> ()
          for p in references |> Map.toList |> List.map fst do
            behind p |> Expect.equal (sprintf "%s is built exactly when behind" p) (List.contains p order)
    ]

    testList "the report" [
      let notes = { Arguments = ArgumentsSource.Kept; Built = [] }

      testCase "WHY — errors are listed before warnings, then in path, line and column order" <| fun _ ->
        let ds =
          [ diag "/b.fs" 1 0 Severity.Warning "FS0025" "incomplete"
            diag "/b.fs" 9 4 Severity.Error "FS0001" "type mismatch"
            diag "/a.fs" 30 2 Severity.Error "FS0039" "not defined"
            diag "/a.fs" 30 1 Severity.Error "FS0039" "not defined" ]
        Report.ordered ds |> List.map (fun d -> d.Path, d.Line, d.Column) |> Expect.equal "order" [ "/a.fs", 30, 1; "/a.fs", 30, 2; "/b.fs", 9, 4; "/b.fs", 1, 0 ]

      testCase "WHY — a project with only warnings is Clean, and the headline counts them" <| fun _ ->
        let text = Report.render 50 (CheckOutcome.Checked([ diag "/a.fs" 1 0 Severity.Warning "FS0025" "incomplete" ], notes))
        text |> Expect.stringStarts "clean" "Clean: the project compiles (1 warnings)."

      testCase "WHY — an error line carries path, line, column, severity, code and message in the form MSBuild prints, on one line" <| fun _ ->
        let text = Report.render 50 (CheckOutcome.Checked([ diag "/a.fs" 3 5 Severity.Error "FS0001" "this\nis wrong" ], notes))
        text.Split('\n') |> Array.item 1 |> Expect.equal "the line" "/a.fs(3,5): error FS0001: this is wrong"
        text |> Expect.stringStarts "headline" "1 errors, 0 warnings."

      testCase "WHY — a long list is cut at the limit and the rest is counted, never silently dropped" <| fun _ ->
        let ds = [ for i in 1..12 -> diag "/a.fs" i 0 Severity.Error "FS0001" "x" ]
        let lines = (Report.render 5 (CheckOutcome.Checked(ds, notes))).Split('\n')
        lines.Length |> Expect.equal "headline, five, the count" 7
        lines |> Array.last |> Expect.equal "what was cut" "... 7 more not shown"

      testCase "WHY — the headline says whether the arguments were kept or read and why, and what was built first" <| fun _ ->
        let text =
          Report.render 5 (CheckOutcome.Checked([], { Arguments = ArgumentsSource.Read(RefreshReason.FileChanged "/work/Directory.Packages.props"); Built = [ "core"; "lib" ] }))
        text |> Expect.stringContains "why read" "/work/Directory.Packages.props changed"
        text |> Expect.stringContains "built first" "built first: core, lib"

      testCase "WHY — a cancelled or timed out check says which stage was stopped and that nothing was concluded" <| fun _ ->
        Report.render 5 (CheckOutcome.Cancelled Stopped.ArgumentsRun) |> Expect.stringContains "msbuild" "MSBuild run"
        Report.render 5 (CheckOutcome.Cancelled(Stopped.ReferenceBuild "core")) |> Expect.stringContains "build" "referenced project core"
        let timed = Report.render 5 (CheckOutcome.TimedOut(Stopped.Compiler, TimeSpan.FromSeconds 30.0))
        timed |> Expect.stringContains "after" "30 s"
        timed |> Expect.stringContains "nothing concluded" "Nothing was concluded"

      testCase "WHY — a refusal is its own sentence and never a Clean" <| fun _ ->
        let text = Report.render 5 (CheckOutcome.Refused(CheckRefusal.Overlay(OverlayRefusal.Duplicate "/a.fs")))
        text |> Expect.stringContains "says which" "/a.fs is given twice"
        text.StartsWith "Clean" |> Expect.isFalse "not clean"

      testCase "WHY — an overlay refusal lists at most twenty files and counts the rest" <| fun _ ->
        let sources = [ for i in 1..45 -> sprintf "/p/F%d.fs" i ]
        let text = OverlayRefusal.describe (OverlayRefusal.NotInProject("/p/Nope.fs", sources))
        text |> Expect.stringContains "counted" "and 25 more"
        text.Contains "/p/F21.fs" |> Expect.isFalse "the 21st is not listed"
    ]
  ]
