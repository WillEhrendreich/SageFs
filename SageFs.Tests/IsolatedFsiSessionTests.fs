module SageFs.Tests.IsolatedFsiSessionTests

open System
open Expecto
open Expecto.Flip
open SageFs.IsolatedFsiSession

[<Tests>]
let tests =
  testList "IsolatedFsiSession" [
    // #142: AppContext.BaseDirectory and Assembly.Location inside a session point at the isolated host's own
    // directory (or, worse, at the worker's shadow-copy temp dir), never the project's build output.
    // primaryProjectOutputDir is the pure-over-filesystem-primitives lookup that finds the primary
    // project's real output dir straight from disk — never from the -r: references FSI was given, since
    // those are already rewritten to point at the shadow copy by the time solutionToFsiArgs runs (proven
    // live: an earlier fsiArgs-based version of this function reported the shadow-copy directory, exactly
    // reproducing #142's own Assembly.Location bug for AppContext.BaseDirectory too).
    testList "primaryProjectOutputDir" [
      test "WHY — finds the primary project's directory straight from disk, because the -r: references FSI was given are already rewritten to a shadow-copy temp dir by the time solutionToFsiArgs runs" {
        let projects = [ "/repo/App/App.fsproj" ]
        let files = Map.ofList [ ("/repo/App/bin", "App.dll"), [ "/repo/App/bin/Release/net10.0/App.dll" ] ]
        let matchingFiles dir pattern = files |> Map.tryFind (dir, pattern) |> Option.defaultValue []
        let writeTime _ = DateTime(2026, 1, 1)
        primaryProjectOutputDirWith (fun _ -> true) matchingFiles writeTime projects
        |> Expect.equal "the primary project's own output directory" (Some "/repo/App/bin/Release/net10.0")
      }

      test "uses the FIRST project, not a transitive reference" {
        let projects = [ "/repo/App/App.fsproj"; "/repo/Core/Core.fsproj" ]
        let matchingFiles dir pattern =
          match dir, pattern with
          | "/repo/App/bin", "App.dll" -> [ "/repo/App/bin/Release/net10.0/App.dll" ]
          | _ -> failtest "should never look up a transitive reference's own bin dir"
        primaryProjectOutputDirWith (fun _ -> true) matchingFiles (fun _ -> DateTime.UtcNow) projects
        |> Expect.equal "App, the first requested project, wins over Core" (Some "/repo/App/bin/Release/net10.0")
      }

      test "None when no project is requested" {
        primaryProjectOutputDirWith (fun _ -> true) (fun _ _ -> [ "/x" ]) (fun _ -> DateTime.UtcNow) []
        |> Expect.isNone "nothing to find a directory for"
      }

      test "None when the project's bin/ directory doesn't exist yet (never built)" {
        primaryProjectOutputDirWith (fun _ -> false) (fun _ _ -> failtest "must not enumerate a bin/ that doesn't exist") (fun _ -> DateTime.UtcNow) [ "/repo/App/App.fsproj" ]
        |> Expect.isNone "not built yet"
      }

      test "None when no matching dll is found under bin/ (never guess a path)" {
        primaryProjectOutputDirWith (fun _ -> true) (fun _ _ -> []) (fun _ -> DateTime.UtcNow) [ "/repo/App/App.fsproj" ]
        |> Expect.isNone "never guess a path App.dll wasn't found at"
      }

      test "the NEWEST match wins, mirroring resolveFreshestConfigOutput's own rule (the code you built is the code that's reported)" {
        let matchingFiles _ _ =
          [ "/repo/App/bin/Debug/net10.0/App.dll"; "/repo/App/bin/Release/net10.0/App.dll" ]
        let writeTime path =
          if path = "/repo/App/bin/Release/net10.0/App.dll" then DateTime(2026, 6, 1) else DateTime(2026, 1, 1)
        primaryProjectOutputDirWith (fun _ -> true) matchingFiles writeTime [ "/repo/App/App.fsproj" ]
        |> Expect.equal "the newer Release build wins over the stale Debug one" (Some "/repo/App/bin/Release/net10.0")
      }
    ]

    // #141: the project's own FSharp.Core, when it's a different BUILD from the host's (identical version,
    // different members), silently loses to the host's already-loaded copy. Detecting this at warmup and
    // reporting it beats waiting for MissingMethodException from a method the loaded copy happens to lack.
    testList "detectFSharpCoreMismatchWith" [
      test "WHY — file size differs even when file version and AssemblyVersion match, because that's exactly what #141 found between the SDK toolset copy and a NuGet-restored copy" {
        let lengths = Map.ofList [ "/host/FSharp.Core.dll", 4208424L; "/proj/FSharp.Core.dll", 2405672L ]
        detectFSharpCoreMismatchWith (fun p -> lengths.TryFind p) "/host/FSharp.Core.dll" "/proj/FSharp.Core.dll"
        |> Expect.equal
             "reports both copies and both sizes"
             (Some
               { HostCopy = "/host/FSharp.Core.dll"
                 HostSizeBytes = 4208424L
                 ProjectCopy = "/proj/FSharp.Core.dll"
                 ProjectSizeBytes = 2405672L })
      }

      test "None when both copies agree on size" {
        let lengths = Map.ofList [ "/host/FSharp.Core.dll", 4208424L; "/proj/FSharp.Core.dll", 4208424L ]
        detectFSharpCoreMismatchWith (fun p -> lengths.TryFind p) "/host/FSharp.Core.dll" "/proj/FSharp.Core.dll"
        |> Expect.isNone "same size is not proof of a mismatch"
      }

      test "None (never a false positive) when a length can't be read" {
        detectFSharpCoreMismatchWith (fun _ -> None) "/host/FSharp.Core.dll" "/proj/FSharp.Core.dll"
        |> Expect.isNone "an unreadable file means unproven, not mismatched"
      }
    ]

    testList "describeFSharpCoreMismatch" [
      test "names both paths and sizes, and points at the known symptom" {
        let m =
          { HostCopy = "/host/FSharp.Core.dll"
            HostSizeBytes = 4208424L
            ProjectCopy = "/proj/FSharp.Core.dll"
            ProjectSizeBytes = 2405672L }
        let text = describeFSharpCoreMismatch m
        text |> Expect.stringContains "host path" "/host/FSharp.Core.dll"
        text |> Expect.stringContains "project path" "/proj/FSharp.Core.dll"
        text |> Expect.stringContains "known symptom" "MissingMethodException"
      }
    ]

    testList "ProjectOutputEnvironmentVariable" [
      test "is the name the issue itself suggested" {
        ProjectOutputEnvironmentVariable |> Expect.equal "SAGEFS_PROJECT_OUTPUT, verbatim" "SAGEFS_PROJECT_OUTPUT"
      }
    ]
  ]
