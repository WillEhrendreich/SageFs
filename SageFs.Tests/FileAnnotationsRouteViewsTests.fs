module SageFs.Tests.FileAnnotationsRouteViewsTests

// A connection used to be sent, on connect, one `file_annotations` frame and one `coverage_view` frame per
// function for EVERY file of the session. `GET /api/live-testing/file-annotations` answers one file's
// annotations, and now carries that file's coverage views with them, so an editor can ask for the files it
// has open instead of being sent them all.
//
// An element of `CoverageViews` is exactly the `data:` of the `coverage_view` frame for that view, so a
// client's handler for the frame takes it unchanged. (A separate route was tried first: the edge-access
// ratchet counts routes open to every caller and does not let that number go up, and one request per file
// beats two.)

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Server

let private file = "/repo/Tests/File1.fs"

let private entry (line: int) : TestStatusEntry =
  let name = sprintf "Tests.File1.case%d" line
  { TestId = TestId.TestId name
    DisplayName = name
    FullName = name
    Origin = TestOrigin.SourceMapped (file, line)
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit
    CurrentPolicy = RunPolicy.OnEveryChange
    Status = TestRunStatus.Passed TimeSpan.Zero
    PreviousStatus = TestRunStatus.Detected
    Provenance = ResultProvenance.Compiled }

let private coverage (symbol: string) (file: string) (line: int) : CoverageAnnotation =
  { Symbol = symbol
    FilePath = file
    DefinitionLine = line
    Status = CoverageStatus.Covered (1, CoverageHealth.AllPassing)
    BranchCoverage = BranchCoverage.Unknown }

let private cycle : LiveTestCycleState =
  let state =
    { LiveTestState.empty with
        Activation = LiveTestingActivation.Active
        CoverageAnnotations = [| coverage "File1.f" file 10; coverage "File1.g" file 20; coverage "Other.h" "/repo/Tests/Other.fs" 5 |] }
    |> LiveTestState.withStatusEntries [| entry 10; entry 20 |]
  { LiveTestCycleState.empty with TestState = state }

let private written
  (action: Microsoft.AspNetCore.Http.HttpContext -> System.Threading.Tasks.Task)
  : System.Threading.Tasks.Task<string * Microsoft.AspNetCore.Http.HttpContext> =
  task {
    let http = Microsoft.AspNetCore.Http.DefaultHttpContext()
    use body = new MemoryStream()
    http.Response.Body <- body
    do! action http
    return Encoding.UTF8.GetString(body.ToArray()), (http :> Microsoft.AspNetCore.Http.HttpContext)
  }

/// Read something off the parsed root. Not inside a `task`, where `use` wants an async disposable.
let private inRoot (text: string) (read: JsonElement -> 'a) : 'a =
  use parsed = JsonDocument.Parse text
  read parsed.RootElement

let private viewsOf (text: string) : string list =
  inRoot text (fun root -> [ for element in root.GetProperty("CoverageViews").EnumerateArray() -> element.GetRawText() ])

/// The `data:` payload of a `coverage_view` frame.
let private dataOf (frame: string) : string =
  frame.Split('\n') |> Array.find (fun line -> line.StartsWith "data: ") |> fun line -> line.Substring 6

[<Tests>]
let tests =
  testList "file-annotations carries the file's coverage views" [

    testTask "WHY — CoverageViews are exactly the coverage_view frames' data for the file, so one handler takes either" {
      let! (text: string), (http: Microsoft.AspNetCore.Http.HttpContext) =
        written (fun http -> McpServer.writeFileAnnotations http cycle (Some "aa11bb22") file)
      let opts = Json.optionsOf Json.standard
      let expected =
        FileAnnotationsInternals.projectViewsForFile CoverageViewMode.defaults file cycle.DepGraph cycle.TestState
        |> Array.map (fun view -> dataOf (SseWriter.formatCoverageViewEvent opts (Some "aa11bb22") (RunGeneration.value cycle.TestState.LastGeneration) view))
      expected.Length |> Expect.equal "the file has two functions, so two views" 2
      viewsOf text |> Expect.equal "each element is a frame's data" (List.ofArray expected)
      http.Response.ContentType |> Expect.equal "it says it is JSON" "application/json"
    }

    testTask "the annotations are still there, as the object the route always answered" {
      let! (text: string), _ = written (fun http -> McpServer.writeFileAnnotations http cycle (Some "aa11bb22") file)
      let expected = FileAnnotations.projectWithCoverage file cycle
      inRoot text (fun root -> root.GetProperty("FilePath").GetString(), root.GetProperty("TestAnnotations").GetArrayLength())
      |> Expect.equal "the file and its two tests" (file, expected.TestAnnotations.Length)
    }

    testTask "each view names the session it belongs to, like the frame does" {
      let! (text: string), _ = written (fun http -> McpServer.writeFileAnnotations http cycle (Some "aa11bb22") file)
      inRoot text (fun root -> [ for element in root.GetProperty("CoverageViews").EnumerateArray() -> element.GetProperty("SessionId").GetString() ])
      |> Expect.equal "the session id" [ "aa11bb22"; "aa11bb22" ]
    }

    testTask "only the asked-for file's views are in the answer" {
      let! (text: string), _ = written (fun http -> McpServer.writeFileAnnotations http cycle (Some "aa11bb22") file)
      inRoot text (fun root -> [ for element in root.GetProperty("CoverageViews").EnumerateArray() -> element.GetProperty("FilePath").GetString() ])
      |> List.distinct
      |> Expect.equal "one file" [ file ]
    }

    testTask "a file the cycle knows nothing about has the empty annotations and no views, and is not an error" {
      let! (text: string), (http: Microsoft.AspNetCore.Http.HttpContext) =
        written (fun http -> McpServer.writeFileAnnotations http cycle None "NoSuch.fs")
      viewsOf text |> Expect.isEmpty "no views"
      inRoot text (fun root -> root.GetProperty("FilePath").GetString()) |> Expect.equal "the path it was asked for" "NoSuch.fs"
      http.Response.StatusCode |> Expect.equal "200" 200
    }

    testTask "the file may be named by a suffix of its path" {
      let! (text: string), _ = written (fun http -> McpServer.writeFileAnnotations http cycle None "Tests/File1.fs")
      viewsOf text |> List.length |> Expect.equal "found by suffix" 2
    }

    test "withCoverageViews adds the member to an object, and leaves a value that is not an object alone" {
      SseWriter.withCoverageViews """{"a":1}""" [| """{"v":1}"""; """{"v":2}""" |]
      |> Expect.equal "after the last member" """{"a":1,"CoverageViews":[{"v":1},{"v":2}]}"""
      SseWriter.withCoverageViews "{}" [||] |> Expect.equal "an empty object takes it without a stray comma" """{"CoverageViews":[]}"""
      SseWriter.withCoverageViews "[1]" [| "{}" |] |> Expect.equal "an array is returned as it was" "[1]"
    }
  ]
