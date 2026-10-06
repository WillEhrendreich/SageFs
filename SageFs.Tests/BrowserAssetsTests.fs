module SageFs.Tests.BrowserAssetsTests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.BrowserAssets

let configRoot = Path.Combine(Path.GetTempPath(), "sagefs-browser-assets-config")

let rawConfig : RawConfig =
  { Build =
      { Executable = "dotnet"
        Arguments = [ "build"; "Client.fsproj" ]
        WorkingDirectory = "." }
    SourceRoots = [ "src" ]
    ExcludedSourceRoots = None
    AssetRoots = [ { Directory = "wwwroot"; Pattern = "*.js" } ] }

let config () =
  match parse configRoot rawConfig with
  | Ok value -> value
  | Error errors -> failtestf "The test configuration is invalid: %A" errors

let snapshot entries =
  entries
  |> List.map (fun (path, content: string) -> path, Encoding.UTF8.GetBytes content)
  |> snapshotEntries

let before = snapshot [ "wwwroot/client.js", "one" ]
let after = snapshot [ "wwwroot/client.js", "two" ]

let expectChanged expected result =
  result |> Expect.equal "The event must identify the published assets." (BuildResult.Changed (fingerprint expected))

[<Tests>]
let browserAssetsTests =
  testList "Browser assets" [
    testCase "parsing retains build arguments as distinct values" <| fun () ->
      let raw = { rawConfig with Build = { rawConfig.Build with Arguments = [ "build"; "Client App.fsproj" ] } }
      match parse configRoot raw with
      | Ok parsed ->
        buildCommand parsed |> commandArguments
        |> Expect.equal "Arguments must not pass through a shell." [ "build"; "Client App.fsproj" ]
        buildCommand parsed |> commandWorkingDirectory
        |> Expect.equal "The working directory is relative to the configuration." configRoot
      | Error errors -> failtestf "Expected a valid configuration: %A" errors

    testCase "parsing refuses incomplete commands roots and patterns" <| fun () ->
      let invalid =
        [ { rawConfig with Build = { rawConfig.Build with Executable = " " } }
          { rawConfig with Build = { rawConfig.Build with WorkingDirectory = "" } }
          { rawConfig with SourceRoots = [] }
          { rawConfig with SourceRoots = [ "" ] }
          { rawConfig with AssetRoots = [] }
          { rawConfig with AssetRoots = [ { Directory = "wwwroot"; Pattern = "../*.js" } ] } ]
      for raw in invalid do
        parse configRoot raw |> Result.isError |> Expect.isTrue "An incomplete configuration must fail before a build."

    testCase "parsing rejects omitted fields and null command arguments" <| fun () ->
      let invalid =
        [ Unchecked.defaultof<RawConfig>, [ ConfigError.Required "browserAssets" ]
          { rawConfig with Build = Unchecked.defaultof<RawBuildCommand> }, [ ConfigError.Required "build" ]
          { rawConfig with Build = { rawConfig.Build with Arguments = Unchecked.defaultof<string list> } },
            [ ConfigError.Required "build.arguments" ]
          { rawConfig with Build = { rawConfig.Build with Arguments = [ "build"; null ] } },
            [ ConfigError.InvalidArgument 1 ]
          { rawConfig with SourceRoots = Unchecked.defaultof<string list> }, [ ConfigError.Required "sourceRoots" ]
          { rawConfig with AssetRoots = Unchecked.defaultof<RawAssetRoot list> }, [ ConfigError.Required "assetRoots" ]
          { rawConfig with AssetRoots = [ Unchecked.defaultof<RawAssetRoot> ] }, [ ConfigError.Required "assetRoots" ] ]
      for raw, expected in invalid do
        parse configRoot raw
        |> Expect.equal "Missing JSON fields must produce a configuration error before effects." (Error expected)

    testCase "source matching excludes sibling paths build output and served assets" <| fun () ->
      let parsed = config ()
      matchesSource parsed (Path.Combine(configRoot, "src", "Client.fs"))
      |> Expect.isTrue "A client source file must use the asset pipeline."
      for segments in
        [ [ "src-other"; "Client.fs" ]; [ "src"; "obj"; "Generated.fs" ]; [ "src"; "bin"; "Generated.fs" ]; [ "wwwroot"; "client.js" ] ] do
        matchesSource parsed (Path.Combine(Array.ofList (configRoot :: segments)))
        |> Expect.isFalse "A path outside the source boundary must not cause a build."
      sourceRoots parsed |> Expect.equal "Watchers need the parsed roots." [ Path.Combine(configRoot, "src") ]

    testCase "configured exclusions reject generated sources without excluding boundary siblings" <| fun () ->
      let raw = { rawConfig with ExcludedSourceRoots = Some [ "src/./generated/../generated" ] }
      let parsed =
        match parse configRoot raw with
        | Ok config -> config
        | Error errors -> failtestf "The exclusion configuration is invalid: %A" errors
      excludedSourceRoots parsed
      |> Expect.equal "Exclusion roots must be normalized at the input boundary." [ Path.Combine(configRoot, "src", "generated") ]
      for relative in [ "src/generated/Client.fs"; "src/generated/deep/Client.fs" ] do
        matchesSource parsed (Path.Combine(configRoot, relative))
        |> Expect.isFalse "Generated source copies must not schedule an asset build."
      for relative in [ "src/Client.fs"; "src/generated-other/Client.fs" ] do
        matchesSource parsed (Path.Combine(configRoot, relative))
        |> Expect.isTrue "An exclusion must not remove ordinary sources or boundary siblings."

    testCase "missing and null exclusion lists preserve source matching" <| fun () ->
      for exclusions in [ None; Some []; Some (Unchecked.defaultof<string list>) ] do
        let raw = { rawConfig with ExcludedSourceRoots = exclusions }
        match parse configRoot raw with
        | Ok parsed ->
          excludedSourceRoots parsed |> Expect.isEmpty "An omitted exclusion list must default to empty."
          matchesSource parsed (Path.Combine(configRoot, "src", "Client.fs"))
          |> Expect.isTrue "Existing configuration must continue to match its sources."
        | Error errors -> failtestf "An optional exclusion list was refused: %A" errors

    testCase "invalid exclusion paths fail before source routing" <| fun () ->
      for path, expected in
        [ "", ConfigError.Required "excludedSourceRoots"
          null, ConfigError.Required "excludedSourceRoots"
          "src/\000generated", ConfigError.InvalidPath ("excludedSourceRoots", "src/\000generated") ] do
        parse configRoot { rawConfig with ExcludedSourceRoots = Some [ path ] }
        |> Expect.equal "Each configured exclusion must be a valid path." (Error [ expected ])

    testCase "content hashing detects edits additions removals and renames" <| fun () ->
      let variants =
        [ after
          snapshot [ "wwwroot/client.js", "one"; "wwwroot/extra.js", "extra" ]
          snapshot []
          snapshot [ "wwwroot/renamed.js", "one" ] ]
      for next in variants do
        let baseline, result = completeBuild before (Ok next)
        baseline |> Expect.equal "A successful build becomes the next baseline." next
        expectChanged next result

    testCase "entry order does not affect the fingerprint" <| fun () ->
      let entries = [ "wwwroot/b.js", "two"; "wwwroot/a.js", "one" ]
      snapshot entries |> Expect.equal "Filesystem enumeration order must not cause a reload." (snapshot (List.rev entries))

    testCase "filesystem snapshots ignore timestamps and unrelated assets but detect deletion" <| fun () ->
      let directory = Directory.CreateTempSubdirectory("sagefs-browser-assets-")
      try
        let assets = Directory.CreateDirectory(Path.Combine(directory.FullName, "wwwroot"))
        let javascript = Path.Combine(assets.FullName, "client.js")
        let stylesheet = Path.Combine(assets.FullName, "client.css")
        File.WriteAllText(javascript, "before")
        File.WriteAllText(stylesheet, "style-before")
        let parsed =
          match parse directory.FullName rawConfig with
          | Ok config -> config
          | Error errors -> failtestf "The filesystem configuration is invalid: %A" errors
        let read () =
          match snapshotFiles parsed with
          | Ok result -> result
          | Error failure -> failtestf "The snapshot failed: %A" failure
        let initial = read ()
        File.SetLastWriteTimeUtc(javascript, DateTime.UnixEpoch)
        File.WriteAllText(stylesheet, "style-after")
        completeBuild initial (Ok (read ())) |> snd
        |> Expect.equal "Only selected asset names and bytes must affect the hash." BuildResult.Unchanged
        File.Delete javascript
        let empty = read ()
        completeBuild initial (Ok empty) |> snd |> expectChanged empty
        (fingerprint empty).AssetCount |> Expect.equal "Complete deletion is a valid change." 0
        Directory.Delete(assets.FullName, true)
        let retained, result = completeBuild empty (Ok (read ()))
        retained |> Expect.equal "A missing output directory must retain the baseline." empty
        result |> Expect.equal "A missing output directory must report its path."
          (BuildResult.Failed (BuildFailure.AssetDirectoryMissing [ assets.FullName ]))
      finally
        Directory.Delete(directory.FullName, true)

    testCase "unchanged output emits no reload" <| fun () ->
      completeBuild before (Ok before)
      |> Expect.equal "A successful build without asset changes must not reload." (before, BuildResult.Unchanged)

    testCase "a failed build keeps the last published snapshot" <| fun () ->
      let failure = BuildFailure.BuildFailed (1, "compile failed")
      let baseline, result = completeBuild before (Error failure)
      result |> Expect.equal "The build failure must be visible." (BuildResult.Failed failure)
      baseline |> Expect.equal "Failed output must not become the published baseline." before
      completeBuild baseline (Ok after) |> snd |> expectChanged after

    testTask "duplicate requests serialize builds and emit one changed result" {
      let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
      let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
      let observed = ref before
      let builds = ref 0
      let active = ref 0
      let overlap = ref false
      let dependencies =
        { Snapshot = fun _ -> Ok observed.Value
          Build = fun _ _ -> task {
            builds.Value <- builds.Value + 1
            active.Value <- active.Value + 1
            if active.Value <> 1 then overlap.Value <- true
            entered.TrySetResult () |> ignore
            do! release.Task
            observed.Value <- after
            active.Value <- active.Value - 1
            return Ok ()
          } }
      let run = createRunner dependencies (config ())
      let first = run CancellationToken.None
      do! entered.Task
      let second = run CancellationToken.None
      release.SetResult ()
      let! results = Task.WhenAll [| first; second |]
      results |> Array.filter (function BuildResult.Changed _ -> true | _ -> false) |> Array.length
      |> Expect.equal "Duplicate file events must produce one reload." 1
      builds.Value |> Expect.equal "Each request must complete its build." 2
      overlap.Value |> Expect.isFalse "A build must not overlap another build."
    }

    testTask "recovery publishes bytes left by a failed build" {
      let observed = ref before
      let attempts = ref 0
      let failure = BuildFailure.BuildFailed (1, "compile failed")
      let dependencies =
        { Snapshot = fun _ -> Ok observed.Value
          Build = fun _ _ -> task {
            attempts.Value <- attempts.Value + 1
            observed.Value <- after
            return if attempts.Value = 1 then Error failure else Ok ()
          } }
      let run = createRunner dependencies (config ())
      let! failed = run CancellationToken.None
      failed |> Expect.equal "A failed build must not emit a reload." (BuildResult.Failed failure)
      let! recovered = run CancellationToken.None
      expectChanged after recovered
      let! repeated = run CancellationToken.None
      repeated |> Expect.equal "The recovered bytes are now published." BuildResult.Unchanged
    }

    testTask "a canceled request does not invoke the compiler" {
      let calls = ref 0
      let dependencies =
        { Snapshot = fun _ -> Ok before
          Build = fun _ _ ->
            calls.Value <- calls.Value + 1
            Task.FromResult(Ok ()) }
      let run = createRunner dependencies (config ())
      let cancellation = new CancellationTokenSource()
      try
        cancellation.Cancel()
        let! result = run cancellation.Token
        result |> Expect.equal "Cancellation must be a typed failure." (BuildResult.Failed BuildFailure.Cancelled)
        calls.Value |> Expect.equal "A canceled request must not start a build." 0
      finally
        cancellation.Dispose()
    }

    testTask "canceling an active build releases the queue for the next request" {
      let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
      let completion = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
      let observed = ref before
      let builds = ref 0
      let firstStopped = ref false
      let overlap = ref false
      let dependencies =
        { Snapshot = fun _ -> Ok observed.Value
          Build = fun _ cancellation -> task {
            builds.Value <- builds.Value + 1
            if builds.Value = 1 then
              entered.SetResult ()
              try
                do! completion.Task.WaitAsync cancellation
              finally
                firstStopped.Value <- true
              return Ok ()
            else
              overlap.Value <- not firstStopped.Value
              observed.Value <- after
              return Ok ()
          } }
      let run = createRunner dependencies (config ())
      let cancellation = new CancellationTokenSource()
      try
        let first = run cancellation.Token
        do! entered.Task
        let next = run CancellationToken.None
        builds.Value |> Expect.equal "The next build must remain queued while the first is active." 1
        cancellation.Cancel()
        let! canceled = first
        canceled |> Expect.equal "An active cancellation must be a typed failure." (BuildResult.Failed BuildFailure.Cancelled)
        let! recovered = next
        expectChanged after recovered
        builds.Value |> Expect.equal "Cancellation must permit the queued build to run." 2
        overlap.Value |> Expect.isFalse "The canceled build must exit before the next build starts."
      finally
        cancellation.Cancel()
        cancellation.Dispose()
        completion.TrySetResult () |> ignore
    }
  ]

