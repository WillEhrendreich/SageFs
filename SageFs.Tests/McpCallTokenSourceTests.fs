/// Where `sagefs call` gets the member token. `--token <token>` puts the token in the process list and the shell history, so
/// the token now also comes from a file (`--token-file <path>`) or standard input (`--token-file -`), `--token` warns, and
/// no sentence the command prints holds the token (roast 2026-11-08, UI/UX).
module SageFs.Tests.McpCallTokenSourceTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpCall

let private parsed (words: string) : Result<Invocation, UsageRefusal> =
  CallArgs.parse (words.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)

let private tokenOf (result: Result<Invocation, UsageRefusal>) : Result<TokenChoice, UsageRefusal> =
  result
  |> Result.bind (fun invocation ->
    match invocation with
    | Invocation.Run args -> Ok args.Token
    | Invocation.Help -> Error UsageRefusal.NothingToDo)

/// Reads that say what they were asked, so a test sees which one the choice used.
let private reads (files: (string * TokenFileRead) list) (stdin: string) : TokenReads =
  { ReadFile = fun path -> files |> List.tryFind (fun (name, _) -> name = path) |> Option.map snd |> Option.defaultValue (TokenFileRead.Unreadable "no such file")
    ReadStdin = fun () -> stdin }

let private noEnv (_: string) : string = null

