#r "nuget: Fake.Core.Target, 6.1.3"
#r "nuget: Fake.Core.Trace, 6.1.3"
#r "nuget: Fake.Core.Process, 6.1.3"
#r "nuget: Fake.DotNet.Cli, 6.1.3"
#r "nuget: Fake.IO.FileSystem, 6.1.3"

open System
open System.IO
open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO

if not (Context.isFakeContext ()) then
    let executionContext = Context.FakeExecutionContext.Create false "build.fsx" []
    Context.RuntimeContext.Fake executionContext |> Context.setExecutionContext

let rootDir = Path.GetFullPath __SOURCE_DIRECTORY__
let mcpSdkDir = Path.Combine(rootDir, "mcp-sdk")
let mcpNupkgDir = Path.Combine(rootDir, "mcp-sdk-nupkg")
let sageFsProject = Path.Combine(rootDir, "SageFs")
let sageFsNupkgDir = Path.Combine(sageFsProject, "nupkg")

let mcpSubProjects =
    [ "ModelContextProtocol.Core"
      "ModelContextProtocol"
      "ModelContextProtocol.AspNetCore" ]

let cliArgs =
    let commandLineArgs = Environment.GetCommandLineArgs() |> Array.toList

    match commandLineArgs |> List.tryFindIndex ((=) "--") with
    | Some separatorIndex -> commandLineArgs |> List.skip (separatorIndex + 1)
    | None -> commandLineArgs |> List.skip 1

let requestedTarget =
    let rec loop args =
        match args with
        | "--target" :: target :: _ -> target
        | "-t" :: target :: _ -> target
        | arg :: _ when arg.StartsWith "--target=" -> arg.Substring "--target=".Length
        | _ :: rest -> loop rest
        | [] -> "Build"

    loop cliArgs

let private runDotNetCommand command args =
    let result = DotNet.exec id command args

    if not result.OK then
        failwithf "dotnet %s failed with args: %s" command args

Target.create "FetchMcpSdk" (fun _ ->
    if Directory.Exists mcpSdkDir then
        Trace.log "mcp-sdk/ already exists, skipping clone"
    else
        let result =
            CreateProcess.fromRawCommand
                "git"
                [ "clone"; "--depth"; "1"; "https://github.com/WillEhrendreich/ModelContextProtocolSdk.git"; mcpSdkDir ]
            |> Proc.run

        if result.ExitCode <> 0 then
            failwith "Failed to clone ModelContextProtocolSdk")

Target.create "PackMcpSdk" (fun _ ->
    for sub in mcpSubProjects do
        let project = Path.Combine(mcpSdkDir, "src", sub)
        runDotNetCommand "pack" $"\"{project}\" -o \"{mcpNupkgDir}\" -c Release /p:SignAssembly=false")

Target.create "Build" (fun _ -> runDotNetCommand "build" $"\"{rootDir}\"")

Target.create "PackCli" (fun _ ->
    runDotNetCommand "pack" $"\"{sageFsProject}\" -c Release"
    Trace.log $"Packed SageFs CLI to %s{sageFsNupkgDir}")

Target.create "Test" (fun _ ->
    let testsProject = Path.Combine(rootDir, "SageFs.Tests")
    let result = DotNet.exec id "run" $"--no-build --project \"{testsProject}\" -- --summary"

    if result.ExitCode <> 0 && result.ExitCode <> 2 then
        failwithf "Tests failed with exit code %d" result.ExitCode)

Target.create "InstallCli" (fun _ ->
    let updateResult =
        DotNet.exec id "tool" $"update --global SageFs --add-source \"{sageFsNupkgDir}\" --no-cache --ignore-failed-sources"

    if updateResult.OK then
        Trace.log "Updated global SageFs."
    else
        runDotNetCommand
            "tool"
            $"install --global SageFs --add-source \"{sageFsNupkgDir}\" --no-cache --ignore-failed-sources"

        Trace.log "Installed global SageFs.")

"FetchMcpSdk" ==> "PackMcpSdk" ==> "Build"
"Build" ==> "PackCli" ==> "InstallCli"
"Build" ==> "Test"

Target.runOrDefault requestedTarget