let rec schedules length =
  if length = 0 then [ [] ]
  else
    [ for head in [ Ok before; Ok after; Error (BuildFailure.BuildFailed (1, "compile failed")) ] do
        for tail in schedules (length - 1) do
          yield head :: tail ]

let violations decide schedule =
  let _, violations =
    schedule
    |> List.fold (fun (baseline, found) observed ->
      let next, result = decide baseline observed
      let violation =
        match observed, result with
        | Error _, BuildResult.Failed _ when next = baseline -> []
        | Error _, _ -> [ "FAILED-BUILD-KEEPS-BASELINE" ]
        | Ok current, BuildResult.Unchanged when current = baseline && next = current -> []
        | Ok current, BuildResult.Changed change when current <> baseline && next = current && change = fingerprint current -> []
        | Ok _, _ -> [ "ONE-EVENT-PER-CHANGED-SUCCESS" ]
      next, found @ violation) (before, [])
  violations

[<Tests>]
let browserAssetDecisionTests =
  testList "Browser asset decision simulation" [
    testCase "all bounded build schedules preserve publication invariants" <| fun () ->
      let traces = schedules 5
      traces.Length |> Expect.equal "The exploration must cover every five-step schedule." 243
      for trace in traces do
        violations completeBuild trace
        |> Expect.isEmpty (sprintf "Publication invariants failed for %A" trace)

    testCase "the same build schedule produces the same trace" <| fun () ->
      let run trace =
        trace |> List.scan (fun (baseline, _) observed -> completeBuild baseline observed) (before, BuildResult.Unchanged)
      let trace = [ Ok after; Error (BuildFailure.BuildFailed (1, "compile failed")); Ok after; Ok before ]
      run trace |> Expect.equal "The simulation must replay exactly." (run trace)

    testCase "mutants that suppress or duplicate reloads violate the real assertions" <| fun () ->
      let mutants =
        [ "suppress changed output", (fun baseline observed ->
            let next, result = completeBuild baseline observed
            match result with
            | BuildResult.Changed _ -> next, BuildResult.Unchanged
            | _ -> next, result)
          "publish unchanged output", (fun baseline observed ->
            let next, result = completeBuild baseline observed
            match result with
            | BuildResult.Unchanged -> next, BuildResult.Changed (fingerprint next)
            | _ -> next, result)
          "publish failed output", (fun baseline observed ->
            match observed with
            | Error _ -> after, BuildResult.Changed (fingerprint after)
            | Ok _ -> completeBuild baseline observed)
          "retain the stale baseline", (fun baseline observed ->
            let _, result = completeBuild baseline observed
            baseline, result) ]
      for name, mutant in mutants do
        schedules 3 |> List.exists (fun trace -> not (List.isEmpty (violations mutant trace)))
        |> Expect.isTrue (sprintf "The publication assertions must detect this mutant: %s" name)
  ]
