module SageFs.Tests.ArgsParserTests

open Expecto
open Expecto.Flip
open SageFs.Args

[<Tests>]
let argsTests = testList "CLI Args parsing" [
  testCase "empty args returns empty list" <| fun () ->
    parseArgs [||]
    |> Expect.isEmpty "should be empty"

  testCase "--bare flag" <| fun () ->
    parseArgs [| "--bare" |]
    |> Expect.equal "should have Bare" [ Bare ]

  testCase "--no-watch flag" <| fun () ->
    parseArgs [| "--no-watch" |]
    |> Expect.equal "should have No_Watch" [ No_Watch ]

  testCase "--no-resume flag" <| fun () ->
    parseArgs [| "--no-resume" |]
    |> Expect.equal "should have No_Resume" [ No_Resume ]

  testCase "--prune flag" <| fun () ->
    parseArgs [| "--prune" |]
    |> Expect.equal "should have Prune" [ Prune ]

  testCase "--proj with file" <| fun () ->
    parseArgs [| "--proj"; "MyApp.fsproj" |]
    |> Expect.equal "should have Proj" [ Proj "MyApp.fsproj" ]

  testCase "--sln with file" <| fun () ->
    parseArgs [| "--sln"; "MySolution.sln" |]
    |> Expect.equal "should have Sln" [ Sln "MySolution.sln" ]

  testCase "--dir with directory" <| fun () ->
    parseArgs [| "--dir"; "/home/user/code" |]
    |> Expect.equal "should have Dir" [ Dir "/home/user/code" ]

  testCase "--reference with file" <| fun () ->
    parseArgs [| "--reference"; "Lib.dll" |]
    |> Expect.equal "should have Reference" [ Reference "Lib.dll" ]

  testCase "-r: shorthand" <| fun () ->
    parseArgs [| "-r:Lib.dll" |]
    |> Expect.equal "should have Reference" [ Reference "Lib.dll" ]

  testCase "--load with file" <| fun () ->
    parseArgs [| "--load"; "script.fsx" |]
    |> Expect.equal "should have Load" [ Load "script.fsx" ]

  testCase "--load: shorthand" <| fun () ->
    parseArgs [| "--load:script.fsx" |]
    |> Expect.equal "should have Load" [ Load "script.fsx" ]

  testCase "--use with file" <| fun () ->
    parseArgs [| "--use"; "config.fsx" |]
    |> Expect.equal "should have Use" [ Use "config.fsx" ]

  testCase "--use: shorthand" <| fun () ->
    parseArgs [| "--use:config.fsx" |]
    |> Expect.equal "should have Use" [ Use "config.fsx" ]

  testCase "--lib with multiple dirs" <| fun () ->
    parseArgs [| "--lib"; "dir1"; "dir2"; "dir3" |]
    |> Expect.equal "should have Lib with all dirs" [ Lib [ "dir1"; "dir2"; "dir3" ] ]

  testCase "--lib stops at next flag" <| fun () ->
    parseArgs [| "--lib"; "dir1"; "dir2"; "--bare" |]
    |> Expect.equal "should have Lib then Bare" [ Lib [ "dir1"; "dir2" ]; Bare ]

  testCase "--other captures remaining args" <| fun () ->
    parseArgs [| "--other"; "foo"; "bar"; "--baz" |]
    |> Expect.equal "should have Other" [ Other [ "foo"; "bar"; "--baz" ] ]

  testCase "multiple flags combined" <| fun () ->
    parseArgs [| "--bare"; "--no-watch"; "--proj"; "App.fsproj" |]
    |> Expect.equal "should have all three" [ Bare; No_Watch; Proj "App.fsproj" ]

  testCase "unknown flags are ignored" <| fun () ->
    parseArgs [| "--unknown"; "--bare" |]
    |> Expect.equal "should skip unknown" [ Bare ]

  testCase "-l is alias for --lib" <| fun () ->
    parseArgs [| "-l"; "myDir" |]
    |> Expect.equal "should have Lib" [ Lib [ "myDir" ] ]
]
