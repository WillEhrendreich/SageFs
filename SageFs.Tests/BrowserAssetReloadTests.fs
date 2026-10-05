module SageFs.Tests.BrowserAssetReloadTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

let baseDirectory = Path.Combine(Path.GetTempPath(), "browser-asset-routing")

let configuration =
  """{"build":{"executable":"dotnet","arguments":["build","Client/Client.fsproj"],"workingDirectory":"."},"sourceRoots":["Client"],"assetRoots":[{"directory":"Host/wwwroot/assets","pattern":"*.js"}]}"""

let configured () =
  match BrowserAssetReload.parseJson baseDirectory configuration with
  | Ok config -> config
  | Error error -> failtestf "Fixture configuration failed: %A" error

let change kind path : FileWatcher.FileChange =
  { FilePath = Path.Combine(baseDirectory, path)
    Kind = kind
    Timestamp = DateTimeOffset.UnixEpoch }

[<Tests>]
let tests =
  testList "BrowserAssetReload" [
    testCase "missing configuration preserves ordinary project behavior" <| fun () ->
      let missingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
      BrowserAssetReload.load missingDirectory
      |> Expect.equal "No configuration means no asset pipeline." (Ok None)

    testCase "malformed configuration is refused instead of silently using CLR reload" <| fun () ->
      BrowserAssetReload.parseJson baseDirectory "{"
      |> Expect.isError "Malformed JSON must fail at the configuration boundary."
      BrowserAssetReload.parseJson baseDirectory "null"
      |> Expect.isError "Null is not an enabled pipeline."

    testCase "client sources use the asset route for each file event kind" <| fun () ->
      let config = Some (configured ())
      for kind in [ FileWatcher.FileChangeKind.Changed; FileWatcher.FileChangeKind.Created;
                    FileWatcher.FileChangeKind.Deleted; FileWatcher.FileChangeKind.Renamed ] do
        let saved = change kind "Client/Client.fs"
        BrowserAssetReload.route config saved
        |> Expect.equal "A client save must not evaluate browser code in FSI."
          (BrowserAssetReload.SaveRoute.BrowserAssets saved.FilePath)

    testCase "client project changes rebuild browser assets without resetting the host" <| fun () ->
      let saved = change FileWatcher.FileChangeKind.Changed "Client/Client.fsproj"
      BrowserAssetReload.route (Some (configured ())) saved
      |> Expect.equal "The client compile list belongs to the asset build."
        (BrowserAssetReload.SaveRoute.BrowserAssets saved.FilePath)

    testCase "host and unconfigured sources retain the existing reload route" <| fun () ->
      let host = change FileWatcher.FileChangeKind.Changed "Host/Program.fs"
      let client = change FileWatcher.FileChangeKind.Changed "Client/Client.fs"
      for config, saved in [ Some (configured ()), host; None, client ] do
        BrowserAssetReload.route config saved
        |> Expect.equal "Unowned files retain the original reload behavior."
          (BrowserAssetReload.SaveRoute.Existing (FileWatcher.FileChangeAction.Reload saved.FilePath))

    testCase "ordinary project changes retain the existing reset route" <| fun () ->
      let saved = change FileWatcher.FileChangeKind.Changed "Host/Host.fsproj"
      BrowserAssetReload.route (Some (configured ())) saved
      |> Expect.equal "A server project change still needs the normal reset."
        (BrowserAssetReload.SaveRoute.Existing FileWatcher.FileChangeAction.SoftReset)

    testCase "unchanged published assets do not refresh the browser" <| fun () ->
      let event = BrowserAssetReload.outcome "Client.fs" BrowserAssets.BuildResult.Unchanged
      event |> DevReload.DevReloadEvent.refreshes
      |> Expect.isFalse "A successful no-op build is not a page reload."
      match event with
      | DevReload.NotApplied report ->
        report.Message |> Expect.stringContains "The report identifies browser bytes instead of CLR declarations." "Served browser assets are unchanged."
      | other -> failtestf "Expected unchanged browser assets, received %A" other

    testCase "asset build failures report an error without refreshing the browser" <| fun () ->
      let failure = BrowserAssets.BuildFailure.BuildFailed (1, "client compilation failed")
      let event = BrowserAssetReload.outcome "Client.fs" (BrowserAssets.BuildResult.Failed failure)
      event |> DevReload.DevReloadEvent.refreshes |> Expect.isFalse "Failed output must not trigger a page reload."
      match event with
      | DevReload.CompilationFailed(summary, report, _) ->
        summary |> Expect.stringContains "The compiler failure remains visible." "client compilation failed"
        report.Outcome |> Expect.equal "The terminal event states failure." "CompileFailed"
      | other -> failtestf "Expected a build failure, received %A" other

    testCase "changed assets refresh without claiming a CLR patch" <| fun () ->
      let result = BrowserAssets.BuildResult.Changed { ContentHash = "version-two"; AssetCount = 2 }
      let event = BrowserAssetReload.outcome "Client.fs" result
      event |> DevReload.DevReloadEvent.refreshes |> Expect.isTrue "New served bytes require a refresh."
      match event with
      | DevReload.AssetsRebuilt(report, sourceFile, assetCount, contentHash) ->
        report.Patched |> Expect.equal "No CLR methods were patched." 0
        report.Considered |> Expect.equal "No CLR methods were considered." 0
        sourceFile |> Expect.equal "Source attribution is preserved." "Client.fs"
        assetCount |> Expect.equal "The snapshot count is preserved." 2
        contentHash |> Expect.equal "The snapshot identity is preserved." "version-two"
      | other -> failtestf "Expected an asset event, received %A" other
  ]