[<Tests>]
let tests =
  testList "sagefs call: where the token comes from" [

    testList "the command line" [
      testCase "WHY - --token-file names a file, and a lone dash names standard input" <| fun _ ->
        tokenOf (parsed "--list --token-file /run/secrets/t") |> Expect.equal "a file" (Ok(TokenChoice.FromFile "/run/secrets/t"))
        tokenOf (parsed "--list --token-file=/run/secrets/t") |> Expect.equal "the = form" (Ok(TokenChoice.FromFile "/run/secrets/t"))
        tokenOf (parsed "--list --token-file -") |> Expect.equal "standard input" (Ok TokenChoice.FromStdin)

      testCase "WHY - a token file with no path is refused by name, and two sources for the token are refused too" <| fun _ ->
        tokenOf (parsed "--list --token-file") |> Expect.equal "needs a value" (Error(UsageRefusal.OptionNeedsValue "--token-file"))
        tokenOf (parsed "--list --token sfm_a --token-file /t") |> Expect.equal "not both" (Error UsageRefusal.TwoTokenSources)
        tokenOf (parsed "--list --token-file /t --token sfm_a") |> Expect.equal "in either order" (Error UsageRefusal.TwoTokenSources)

      testCase "WHY - the option is the command's own, so no tool may have a parameter by that name" <| fun _ ->
        CommandOption.all |> List.map CommandOption.name |> Expect.contains "token-file is one of them" "token-file"
    ]

    testList "reading it" [
      testCase "WHY - a token file's text is trimmed (an editor adds a newline) and goes in the member header, identified by its path" <| fun _ ->
        match Credentials.resolve (reads [ "/t", TokenFileRead.Text "sfm_file\n" ] "") (TokenChoice.FromFile "/t") noEnv with
        | Ok credentials ->
          credentials.Headers |> Expect.equal "the trimmed token" [ Capability.CapabilityTransport.headerName, "sfm_file" ]
          credentials.Identity |> Expect.equal "from that file" (Identity.Member(TokenSource.File "/t"))
        | Error refusal -> failtestf "refused: %A" refusal

      testCase "WHY - standard input is read once for --token-file -" <| fun _ ->
        match Credentials.resolve (reads [] " sfm_piped\r\n") TokenChoice.FromStdin noEnv with
        | Ok credentials ->
          credentials.Headers |> Expect.equal "the trimmed token" [ Capability.CapabilityTransport.headerName, "sfm_piped" ]
          credentials.Identity |> Expect.equal "from standard input" (Identity.Member TokenSource.Stdin)
        | Error refusal -> failtestf "refused: %A" refusal

      testCase "WHY - an empty file, empty input and an unreadable file are each refused by name, never read as anonymous" <| fun _ ->
        Credentials.resolve (reads [ "/t", TokenFileRead.Text "  \n" ] "") (TokenChoice.FromFile "/t") noEnv
        |> Expect.equal "blank file" (Error(UsageRefusal.BlankTokenFile "/t"))
        Credentials.resolve (reads [] "") TokenChoice.FromStdin noEnv
        |> Expect.equal "blank input" (Error(UsageRefusal.BlankTokenFile "standard input"))
        Credentials.resolve (reads [ "/t", TokenFileRead.Unreadable "denied" ] "") (TokenChoice.FromFile "/t") noEnv
        |> Expect.equal "unreadable" (Error(UsageRefusal.TokenFileUnreadable("/t", "denied")))

      testCase "WHY - a token from a file wins over the environment, like --token" <| fun _ ->
        let env name = match name with "SAGEFS_MEMBER_TOKEN" -> "sfm_env" | _ -> null
        match Credentials.resolve (reads [ "/t", TokenFileRead.Text "sfm_file" ] "") (TokenChoice.FromFile "/t") env with
        | Ok credentials -> credentials.Headers |> Expect.equal "the file's token" [ Capability.CapabilityTransport.headerName, "sfm_file" ]
        | Error refusal -> failtestf "refused: %A" refusal
    ]

    testList "what is said about it" [
      testCase "WHY - only --token warns, and the warning names the two better ways and never holds the token" <| fun _ ->
        let token = "sfm_very_secret_value"
        let credentialsOf choice =
          match Credentials.resolve (reads [ "/t", TokenFileRead.Text token ] token) choice (fun name -> match name with "SAGEFS_MEMBER_TOKEN" -> token | _ -> null) with
          | Ok credentials -> credentials
          | Error refusal -> failtestf "refused: %A" refusal
        match Credentials.warning (credentialsOf (TokenChoice.Given token)) with
        | Some line ->
          line |> Expect.stringContains "names the file" "--token-file"
          line |> Expect.stringContains "names the environment variable" "SAGEFS_MEMBER_TOKEN"
          line.Contains token |> Expect.isFalse "and never the token"
        | None -> failtest "a token on the command line should warn"
        for choice in [ TokenChoice.FromEnvironment; TokenChoice.FromFile "/t"; TokenChoice.FromStdin ] do
          Credentials.warning (credentialsOf choice) |> Expect.isNone (sprintf "%A needs no warning" choice)

      testCase "WHY - no description of who is calling holds a token read from a file or standard input" <| fun _ ->
        let token = "sfm_very_secret_value"
        for choice in [ TokenChoice.FromFile "/t"; TokenChoice.FromStdin ] do
          match Credentials.resolve (reads [ "/t", TokenFileRead.Text token ] token) choice noEnv with
          | Ok credentials -> (Credentials.describe credentials).Contains token |> Expect.isFalse (sprintf "%A: the description" choice)
          | Error refusal -> failtestf "refused: %A" refusal

      testCase "WHY - every new refusal says something, and none can hold a token" <| fun _ ->
        [ UsageRefusal.BlankTokenFile "/t"; UsageRefusal.TokenFileUnreadable("/t", "denied"); UsageRefusal.TwoTokenSources ]
        |> List.iter (fun refusal -> UsageRefusal.describe refusal |> Expect.isNotEmpty (sprintf "%A" refusal))
        usage |> Expect.stringContains "the usage lists the option" "--token-file <path>"
        usage |> Expect.stringContains "and calls --token deprecated" "Deprecated"
    ]

    testList "the command" [
      testTask "WHY - a token typed with --token warns on stderr before anything else, and --token-file does not" {
        let port = fst (TestInfrastructure.TestPorts.reservePair ())
        let! (withFlag: McpCallCommandTests.Run) = McpCallCommandTests.execute DaemonPresence.NotRunning port [] "--list --token sfm_on_the_command_line"
        withFlag.Err |> Expect.stringContains "warns" "--token puts the member token on the command line"
        withFlag.Err.Contains "sfm_on_the_command_line" |> Expect.isFalse "and never prints the token"
        let path = Path.Combine(Path.GetTempPath(), sprintf "sagefs-token-%s.txt" (Guid.NewGuid().ToString "N"))
        File.WriteAllText(path, "sfm_in_a_file\n")
        try
          let! (withFile: McpCallCommandTests.Run) = McpCallCommandTests.execute DaemonPresence.NotRunning port [] (sprintf "--list --token-file %s" path)
          withFile.Err.Contains "puts the member token on the command line" |> Expect.isFalse "no warning for a file"
          withFile.Err.Contains "sfm_in_a_file" |> Expect.isFalse "and the token is never printed"
        finally
          File.Delete path
        let! (missing: McpCallCommandTests.Run) = McpCallCommandTests.execute DaemonPresence.NotRunning port [] "--list --token-file /nonexistent/sagefs-token"
        missing.Exit |> Expect.equal "a usage error" 2
        missing.Err |> Expect.stringContains "names the file" "/nonexistent/sagefs-token"
      }
    ]
  ]
