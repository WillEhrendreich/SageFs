# WebSharper browser assets

This sample has two projects. WebSharper compiles the client into JavaScript. An independent ASP.NET Core host serves that output.

The browser-assets prototype runs the client build after a source change. A successful build with changed JavaScript produces `AssetsRebuilt` and reloads the browser.

The host has no project reference to the client. A client rebuild does not require a host restart.

## Prerequisites

- .NET 10 SDK.
- A SageFs build that includes the browser-assets prototype.
- NuGet access for the first restore.

The sample uses WebSharper 10.1.6.676. Its local SDK and package settings are independent of the SageFs build settings.

## Prepare the sample

Run these commands from this directory:

```shell
dotnet build Client/BrowserAssets.Client.fsproj
dotnet build Host/BrowserAssets.Host.fsproj
```

The client build writes JavaScript into `Host/wwwroot/assets`. The generated files are ignored by Git.

## Run with SageFs

1. Create a project session for `Host/BrowserAssets.Host.fsproj`.
2. Set the session working directory to this sample directory.
3. Select the Hot Reload workflow.
4. Call `run_app` for the session.
5. Open the application URL from the response.

The page displays `WebSharper client: version one`. SageFs injects its browser reload script when it starts the host.

The session reads `sagefs.browser-assets.json` from its working directory. That file selects the client sources, build command, and served JavaScript files.

The build command uses `--no-restore`. Repeat the client build manually after a package change.

## Check a client change

1. Read `/host-pid` from the application URL.
2. Change `version one` to `version two` in `Client/Client.fs`.
3. Save the file.
4. Confirm that the page displays `WebSharper client: version two` without a manual refresh.
5. Read `/host-pid` again.
6. Confirm that the process ID did not change.

A successful build with identical JavaScript does not reload the browser. A failed build does not produce `AssetsRebuilt`.

The JavaScript files use `Cache-Control: no-store` to prevent stale responses during this check.

## Prototype limits

This sample reloads the complete page. It does not preserve browser state through JavaScript hot module replacement.

Only the client directory uses the asset build path. Host changes use the existing SageFs hot reload path.

Use source roots only for browser code. Shared browser/server sources need a coordinated rebuild strategy and are outside this prototype.

The prototype watches `.fs`, `.fsx`, and `.fsproj` files. After a `wsconfig.json`, package, or bundler configuration change, run the build manually.

The configuration is read when the session starts. Restart the session after a configuration change.

Each asset build has a five-minute default limit. Set `SAGEFS_BROWSER_ASSET_BUILD_SECONDS` before starting SageFs to change this limit.

A failed build does not reload the page. The build command owns output writes, so the prototype does not restore files that a failed build changed.

The sample does not need a JavaScript bundler. Projects that use a bundler must include it in their configured build command.

The build command must finish after all served JavaScript files are written. Use one build owner for these files.

## Run without SageFs

```shell
dotnet run --project Host/BrowserAssets.Host.fsproj --no-build --urls http://localhost:5087
```

Open `http://localhost:5087`. After a client build, refresh the page manually.
