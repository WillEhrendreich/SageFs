module SageFs.VisualStudio.Core.Tests.TypeExplorerGuardTests

open Xunit
open FsUnit.Xunit

/// Tests for the TypeExplorer daemon-guard logic.
/// The production `TypeExplorerData.ShouldSkipRefresh(bool)` lives in
/// SageFs.VisualStudio (net8.0 VS project) and cannot be referenced here.
/// This file verifies the pure specification inline, mirroring that method.
let private shouldSkipRefresh (daemonReachable: bool) = not daemonReachable

[<Fact>]
let ``ShouldSkipRefresh returns true when daemon not reachable`` () =
  shouldSkipRefresh false |> should equal true

[<Fact>]
let ``ShouldSkipRefresh returns false when daemon reachable`` () =
  shouldSkipRefresh true |> should equal false

[<Fact>]
let ``ShouldSkipRefresh is the negation of reachable`` () =
  [ true; false ]
  |> List.iter (fun r -> shouldSkipRefresh r |> should equal (not r))
