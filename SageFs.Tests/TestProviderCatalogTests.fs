module SageFs.Tests.TestProviderCatalogTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Features.LiveTesting

let private descriptionsEqual (left: ProviderDescription) (right: ProviderDescription) =
  match left, right with
  | ProviderDescription.AttributeBased a, ProviderDescription.AttributeBased b ->
    a.Name = b.Name
    && a.AssemblyMarker = b.AssemblyMarker
    && a.TestAttributes = b.TestAttributes
  | ProviderDescription.Custom a, ProviderDescription.Custom b ->
    a.Name = b.Name && a.AssemblyMarker = b.AssemblyMarker
  | _ -> false

let private markerOf (description: ProviderDescription) =
  match description with
  | ProviderDescription.AttributeBased value -> value.AssemblyMarker
  | ProviderDescription.Custom value -> value.AssemblyMarker

[<Tests>]
let tests =
  testList "TestProviderCatalog" [
    test "provider ids are unique and every capability has a complete executable surface" {
      let ids = TestProviderCatalog.all |> List.map (fun capability -> capability.Id)
      ids |> List.distinct |> Expect.equal "unique provider ids" ids
      for capability in TestProviderCatalog.all do
        capability.AssemblyMarkers |> Expect.isNonEmpty (sprintf "%s has an assembly marker" capability.Id)
        capability.ExecutableAttributes
        |> List.forall (fun attribute -> List.contains attribute capability.SourceAttributes)
        |> Expect.isTrue (sprintf "%s executable attributes are source-visible" capability.Id)
        capability.TheoryAttributes
        |> List.forall (fun attribute -> List.contains attribute capability.ExecutableAttributes)
        |> Expect.isTrue (sprintf "%s theory attributes are executable" capability.Id)
    }

    testPropertyWithConfig FsCheckConfig.defaultConfig "catalog package markers classify test projects" <|
      fun (NonNegativeInt index) ->
        let capability = TestProviderCatalog.all |> List.item (index % List.length TestProviderCatalog.all)
        capability.PackageMarkers
        |> List.forall TestProviderCatalog.isTestPackageName
        |> Expect.isTrue (sprintf "%s package markers are test-project markers" capability.Id)

    test "Microsoft Testing Platform is infrastructure, not a fake framework" {
      TestProviderCatalog.isTestPackageName "Microsoft.Testing.Platform" |> Expect.isTrue "test project"
      TestProviderCatalog.isTestPackageName "Microsoft.Testing.Platform.MSBuild" |> Expect.isTrue "test project"
      let detected =
        TestProviderCatalog.detect {
          ReferencedAssemblies = [ "Microsoft.Testing.Platform" ]
          LoadedAssemblies = [ "Microsoft.Testing.Platform" ]
        }
      detected |> Expect.hasLength "platform alone exposes no framework executor" 0
    }

    test "TUnit and YoloDev Expecto now classify their projects" {
      TestProviderCatalog.isTestPackageName "TUnit.Core" |> Expect.isTrue "TUnit"
      TestProviderCatalog.isTestPackageName "YoloDev.Expecto.TestSdk" |> Expect.isTrue "MTP-based Expecto"
    }

    test "project classification and workflow detection use the same package vocabulary" {
      let packages = TestProviderCatalog.testProjectPackageMarkers
      packages
      |> List.forall (fun package -> WorkflowTypes.WorkflowDetection.isTestPackageSet [ package ])
      |> Expect.isTrue "workflow detector agrees with the shared catalog"
    }

    test "provider descriptions and runtime executors project the same capability data" {
      let descriptions = TestProviderDescriptions.builtInDescriptions
      descriptions
      |> List.forall (fun description ->
        TestProviderCatalog.all
        |> List.exists (fun capability -> markerOf description = capability.AssemblyMarkers.Head))
      |> Expect.isTrue "every description comes from the capability catalog"

      let executorCapabilities: TestProviderCapability list =
        BuiltInExecutors.descriptions
        |> List.map (fun description ->
          TestProviderCatalog.all
          |> List.find (fun capability -> markerOf description = capability.AssemblyMarkers.Head))
      let parity =
        List.forall2 (fun (capability: TestProviderCapability) (description: ProviderDescription) ->
          match description with
          | ProviderDescription.AttributeBased value ->
            value.Name = capability.Framework
            && value.AssemblyMarker = capability.AssemblyMarkers.Head
            && value.TestAttributes = capability.ExecutableAttributes
          | ProviderDescription.Custom value ->
            value.Name = capability.Framework
            && value.AssemblyMarker = capability.AssemblyMarkers.Head)
          executorCapabilities
          descriptions
      parity |> Expect.isTrue "runtime executor descriptions cannot drift"
    }

    test "source-only attributes are not advertised as executable" {
      let nunit =
        TestProviderDescriptions.builtInDescriptions
        |> List.find (fun description -> markerOf description = "nunit.framework")
      match nunit with
      | ProviderDescription.AttributeBased value ->
        value.TestAttributes |> Expect.equal "only Test is currently executable" [ "Test" ]
        value.TestAttributes |> List.contains "TestCaseSource" |> Expect.isFalse "source-only data source"
      | ProviderDescription.Custom _ -> failtest "NUnit must be attribute-based"
    }

    test "reference and load evidence produce closed availability states" {
      let referenced =
        TestProviderCatalog.detect {
          ReferencedAssemblies = [ "xunit.core" ]
          LoadedAssemblies = []
        }
      referenced |> List.forall (function ProviderAvailability.Referenced _ -> true | _ -> false)
      |> Expect.isTrue "referenced only"

      let loaded =
        TestProviderCatalog.detect {
          ReferencedAssemblies = [ "xunit.core" ]
          LoadedAssemblies = [ "xunit.core" ]
        }
      loaded |> List.forall (function ProviderAvailability.Loaded _ -> true | _ -> false)
      |> Expect.isTrue "loaded"
    }

    testPropertyWithConfig FsCheckConfig.defaultConfig "a capability is loaded only when a marker is both referenced and loaded" <|
      fun (PositiveInt index) ->
        let capability = TestProviderCatalog.all |> List.item ((index - 1) % List.length TestProviderCatalog.all)
        let marker = capability.AssemblyMarkers.Head
        TestProviderCatalog.detect {
          ReferencedAssemblies = [ marker ]
          LoadedAssemblies = []
        }
        |> List.exists (function ProviderAvailability.Referenced value -> value.Id = capability.Id | _ -> false)
        |> Expect.isTrue (sprintf "%s referenced" capability.Id)

        TestProviderCatalog.detect {
          ReferencedAssemblies = [ marker ]
          LoadedAssemblies = [ marker ]
        }
        |> List.exists (function ProviderAvailability.Loaded value -> value.Id = capability.Id | _ -> false)
        |> Expect.isTrue (sprintf "%s loaded" capability.Id)
  ]
