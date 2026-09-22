/// Which SDK the FSI host is built with, when the project's SDK doesn't live
/// in the default install.
///
/// This is the bug an F# compiler contributor would have hit on day one:
/// dotnet/fsharp (and most Arcade repos) ship their SDK inside the checkout
/// and point global.json at it with `"paths": [".dotnet", "$host$"]`. SageFs
/// resolved that version correctly from the project's directory, then tried to
/// build its host somewhere else, where the repo's .dotnet isn't on the search
/// path, and failed with "A compatible .NET SDK was not found".
module SageFs.Tests.FsiHostSdkRootTests

open System.IO
open Expecto
open Expecto.Flip
open SageFs.FsiHostBuild

/// The real shape of `dotnet --list-sdks` run inside an Arcade checkout: the
/// repo's own SDK sits between the globally installed ones.
let private arcadeListing =
  String.concat
    "\n"
    [ "10.0.401 [/home/will/.dotnet/sdk]"
      "11.0.100-rc.1.26420.103 [/home/will/Work/fsharp/.dotnet/sdk]"
      "11.0.100-rc.1.26425.128 [/home/will/.dotnet/sdk]" ]

[<Tests>]
let sdkRootTests =
  testList "FSI host SDK root" [
    testCase "a repo-local SDK resolves to the repo's own dotnet root" <| fun _ ->
      sdkRootOf "11.0.100-rc.1.26420.103" arcadeListing
      |> Expect.equal
        "the SDK the project pinned lives in the checkout, not the global install"
        (Some(Path.Combine("/home/will/Work/fsharp", ".dotnet")))

    testCase "a globally installed SDK resolves to the global root" <| fun _ ->
      sdkRootOf "10.0.401" arcadeListing
      |> Expect.equal "nothing special about this one" (Some(Path.Combine("/home/will", ".dotnet")))

    testCase "an SDK that isn't listed has no root" <| fun _ ->
      sdkRootOf "9.9.9" arcadeListing |> Expect.isNone "we must not invent a path for an SDK that isn't there"

    testCase "a listing with no versions has no root" <| fun _ ->
      sdkRootOf "10.0.401" "" |> Expect.isNone "an empty listing says nothing"

    testCase "a path containing brackets still parses" <| fun _ ->
      sdkRootOf "10.0.401" "10.0.401 [/home/will/[odd] dir/.dotnet/sdk]"
      |> Expect.equal "the last bracket closes the path" (Some(Path.Combine("/home/will/[odd] dir", ".dotnet")))

    testCase "no root means the muxer we already have" <| fun _ ->
      SdkSelection.muxer "/usr/bin/dotnet" { Version = "10.0.401"; DotnetRoot = None }
      |> Expect.equal "nothing to switch to" "/usr/bin/dotnet"

    testCase "a root with no dotnet in it falls back rather than failing" <| fun _ ->
      let empty = Path.Combine(Path.GetTempPath(), "sagefs-sdkroot-" + System.Guid.NewGuid().ToString("N"))
      Directory.CreateDirectory empty |> ignore
      try
        SdkSelection.muxer "/usr/bin/dotnet" { Version = "10.0.401"; DotnetRoot = Some empty }
        |> Expect.equal "a root without a muxer is no better than what we had" "/usr/bin/dotnet"
      finally
        Directory.Delete(empty, true)

    testCase "a root that really holds a dotnet is used" <| fun _ ->
      let root = Path.Combine(Path.GetTempPath(), "sagefs-sdkroot-" + System.Guid.NewGuid().ToString("N"))
      Directory.CreateDirectory root |> ignore
      let muxer = Path.Combine(root, (if System.OperatingSystem.IsWindows() then "dotnet.exe" else "dotnet"))
      File.WriteAllText(muxer, "")
      try
        SdkSelection.muxer "/usr/bin/dotnet" { Version = "11.0.100"; DotnetRoot = Some root }
        |> Expect.equal "build with the SDK the project asked for" muxer
      finally
        Directory.Delete(root, true)
  ]
