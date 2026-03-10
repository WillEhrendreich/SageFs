module SageFs.VisualStudio.Core.Tests.DaemonTargetFinderTests

open System.IO
open Xunit
open SageFs.VisualStudio.Core

let private createTempDir () =
  let dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
  Directory.CreateDirectory(dir) |> ignore
  dir

let private createFile (dir: string) (segments: string list) =
  let path = Path.Combine(dir :: segments |> List.toArray)
  let parent = Path.GetDirectoryName(path)
  if not (Directory.Exists(parent)) then
    Directory.CreateDirectory(parent) |> ignore
  File.WriteAllText(path, "")
  path

let private assertOk expected (result: Result<string, string>) =
  match result with
  | Ok path -> Assert.Equal(expected, path)
  | Error msg -> Assert.Fail($"Expected Ok \"{expected}\" but got Error: {msg}")

let private assertError (substring: string) (result: Result<string, string>) =
  match result with
  | Error msg -> Assert.Contains(substring, msg)
  | Ok path -> Assert.Fail($"Expected Error containing \"{substring}\" but got Ok: {path}")

let private assertIsOk (result: Result<string, string>) =
  match result with
  | Ok _ -> ()
  | Error msg -> Assert.Fail($"Expected Ok but got Error: {msg}")

[<Fact>]
let ``Single .slnx returns it`` () =
  let dir = createTempDir ()
  try
    let expected = createFile dir ["MySolution.slnx"]
    DaemonTargetFinder.findTarget dir |> assertOk expected
  finally
    Directory.Delete(dir, true)

[<Fact>]
let ``Multiple .fsproj one contains Test returns the test project`` () =
  let dir = createTempDir ()
  try
    createFile dir ["src"; "App.fsproj"] |> ignore
    let testProj = createFile dir ["tests"; "App.Tests.fsproj"]
    DaemonTargetFinder.findTarget dir |> assertOk testProj
  finally
    Directory.Delete(dir, true)

[<Fact>]
let ``Multiple .fsproj none contain Test returns Ok with a valid fsproj`` () =
  let dir = createTempDir ()
  try
    createFile dir ["Alpha.fsproj"] |> ignore
    createFile dir ["sub"; "Beta.fsproj"] |> ignore
    match DaemonTargetFinder.findTarget dir with
    | Ok path -> Assert.EndsWith(".fsproj", path)
    | Error msg -> Assert.Fail($"Expected Ok but got Error: {msg}")
  finally
    Directory.Delete(dir, true)

[<Fact>]
let ``No .fsproj found returns Error with descriptive message`` () =
  let dir = createTempDir ()
  try
    DaemonTargetFinder.findTarget dir |> assertError "No F# projects found"
  finally
    Directory.Delete(dir, true)

[<Fact>]
let ``Multiple .slnx returns first alphabetically`` () =
  let dir = createTempDir ()
  try
    let first = createFile dir ["Alpha.slnx"]
    createFile dir ["Zeta.slnx"] |> ignore
    DaemonTargetFinder.findTarget dir |> assertOk first
  finally
    Directory.Delete(dir, true)

[<Fact>]
let ``Single .fsproj returns it`` () =
  let dir = createTempDir ()
  try
    let expected = createFile dir ["MyApp.fsproj"]
    DaemonTargetFinder.findTarget dir |> assertOk expected
  finally
    Directory.Delete(dir, true)

[<Fact>]
let ``Nonexistent directory returns Error with descriptive message`` () =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-nonexistent-" + Path.GetRandomFileName())
  // Ensure the directory truly does not exist
  if Directory.Exists(dir) then Directory.Delete(dir, true)
  DaemonTargetFinder.findTarget dir |> assertError "Directory not found"

  let dir = createTempDir ()
  try
    createFile dir ["Solution.sln"] |> ignore
    let slnx = createFile dir ["Solution.slnx"]
    DaemonTargetFinder.findTarget dir |> assertOk slnx
  finally
    Directory.Delete(dir, true)

