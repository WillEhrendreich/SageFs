// WHY — on connect the daemon used to send one `file_annotations` frame and one `coverage_view` frame per
// function for EVERY file of the session, because nothing said which files a window had open. It now lists the
// files that have annotations (`annotations_available`), and the window asks for the ones it has open:
// GET /api/live-testing/file-annotations?session=&file= answers a file's annotations with its CoverageViews.
// These pin the pure decisions: which listed file an open editor is, and where to ask. The same rules as
// sagefs.nvim's annotations_fetch.lua.
//
// Runs under plain `dotnet fsi` (no Fable), like the other contract tests here.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/AnnotationsFetchPure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.AnnotationsFetchPure

let tests =
  testList "VS Code annotations fetch - which open editor is which listed file" [

    testCase "an available file that an editor has open is asked for, in the daemon's spelling" <| fun _ ->
      filesToFetch [ "/repo/src/A.fs"; "/repo/src/B.fs"; "/repo/src/C.fs" ] [ "/repo/src/B.fs" ]
      |> Expect.equal "only B" [ "/repo/src/B.fs" ]

    testCase "a path that is a suffix of the daemon's, or the other way round, is the same file" <| fun _ ->
      filesToFetch [ "/daemon/root/src/A.fs"; "src/B.fs" ] [ "src/A.fs"; "/work/checkout/src/B.fs" ]
      |> List.sort
      |> Expect.equal "both" [ "/daemon/root/src/A.fs"; "src/B.fs" ]

    testCase "Windows separators read as slashes" <| fun _ ->
      filesToFetch [ "C:\\repo\\src\\A.fs" ] [ "C:/repo/src/A.fs" ]
      |> Expect.equal "same file" [ "C:\\repo\\src\\A.fs" ]

    testCase "nothing open, nothing listed, or nothing in common asks for nothing" <| fun _ ->
      filesToFetch [ "/repo/A.fs" ] [ "/elsewhere/Z.fs" ] |> Expect.isEmpty "no overlap"
      filesToFetch [] [ "/repo/A.fs" ] |> Expect.isEmpty "nothing listed"
      filesToFetch [ "/repo/A.fs" ] [] |> Expect.isEmpty "nothing open"

    testCase "each file is named once however many editors show it" <| fun _ ->
      filesToFetch [ "/repo/A.fs" ] [ "/repo/A.fs"; "A.fs" ]
      |> Expect.equal "once" [ "/repo/A.fs" ]

    testCase "WHY - a longer name that merely ends the same way is another file" <| fun _ ->
      filesToFetch [ "/repo/FooA.fs" ] [ "/repo/A.fs" ] |> Expect.isEmpty "FooA is not A"

    testCase "empty and blank names are ignored" <| fun _ ->
      filesToFetch [ ""; "/a.fs" ] [ ""; "/a.fs" ] |> Expect.equal "only the real one" [ "/a.fs" ]

    testCase "the request names the session and the file, escaped" <| fun _ ->
      url "http://localhost:37749" "s1" "/repo/src/A b.fs"
      |> Expect.equal "the file-annotations route"
           "http://localhost:37749/api/live-testing/file-annotations?session=s1&file=%2Frepo%2Fsrc%2FA%20b.fs"
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
