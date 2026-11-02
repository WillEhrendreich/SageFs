module SageFs.Tests.MemberMintCommandTests

/// `sagefs member mint`: its arguments, what it prints, and what it says when it cannot mint. The mint itself,
/// against a real daemon, is in CapabilityHttpIntegrationTests.

open System
open System.IO
open System.Net
open System.Net.Sockets
open Expecto
open Expecto.Flip
open SageFs
open SageFs.EdgeCredential
open SageFs.MemberMintCommand

let parse (args: string) : Result<MintArgs, UsageRefusal> =
  parseArgs (args.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)

let reply : McpMintRoute.MintReply =
  { Token = "sfm_TheTokenItself"
    MemberId = "cap:0123456789abcdef"
    Role = "Analysis"
    Route = "the checkout /repo"
    Scope = "src/Foo/"
    ExpiresAt = "2026-11-02T10:00:00.0000000Z"
    Session = "" }

/// A port nothing listens on, reserved the way every test that wants one reserves it.
let freePort () : int = fst (TestInfrastructure.TestPorts.reservePair ())

[<Tests>]
let tests =
  testList "MemberMintCommand" [

    testList "arguments" [
      test "WHY - a role and a checkout are enough, and the rest take the route's own defaults" {
        match parse "mint --role Analysis --dir /repo" with
        | Ok args ->
          args.Request |> Expect.equal "defaults" { Role = "Analysis"; Scope = ""; TtlMinutes = 0; WorkingDirectory = "/repo"; SessionId = "" }
          args.Output |> Expect.equal "shown for a person" MintOutput.Details
        | Error refusal -> failtestf "should parse: %A" refusal
      }

      test "WHY - every flag is read, and --print-env asks for the export line" {
        match parse "mint --role Implementer --session abc --scope src/Foo/ --ttl 45 --print-env SAGEFS_MEMBER_TOKEN" with
        | Ok args ->
          args.Request
          |> Expect.equal "all of it" { Role = "Implementer"; Scope = "src/Foo/"; TtlMinutes = 45; WorkingDirectory = ""; SessionId = "abc" }
          args.Output |> Expect.equal "export" (MintOutput.Export "SAGEFS_MEMBER_TOKEN")
        | Error refusal -> failtestf "should parse: %A" refusal
      }

      test "WHY - each mistake is its own named refusal, and every refusal says what to do" {
        [ "mint --dir /repo", UsageRefusal.MissingRole
          "mint --role Analysis", UsageRefusal.NamesNeitherOrBoth
          "mint --role Analysis --dir /repo --session abc", UsageRefusal.NamesNeitherOrBoth
          "mint --role Analysis --dir /repo --ttl soon", UsageRefusal.NotANumber("--ttl", "soon")
          "mint --role Analysis --dir /repo --print-env 1BAD", UsageRefusal.NotAVariableName "1BAD"
          "mint --role Analysis --dir /repo --colour red", UsageRefusal.UnknownFlag "--colour"
          "mint --role --dir /repo", UsageRefusal.FlagNeedsValue "--role"
          "mint --role Analysis --dir", UsageRefusal.FlagNeedsValue "--dir"
          "revoke --role Analysis", UsageRefusal.NotMemberMint ]
        |> List.iter (fun (args, expected) ->
          parse args |> Expect.equal (sprintf "'%s'" args) (Error expected)
          String.IsNullOrWhiteSpace(UsageRefusal.describe expected) |> Expect.isFalse "and it says something")
      }

      test "WHY - a variable name is letters, digits and underscores, and does not start with a digit" {
        isVariableName "SAGEFS_MEMBER_TOKEN" |> Expect.isTrue "plain"
        isVariableName "_x1" |> Expect.isTrue "underscore first"
        [ ""; "1X"; "A-B"; "A B"; "A;rm"; "A$(x)" ] |> List.iter (fun name -> isVariableName name |> Expect.isFalse (sprintf "'%s' is not one" name))
      }
    ]

    test "WHY - the command line routes 'member' to the verb, with the arguments after it" {
      Program.CliCommand.parse [| "member"; "mint"; "--mcp-port"; "39990"; "--role"; "Analysis" |]
      |> Expect.equal "the verb" (Program.CliCommand.Member [ "mint"; "--mcp-port"; "39990"; "--role"; "Analysis" ])
    }

    testList "what is printed" [
      test "WHY - with --print-env stdout is one export line, and the summary beside it does not hold the token" {
        render (MintOutput.Export "SAGEFS_MEMBER_TOKEN") reply |> Expect.equal "the line" "export SAGEFS_MEMBER_TOKEN=sfm_TheTokenItself"
        (summaryLine reply).Contains reply.Token |> Expect.isFalse "the log line has no token"
        summaryLine reply |> Expect.stringContains "but names who was minted" reply.MemberId
      }

      test "WHY - without --print-env the person reads the member, the grant and the token" {
        let shown = render MintOutput.Details reply
        for part in [ reply.MemberId; reply.Role; reply.Route; reply.Scope; reply.Token ] do
          shown |> Expect.stringContains "it shows" part
      }
    ]

    testList "what the daemon answered" [
      test "WHY - a 200 is the reply, read by its camelCase names" {
        let body = Json.serialize Json.camelCase reply
        parseReply 200 body |> Expect.equal "the reply" (Ok reply)
      }

      test "WHY - a refusal carries the daemon's message and what to do" {
        let body = Json.serialize Json.camelCase (McpMintRoute.refusalBody EdgeRefusal.MemberNotAllowed)
        match parseReply 403 body with
        | Error(MintFailure.Refused(403, message, action)) ->
          message |> Expect.equal "message" (EdgeRefusal.message EdgeRefusal.MemberNotAllowed)
          action |> Expect.equal "action" (EdgeRefusal.suggestedAction EdgeRefusal.MemberNotAllowed)
        | other -> failtestf "expected a refusal, got %A" other
      }

      test "WHY - a body that is neither is said so, with the status" {
        parseReply 502 "<html>bad gateway</html>" |> Expect.equal "unreadable" (Error(MintFailure.UnreadableReply 502))
      }
    ]

    testList "when it cannot mint" [
      testTask "WHY - with no operator.token it says where the file should be and how it comes to exist" {
        let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-mint-%s" (Guid.NewGuid().ToString "N"))
        Directory.CreateDirectory dir |> ignore
        try
          let args = { Request = McpMintRoute.requestFor "Analysis"; Output = MintOutput.Details }
          let! result = mint (freePort ()) dir args
          match result with
          | Error(MintFailure.NoCredential(OperatorStore.ReadRefusal.NoFile path) as failure) ->
            path |> Expect.equal "names the file" (OperatorStore.pathIn dir)
            MintFailure.describe failure |> Expect.stringContains "says to start the daemon" "Start the daemon"
          | other -> failtestf "expected NoCredential, got %A" other
        finally
          Directory.Delete(dir, true)
      }

      testTask "WHY - with a credential and no daemon it names the port and what to do" {
        let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-mint-%s" (Guid.NewGuid().ToString "N"))
        Directory.CreateDirectory dir |> ignore
        try
          File.WriteAllText(OperatorStore.pathIn dir, OperatorCredential.ofEntropy (Array.create OperatorCredential.entropyBytes 9uy))
          let port = freePort ()
          let args = { Request = McpMintRoute.requestFor "Analysis"; Output = MintOutput.Details }
          let! result = mint port dir args
          match result with
          | Error(MintFailure.DaemonUnreachable(reportedPort, _) as failure) ->
            reportedPort |> Expect.equal "the port tried" port
            MintFailure.describe failure |> Expect.stringContains "says how to start one" "Start it with 'sagefs'"
          | other -> failtestf "expected DaemonUnreachable, got %A" other
        finally
          Directory.Delete(dir, true)
      }
    ]
  ]
