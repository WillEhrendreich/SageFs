# Arcade repos and repos with many projects

This page is for a repo like dotnet/fsharp: dozens of `.fsproj` files, an SDK that lives inside the checkout, and build output under `artifacts/` instead of `bin/`. It says what SageFs does there, read from the code, and what I have not verified. I have not run a session on dotnet/fsharp for this page.

## Install the repo's SDK first

An Arcade repo ships its own SDK and points `global.json` at it, with `"paths": [".dotnet", "$host$"]`. SageFs asks `dotnet --version` from the project's folder, so that `global.json` decides the SDK. It then finds which install owns that SDK from `dotnet --list-sdks` and builds the session's host with that install's `dotnet` ([`SdkSelection`](../SageFs.Core/FsiHostBuild.fs)). SageFs does not install the repo's SDK. If the repo's `.dotnet` folder is empty, run the repo's own bootstrap first (the `errorMessage` in its `global.json` names it, `./eng/common/dotnet.sh` in dotnet/fsharp), then create the session. Without it you get "Could not determine the .NET SDK" or "Make sure that SDK is installed", and both mean this.

## Where SageFs looks for build output

A project in an Arcade repo has no `bin/` of its own. SageFs walks up from the project's folder, at most eight levels, looking for `artifacts/bin/<ProjectName>/`, and uses `artifacts/obj/<ProjectName>/` for the restore files ([`arcadeDir`](../SageFs.Core/ProjectLoading.fs), [`BuildPreflight`](../SageFs.Core/BuildPreflight.fs)). If the project was never built, the session build runs first, as in any other repo. A missing-DLL error lists every path it checked with the framework it expected, so you can tell "not built" from "looked in the wrong place".

## Picking a project among many

`get_available_projects` sorts the `.fsproj` files it finds under the working directory, drops noise folders, shows the first 40 and ends with "and N more (pass working_directory to narrow the search)" ([`availableProjectsDisplayCap`](../SageFs/Mcp.fs)). In a repo with more than 40, pass the `working_directory` of the part you work on. Create one session for the project you edit. In dotnet/fsharp the compiler is `src/Compiler/FSharp.Compiler.Service.fsproj` (I checked the path in a local checkout and did not load it).

## Warmup takes longer than the status wait

`get_session_status` with `wait_seconds` waits at most 60 seconds, and a longer request is clamped to 60 ([`Timeouts.statusWaitCap`](../SageFs.Core/Timeouts.fs)). A big repo can need several waits. When the reply says `TimedOut`, the session is still warming. Call again, and read the elapsed time and the last progress line in the reply. A session that is moving is not stuck, so do not create a second session or run `dotnet build` beside it. If it ends `Faulted`, the reply names the reason. The warmups I have measured are 5 to 12.5 seconds for projects up to 86,000 lines ([`repl-loop-benchmarks.md`](repl-loop-benchmarks.md)). I have no timing for the compiler repo.

## An SDK 11 host and a `net10.0` project

On a machine with SDK 11 installed and no `global.json`, a `net10.0` project builds with the newest SDK. Issue [#183](https://github.com/WillEhrendreich/SageFs/issues/183) is open: on one Mac that layout faulted in warmup with an error that named no cause, and the same layout loaded on Linux. Pin the SDK you want with a `global.json` in the repo, as Arcade repos already do.

## Known gaps

- I wrote this page from the code and from a local dotnet/fsharp checkout's `global.json` and layout. I did not create a session on that repo for it, so the compiler repo is an expected target and not a tested one.
- The status reply does not estimate how long warmup has left.
