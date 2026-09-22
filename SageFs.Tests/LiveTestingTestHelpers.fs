module SageFs.Tests.LiveTestingTestHelpers

open System
open System.Reflection
open Expecto
open SageFs
open SageFs.Features.LiveTesting

/// Resolve path to a project output DLL. In SageFs FSI the test DLL is shadow-copied,
/// so BaseDirectory points to the tool store, not the test bin.  Fall back to the known
/// project output directory when the DLL isn't beside the tool executable.
let resolveTestDll (dllName: string) =
  let basePath = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, dllName)
  if System.IO.File.Exists basePath then basePath
  else
    let projBinPath =
      System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(
          System.IO.Path.GetDirectoryName(
            System.IO.Path.GetDirectoryName(
              System.IO.Path.GetDirectoryName(
                System.IO.Path.GetDirectoryName(
                  System.AppDomain.CurrentDomain.BaseDirectory))))),
        "SageFs.Tests", "bin", "Debug", "net11.0", dllName)
    if System.IO.File.Exists projBinPath then projBinPath
    else
      // Last resort: find it among loaded assemblies
      System.AppDomain.CurrentDomain.GetAssemblies()
      |> Array.tryFind (fun a ->
        not (System.String.IsNullOrEmpty a.Location)
        && System.IO.Path.GetFileName(a.Location) = dllName)
      |> Option.map (fun a -> a.Location)
      |> Option.defaultValue basePath

let mkTestId name fw = TestId.create name fw
let ts (ms: float) = TimeSpan.FromMilliseconds ms

let mkTestCase name fw category =
  { Id = TestId.create name fw
    FullName = name; DisplayName = name
    Origin = TestOrigin.ReflectionOnly
    Labels = []; Framework = fw; Category = category }

let mkResult testId result =
  { TestId = testId; TestName = TestId.value testId
    Result = result; Timestamp = DateTimeOffset.UtcNow; Output = None }

let mkAssemblyInfo name (refs: string list) =
  { Name = name; Location = sprintf "/%s.dll" name
    ReferencedAssemblies =
      refs |> List.map (fun r -> AssemblyName(r)) |> Array.ofList }


// Alias to avoid FsCheck.TestResult collision
type LTTestResult = TestResult
