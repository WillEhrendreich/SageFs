namespace SageFs

open System

[<RequireQualifiedAccess>]
type TestFramework =
  | Expecto
  | XUnit
  | NUnit
  | MSTest
  | TUnit
  | Unknown of string

module TestFramework =
  let toString = function
    | TestFramework.Expecto -> "expecto"
    | TestFramework.XUnit -> "xunit"
    | TestFramework.NUnit -> "nunit"
    | TestFramework.MSTest -> "mstest"
    | TestFramework.TUnit -> "tunit"
    | TestFramework.Unknown value -> value

  let parse = function
    | "expecto" -> TestFramework.Expecto
    | "xunit" -> TestFramework.XUnit
    | "nunit" -> TestFramework.NUnit
    | "mstest" -> TestFramework.MSTest
    | "tunit" -> TestFramework.TUnit
    | value -> TestFramework.Unknown value

[<RequireQualifiedAccess>]
type TestExecutionMechanism =
  | Reflection
  | PlatformAdapter

type TestProviderCapability = {
  Id: string
  Framework: TestFramework
  PackageMarkers: string list
  AssemblyMarkers: string list
  ExecutableAttributes: string list
  SourceAttributes: string list
  TheoryAttributes: string list
  Execution: TestExecutionMechanism
}

type ProviderEvidence = {
  ReferencedAssemblies: string list
  LoadedAssemblies: string list
}

[<RequireQualifiedAccess>]
type ProviderAvailability =
  | Referenced of capability: TestProviderCapability
  | Loaded of capability: TestProviderCapability

module TestProviderCatalog =
  let xunitV2 =
    { Id = "xunit-v2"
      Framework = TestFramework.XUnit
      PackageMarkers = [ "xunit" ]
      AssemblyMarkers = [ "xunit.core" ]
      ExecutableAttributes = [ "Fact"; "Theory"; "Property" ]
      SourceAttributes = [ "Fact"; "Theory"; "Property" ]
      TheoryAttributes = [ "Theory" ]
      Execution = TestExecutionMechanism.Reflection }

  let xunitV3 =
    { xunitV2 with
        Id = "xunit-v3"
        PackageMarkers = [ "xunit.v3" ]
        AssemblyMarkers = [ "xunit.v3.core" ] }

  let nunit =
    { Id = "nunit"
      Framework = TestFramework.NUnit
      PackageMarkers = [ "NUnit" ]
      AssemblyMarkers = [ "nunit.framework" ]
      ExecutableAttributes = [ "Test" ]
      SourceAttributes = [ "Test"; "TestCase"; "TestCaseSource" ]
      TheoryAttributes = []
      Execution = TestExecutionMechanism.Reflection }

  let mstest =
    { Id = "mstest"
      Framework = TestFramework.MSTest
      PackageMarkers = [ "MSTest.TestFramework" ]
      AssemblyMarkers = [ "Microsoft.VisualStudio.TestPlatform.TestFramework" ]
      ExecutableAttributes = [ "TestMethod" ]
      SourceAttributes = [ "TestMethod"; "DataTestMethod" ]
      TheoryAttributes = []
      Execution = TestExecutionMechanism.Reflection }

  let tunit =
    { Id = "tunit"
      Framework = TestFramework.TUnit
      PackageMarkers = [ "TUnit" ]
      AssemblyMarkers = [ "TUnit.Core" ]
      ExecutableAttributes = [ "Test" ]
      SourceAttributes = [ "Test" ]
      TheoryAttributes = []
      Execution = TestExecutionMechanism.Reflection }

  let expecto =
    { Id = "expecto"
      Framework = TestFramework.Expecto
      PackageMarkers = [ "Expecto"; "YoloDev.Expecto.TestSdk" ]
      AssemblyMarkers = [ "Expecto" ]
      ExecutableAttributes = []
      SourceAttributes = [ "Tests" ]
      TheoryAttributes = []
      Execution = TestExecutionMechanism.Reflection }

  let all = [ xunitV2; xunitV3; nunit; mstest; tunit; expecto ]

  let infrastructurePackageMarkers =
    [ "Microsoft.NET.Test.Sdk"
      "Microsoft.Testing.Platform"
      "Microsoft.Testing.Platform.MSBuild"
      "YoloDev.Expecto.TestSdk" ]

  let testProjectPackageMarkers =
    ((all |> List.collect (fun capability -> capability.PackageMarkers) |> List.distinct)
     @ infrastructurePackageMarkers)
    |> List.distinct

  let isTestPackageName (name: string) =
    testProjectPackageMarkers
    |> List.exists (fun prefix -> name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))

  let containsCI (name: string) (candidates: string list) =
    candidates |> List.exists ((=) name)

  let detect (evidence: ProviderEvidence) : ProviderAvailability list =
    let referenced = Set.ofList evidence.ReferencedAssemblies
    let loaded = Set.ofList evidence.LoadedAssemblies
    all
    |> List.choose (fun capability ->
      let assemblyMarkers = capability.AssemblyMarkers
      if assemblyMarkers |> List.exists (fun marker -> Set.contains marker referenced) then
        if assemblyMarkers |> List.exists (fun marker -> Set.contains marker loaded) then
          Some(ProviderAvailability.Loaded capability)
        else
          Some(ProviderAvailability.Referenced capability)
      else
        None)
