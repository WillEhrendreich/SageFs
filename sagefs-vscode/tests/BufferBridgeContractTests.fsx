#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/BufferBridge.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.BufferBridge

let session sessionId workingDirectory =
  { SessionOwnershipCandidate.SessionId = sessionId
    WorkingDirectory = workingDirectory }

let tests =
  testList "VS Code buffer bridge contract" [
    testCase "builds a request for the active file-backed F# source document" <| fun _ ->
      let request =
        tryBuildBufferChangedRequest
          (Some "deadbeef")
          (Some @"C:\repo")
          (Some @"C:\repo\App.fs")
          [||]
          @"C:\repo\App.fs"
          "file"
          "module App"

      request |> Expect.isSome "active .fs document should sync"
      request.Value.SessionId |> Expect.equal "session id forwarded" "deadbeef"
      request.Value.FilePath |> Expect.equal "file path forwarded" @"C:\repo\App.fs"

    testCase "supports signature files for compiled projects" <| fun _ ->
      let request =
        tryBuildBufferChangedRequest
          (Some "deadbeef")
          (Some @"C:\repo")
          (Some @"C:\repo\App.fsi")
          [||]
          @"C:\repo\App.fsi"
          "file"
          "module App"

      request |> Expect.isSome ".fsi document should sync"

    testCase "routes non-active documents that still belong to the active session directory" <| fun _ ->
      let request =
        tryBuildBufferChangedRequest
          (Some "deadbeef")
          (Some @"C:\repo")
          (Some @"C:\repo\Active.fs")
          [||]
          @"C:\repo\Sub\Other.fs"
          "file"
          "module Other"

      request |> Expect.isSome "compiled-project files inside the active session directory should sync even when not the active editor"
      request.Value.FilePath |> Expect.equal "changed document forwarded" @"C:\repo\Sub\Other.fs"

    testCase "ignores documents outside the active session directory even when the path shares a prefix" <| fun _ ->
      tryBuildBufferChangedRequest
        (Some "deadbeef")
        (Some @"C:\repo")
        (Some @"C:\repo\Active.fs")
        [||]
        @"C:\repo-other\Other.fs"
        "file"
        "module Other"
      |> Expect.isNone "path containment must respect directory boundaries, not naive string prefixes"

    testCase "ignores non-file documents" <| fun _ ->
      tryBuildBufferChangedRequest
        (Some "deadbeef")
        (Some @"untitled:Scratch.fs")
        (Some @"untitled:Scratch.fs")
        [||]
        @"untitled:Scratch.fs"
        "untitled"
        "module Scratch"
      |> Expect.isNone "untitled buffers should not be posted to the session-scoped route"

    testCase "ignores fsx documents in the compiled-project bridge slice" <| fun _ ->
      tryBuildBufferChangedRequest
        (Some "deadbeef")
        (Some @"C:\repo")
        (Some @"C:\repo\Script.fsx")
        [||]
        @"C:\repo\Script.fsx"
        "file"
        "printfn \"hi\""
      |> Expect.isNone "the first bridge slice is limited to file-backed compiled-project documents"

    testCase "falls back to active-file matching when the session directory is unknown" <| fun _ ->
      let request =
        tryBuildBufferChangedRequest
          (Some "deadbeef")
          None
          (Some @"C:\repo\App.fs")
          [||]
          @"C:\repo\App.fs"
          "file"
          "module App"

      request |> Expect.isSome "exact active-file match should still work even if the active session directory has not been cached yet"

    testCase "resolveSessionOwnership returns unique match for exactly one containing session" <| fun _ ->
      let decision =
        resolveSessionOwnership
          [| session "deadbeef" @"C:\repo-a"
             session "cafe1234" @"C:\repo-b" |]
          @"C:\repo-b\Sub\Other.fs"
          "file"

      decision
      |> Expect.equal "one containing session should route uniquely" (UniqueMatch "cafe1234")

    testCase "resolveSessionOwnership returns ambiguous for overlapping containing sessions" <| fun _ ->
      let decision =
        resolveSessionOwnership
          [| session "deadbeef" @"C:\repo"
             session "cafe1234" @"C:\repo\Sub" |]
          @"C:\repo\Sub\Other.fs"
          "file"

      decision
      |> Expect.equal "overlapping session roots should not auto-pick a winner" (AmbiguousMatch [ "deadbeef"; "cafe1234" ])

    testCase "known session ownership overrides the active-session fallback when a different session uniquely owns the file" <| fun _ ->
      let request =
        tryBuildBufferChangedRequest
          (Some "deadbeef")
          (Some @"C:\repo-a")
          (Some @"C:\repo-a\Active.fs")
          [| session "deadbeef" @"C:\repo-a"
             session "cafe1234" @"C:\repo-b" |]
          @"C:\repo-b\Other.fs"
          "file"
          "module Other"

      request |> Expect.isSome "a uniquely owning background session should be routable"
      request.Value.SessionId |> Expect.equal "unique ownership should beat the active-session fallback" "cafe1234"

    testCase "ambiguous known session ownership refuses to post unsaved content" <| fun _ ->
      tryBuildBufferChangedRequest
        (Some "deadbeef")
        (Some @"C:\repo")
        (Some @"C:\repo\Active.fs")
        [| session "deadbeef" @"C:\repo"
           session "cafe1234" @"C:\repo\Sub" |]
        @"C:\repo\Sub\Other.fs"
        "file"
        "module Other"
      |> Expect.isNone "ambiguous ownership should not be guessed"

    testCase "route path is session-scoped" <| fun _ ->
      bufferChangedPath "deadbeef"
      |> Expect.equal "client should post to the session-scoped buffer route" "/api/sessions/deadbeef/buffer-changed"

    // Issue #132: results went stale about a second after every eval with no edit.
    testCase "WHY — resultStalenessFor — a write to the SageFs output channel keeps results because it is not an edit to the code" <| fun _ ->
      resultStalenessFor "output" "extension-output-sagefs.sagefs-#1-SageFs" 1 (Some @"C:\repo\App.fs")
      |> Expect.equal "output channel appends keep results" ResultStaleness.KeepResults

    testCase "WHY — resultStalenessFor — an edit to the F# file on screen marks its results stale because they may no longer match the code" <| fun _ ->
      resultStalenessFor "file" @"C:\repo\App.fs" 1 (Some @"C:\repo\App.fs")
      |> Expect.equal "a real edit marks stale" ResultStaleness.MarkStale

    testCase "WHY — resultStalenessFor — a change event without content changes keeps results because saving or a dirty-flag flip changes no code" <| fun _ ->
      resultStalenessFor "file" @"C:\repo\App.fs" 0 (Some @"C:\repo\App.fs")
      |> Expect.equal "no content change keeps results" ResultStaleness.KeepResults

    testCase "WHY — resultStalenessFor — an edit to another F# file keeps the results on screen because they belong to a different file" <| fun _ ->
      resultStalenessFor "file" @"C:\repo\Other.fs" 1 (Some @"C:\repo\App.fs")
      |> Expect.equal "another file keeps results" ResultStaleness.KeepResults
  ]

Expecto.Tests.runTestsWithCLIArgs [] [||] tests
