module SageFs.Tests.DiagnosticsSocketTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Features

/// The runtime puts its diagnostics socket at `<TMPDIR>/dotnet-diagnostic-<pid>-<key>-socket`, and a Unix socket path holds 107
/// characters. A longer path is cut short by the runtime, so the socket is not named the way `dotnet-trace` looks for it and the
/// profile tool cannot attach. Found 2026-11-10: the release gate's tier directory
/// (`~/.local/share/sagefs-gate/checkout-<id>.tiers/integration-host-4of5.tmp`, 85 characters) put every host's socket past the
/// limit, while a 59 character directory worked. `dotnet-trace` says so itself: "The total length of the diagnostic socket path may
/// exceed 108 characters. Try setting the TMPDIR environment variable to a shorter path".

let private config = { FsCheckConfig.defaultConfig with maxTest = 200 }

let private dirOfLength (n: int) : string = "/" + String('d', max 0 (n - 1))

[<Tests>]
let tests =
  testList "Diagnostics socket path" [

    testList "what fits" [
      testCase "WHY - the longest directory is the one whose longest socket name ends at the limit" <| fun _ ->
        DiagnosticsSocket.fits (dirOfLength DiagnosticsSocket.longestTemp) |> Expect.isTrue "at the limit"
        DiagnosticsSocket.fits (dirOfLength (DiagnosticsSocket.longestTemp + 1)) |> Expect.isFalse "one more is cut short"

      testCase "WHY - the longest name the runtime gives is a seven digit pid and a ten digit key" <| fun _ ->
        DiagnosticsSocket.longestName |> Expect.equal "name" "dotnet-diagnostic-4194304-4294967295-socket"

      testCase "WHY - the directories seen in practice: /tmp and a 59 character tier directory fit, the gate's 85 character one does not" <| fun _ ->
        DiagnosticsSocket.fits "/tmp" |> Expect.isTrue "/tmp"
        DiagnosticsSocket.fits "/tmp/" |> Expect.isTrue "a trailing slash is not counted twice"
        DiagnosticsSocket.fits (dirOfLength 59) |> Expect.isTrue "59"
        DiagnosticsSocket.fits "/home/will/.local/share/sagefs-gate/checkout-8431b56a.tiers/integration-host-4of5.tmp" |> Expect.isFalse "the gate's"
    ]

    testList "the plan for a host's temp directory" [
      testCase "WHY - a directory that fits is kept" <| fun _ ->
        DiagnosticsSocket.plan "/tmp" [ "/tmp" ] |> Expect.equal "kept" DiagnosticsSocket.TempPlan.KeepTemp

      testCase "WHY - a directory that is too long gets a short link to it, in the first base that has room" <| fun _ ->
        match DiagnosticsSocket.plan (dirOfLength 90) [ dirOfLength 80; "/tmp" ] with
        | DiagnosticsSocket.TempPlan.UseShortLink(link, target) ->
          target |> Expect.equal "to the same directory" (dirOfLength 90)
          link |> Expect.stringStarts "inside the base that fits" "/tmp/"
          DiagnosticsSocket.fits link |> Expect.isTrue "and the link fits"
        | other -> failtestf "got %A" other

      testCase "WHY - the same directory always gets the same link, and another directory another" <| fun _ ->
        let link (dir: string) =
          match DiagnosticsSocket.plan dir [ "/tmp" ] with
          | DiagnosticsSocket.TempPlan.UseShortLink(l, _) -> l
          | other -> failtestf "got %A" other
        link (dirOfLength 90) |> Expect.equal "stable" (link (dirOfLength 90))
        (link (dirOfLength 90) <> link (dirOfLength 91)) |> Expect.isTrue "distinct"

      testCase "WHY - when no base has room the plan says so and names the directory and the most it may be" <| fun _ ->
        match DiagnosticsSocket.plan (dirOfLength 90) [ dirOfLength 80 ] with
        | DiagnosticsSocket.TempPlan.TempTooLong(tmp, longest) ->
          tmp |> Expect.equal "the directory" (dirOfLength 90)
          longest |> Expect.equal "the limit" DiagnosticsSocket.longestTemp
        | other -> failtestf "got %A" other

      testPropertyWithConfig config "WHY - whatever the directory, the plan is a directory that fits or a refusal, never a path that is cut short" <| fun (length: byte) ->
        let tmp = dirOfLength (int length + 1)
        match DiagnosticsSocket.plan tmp [ "/tmp" ] with
        | DiagnosticsSocket.TempPlan.KeepTemp -> DiagnosticsSocket.fits tmp |> Expect.isTrue "kept only when it fits"
        | DiagnosticsSocket.TempPlan.UseShortLink(link, _) ->
          DiagnosticsSocket.fits tmp |> Expect.isFalse "linked only when it does not"
          DiagnosticsSocket.fits link |> Expect.isTrue "to one that does"
        | DiagnosticsSocket.TempPlan.TempTooLong _ -> failtestf "/tmp has room, so %d characters can be linked" tmp.Length
    ]

    testCase "WHY - a link is made to the long directory, resolves to it, and is made again as the same link" <| fun _ ->
      match OperatingSystem.IsWindows() with
      | true -> ()
      | false ->
        // A long directory wherever the test's own temp is, and a short place for the link: under /tmp, which is short.
        let longParent = Path.Combine(Path.GetTempPath(), "sagefs-socket-test", Guid.NewGuid().ToString "N")
        let root = Path.Combine(longParent, String('x', 80))
        Directory.CreateDirectory root |> ignore
        let linkBase = "/tmp/sfk" + Guid.NewGuid().ToString("N").Substring(0, 8)
        Directory.CreateDirectory linkBase |> ignore
        try
          let bases = [ linkBase ]
          match DiagnosticsSocket.environmentFor bases root with
          | [ ("TMPDIR", link) ] ->
            DiagnosticsSocket.fits link |> Expect.isTrue "the link fits"
            Directory.Exists link |> Expect.isTrue "the link is a directory"
            (Directory.ResolveLinkTarget(link, false).FullName.TrimEnd('/')) |> Expect.equal "to the long one" (root.TrimEnd('/'))
            DiagnosticsSocket.environmentFor bases root |> Expect.equal "the same again" [ ("TMPDIR", link) ]
          | other -> failtestf "got %A" other
        finally
          Directory.Delete(longParent, true)
          Directory.Delete(linkBase, true)

    testCase "WHY - a directory that fits needs no change to the host's environment" <| fun _ ->
      DiagnosticsSocket.environmentFor [ "/tmp" ] "/tmp" |> Expect.isEmpty "nothing to set"

    testList "reading a host's directory" [
      testCase "WHY - TMPDIR is read out of the NUL separated environment, and /tmp is the default" <| fun _ ->
        DiagnosticsSocket.tempOfEnvironment "PATH=/bin\000TMPDIR=/work/tmp\000HOME=/h\000" |> Expect.equal "set" "/work/tmp"
        DiagnosticsSocket.tempOfEnvironment "PATH=/bin\000HOME=/h\000" |> Expect.equal "unset" "/tmp"
        DiagnosticsSocket.tempOfEnvironment "TMPDIR=\000" |> Expect.equal "empty" "/tmp"
    ]

    testList "what the refusal says" [
      testCase "WHY - a directory too long for the socket is refused by name, with the limit and what to do" <| fun _ ->
        let text = ProfileFailure.describe (ProfileFailure.SocketPathTooLong("/very/long/dir", DiagnosticsSocket.longestTemp))
        text |> Expect.stringContains "names the directory" "/very/long/dir"
        text |> Expect.stringContains "names the setting" "TMPDIR"
        text |> Expect.stringContains "says the code did not run" "did not run"

      testCase "WHY - what the recorder said puts the exception's message first and keeps only a short tail of the stack" <| fun _ ->
        let lines =
          [ "[ERROR] ServerNotAvailableException: Unable to connect to Process 1. The total length of the diagnostic socket path may exceed 108 characters."
            "   at Microsoft.Diagnostics.NETCore.Client.PidIpcEndpoint.GetDefaultAddress(Int32 pid)"
            "   at Microsoft.Diagnostics.NETCore.Client.PidIpcEndpoint.ConnectAsync(CancellationToken token)"
            "   at Microsoft.Diagnostics.NETCore.Client.IpcClient.SendMessageGetContinuationAsync(IpcEndpoint endpoint)"
            "   at Microsoft.Diagnostics.NETCore.Client.EventPipeSession.StartAsync(IpcEndpoint endpoint)" ]
        let said = DotnetTrace.saidBy lines
        said |> Expect.stringStarts "the message leads" "[ERROR] ServerNotAvailableException: Unable to connect"
        said |> Expect.stringContains "all of it" "may exceed 108 characters."
        said |> Expect.stringContains "a little of where" "GetDefaultAddress"
        (said.Contains "StartAsync") |> Expect.isFalse "and not the whole stack"
        (said.Length <= 403) |> Expect.isTrue "bounded"

      testPropertyWithConfig config "WHY - the message of what the recorder said survives however long the stack is" <| fun (depth: byte) ->
        let stack = [ for i in 1 .. int depth -> sprintf "   at Frame%d(Int32 x)" i ]
        let said = DotnetTrace.saidBy ("[ERROR] the message" :: stack)
        said |> Expect.stringStarts "message first" "[ERROR] the message"
        (said.Length <= 403) |> Expect.isTrue "bounded"
    ]
  ]
