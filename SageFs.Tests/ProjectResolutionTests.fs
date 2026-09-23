module SageFs.Tests.ProjectResolutionTests

open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open SageFs

[<Tests>]
let projectResolutionClassifyTests =
  testList "ProjectResolution.classify" [
    testCase "WHY — nothing named and nothing on disk is a genuine scratch session, not a broken one" <| fun _ ->
      ProjectResolution.classify (fun _ -> []) "/scratch" [] 0
      |> Expect.equal "no request, no candidates, nothing resolved: fine" ProjectResolution.NoneRequested

    testCase "WHY — an explicit project that resolves to nothing is the exact bug that shipped Ready with loadedProjects: []" <| fun _ ->
      ProjectResolution.classify (fun _ -> failwith "must not glob when a project was named explicitly") "/repo" [ "Foo.fsproj" ] 0
      |> Expect.equal "named but unresolved" (ProjectResolution.RequestedButUnresolved [ "Foo.fsproj" ])

    testCase "WHY — an explicit project that DOES resolve is the ordinary, quiet case" <| fun _ ->
      ProjectResolution.classify (fun _ -> failwith "must not glob when a project was named explicitly") "/repo" [ "Foo.fsproj" ] 1
      |> Expect.equal "named and resolved" ProjectResolution.Resolved

    testCase "WHY — projects=[] with real candidates sitting on disk means auto-discover was implicitly requested; resolving zero of them is still a real failure, not a scratch session" <| fun _ ->
      ProjectResolution.classify (fun _ -> [ "/repo/Found.fsproj" ]) "/repo" [] 0
      |> Expect.equal "auto-discover found candidates, none resolved" (ProjectResolution.RequestedButUnresolved [ "/repo/Found.fsproj" ])

    testCase "WHY — projects=[] with real candidates that DO resolve is the ordinary auto-discover happy path" <| fun _ ->
      ProjectResolution.classify (fun _ -> [ "/repo/Found.fsproj" ]) "/repo" [] 1
      |> Expect.equal "auto-discover found candidates, resolved" ProjectResolution.Resolved

    testCase "WHY — an explicit non-empty request always wins over what's on disk, even if the glob disagrees" <| fun _ ->
      ProjectResolution.classify (fun _ -> []) "/repo" [ "Foo.fsproj"; "Bar.fsproj" ] 0
      |> Expect.equal "explicit request is authoritative" (ProjectResolution.RequestedButUnresolved [ "Foo.fsproj"; "Bar.fsproj" ])

    testPropertyWithConfig FsCheckConfig.defaultConfig "PROPERTY — resolvedCount > 0 is never RequestedButUnresolved, whatever was asked for or found" <|
      fun (explicit: string list) (candidates: string list) (PositiveInt resolvedCount) ->
        match ProjectResolution.classify (fun _ -> candidates) "/repo" explicit resolvedCount with
        | ProjectResolution.RequestedButUnresolved _ -> false
        | ProjectResolution.NoneRequested
        | ProjectResolution.Resolved -> true

    testPropertyWithConfig FsCheckConfig.defaultConfig "PROPERTY — an empty explicit request with nothing on disk is always NoneRequested, regardless of resolvedCount" <|
      fun (resolvedCount: int) ->
        ProjectResolution.classify (fun _ -> []) "/repo" [] resolvedCount = ProjectResolution.NoneRequested

    testPropertyWithConfig FsCheckConfig.defaultConfig "PROPERTY — a non-empty explicit request that resolves to zero always names exactly what was requested" <|
      fun (explicitNonEmpty: NonEmptyArray<string>) ->
        let explicit = explicitNonEmpty.Get |> Array.toList
        match ProjectResolution.classify (fun _ -> failwith "must not consult disk") "/repo" explicit 0 with
        | ProjectResolution.RequestedButUnresolved requested -> requested = explicit
        | _ -> false
  ]

[<Tests>]
let projectResolutionListCandidatesOnDiskTests =
  testList "ProjectResolution.listCandidatesOnDisk" [
    testCase "WHY — a genuinely empty directory has no candidates, backing the real scratch-session case" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "sagefs-test-" + System.Guid.NewGuid().ToString("N"))
      Directory.CreateDirectory dir |> ignore
      try
        ProjectResolution.listCandidatesOnDisk dir
        |> Expect.equal "no fsproj/sln/slnx in an empty directory" []
      finally
        Directory.Delete(dir, true)

    testCase "WHY — a directory with an .fsproj IS a candidate — auto-discover found something" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "sagefs-test-" + System.Guid.NewGuid().ToString("N"))
      Directory.CreateDirectory dir |> ignore
      try
        let proj = Path.Combine(dir, "Found.fsproj")
        File.WriteAllText(proj, "<Project />")
        ProjectResolution.listCandidatesOnDisk dir
        |> Expect.equal "the fsproj is a candidate" [ proj ]
      finally
        Directory.Delete(dir, true)

    testCase "WHY — a directory that cannot be listed fails open to no candidates rather than crashing the readiness decision" <| fun _ ->
      let missing = Path.Combine(Path.GetTempPath(), "sagefs-test-does-not-exist-" + System.Guid.NewGuid().ToString("N"))
      ProjectResolution.listCandidatesOnDisk missing
      |> Expect.equal "a missing directory has no candidates" []
  ]

[<Tests>]
let projectResolutionUnresolvedReasonTests =
  testList "ProjectResolution.unresolvedReason" [
    testCase "WHY — the reason names exactly what was requested so the reader can act on it" <| fun _ ->
      let reason = ProjectResolution.unresolvedReason [ "Foo.fsproj" ]
      reason.Contains "Foo.fsproj" |> Expect.isTrue "names the requested project"
      reason.Contains "loadedProjects: []" |> Expect.isTrue "names the observed symptom"

    testCase "WHY — multiple requested projects are all named, not truncated to one" <| fun _ ->
      let reason = ProjectResolution.unresolvedReason [ "Foo.fsproj"; "Bar.fsproj" ]
      reason.Contains "Foo.fsproj" |> Expect.isTrue "names the first"
      reason.Contains "Bar.fsproj" |> Expect.isTrue "names the second"
  ]
