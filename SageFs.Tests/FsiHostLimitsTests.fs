module SageFs.Tests.FsiHostLimitsTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs.FsiHost.FsiProtocol
open SageFs.FsiHost.FsiProtocol.HostLimits

let private withTempFile (contents: string) (check: string -> unit) =
  let path = Path.GetTempFileName()
  try
    File.WriteAllText(path, contents)
    check path
  finally
    File.Delete path

let private valid = [| "fsi"; "--noninteractive"; "--nologo"; "-r:/a/b.dll" |]

[<Tests>]
let queueTests =
  testList "FsiHost eval queue bound" [

    testCase "WHY — the queue has a named, positive capacity" <| fun _ ->
      Expect.isGreaterThan "capacity is positive" (EvalQueueCapacity, 0)

    testProperty "WHY — below capacity a request is admitted, at or above it is refused with the numbers"
    <| fun (capacity: byte) (pending: byte) ->
      let capacity = int capacity + 1
      let pending = int pending
      match admit capacity pending with
      | Admitted -> Expect.isLessThan "admitted only below capacity" (pending, capacity)
      | Refused refusal ->
        Expect.isGreaterThanOrEqual "refused only at or above capacity" (pending, capacity)
        Expect.equal "refusal names the capacity" capacity refusal.Capacity
        Expect.equal "refusal names the pending count" pending refusal.Pending

    testCase "WHY — a refused eval is answered as a failed eval that says why, so the caller never waits" <| fun _ ->
      let refusal = { Capacity = 64; Pending = 64 }
      match refusalResponse AskedEval 41L refusal with
      | EvalResult(41L, EvalFailed message, []) ->
        Expect.stringContains "says the queue is full" "full" message
        Expect.stringContains "says the capacity" "64" message
      | other -> failtestf "expected a failed EvalResult for id 41, got %A" other

    testCase "WHY — a refused non-eval request is answered with a refusal carrying its id" <| fun _ ->
      let refusal = { Capacity = 64; Pending = 70 }
      match refusalResponse AskedOther 9L refusal with
      | AgentRefused(9L, reason) -> Expect.stringContains "says the queue is full" "full" reason
      | other -> failtestf "expected AgentRefused for id 9, got %A" other

    testCase "WHY — a refusal survives the wire, because it is a protocol message" <| fun _ ->
      let response = refusalResponse AskedEval 5L { Capacity = 8; Pending = 8 }
      match decodeResponse (encodeResponse response) with
      | Result.Ok decoded -> Expect.equal "round trip" response decoded
      | Result.Error reason -> failtestf "did not round trip: %s" (describeError reason)
  ]

[<Tests>]
let argsFileTests =
  testList "FsiHost args file validation" [

    testCase "WHY — a well formed args file yields its arguments, blank lines dropped" <| fun _ ->
      withTempFile (String.Join("\n", [| "fsi"; ""; "--noninteractive"; ""; "-r:/a/b.dll" |])) (fun path ->
        readArgsFile path
        |> Expect.equal "arguments" (Result.Ok [ "fsi"; "--noninteractive"; "-r:/a/b.dll" ]))

    testCase "WHY — a missing file is reported by name" <| fun _ ->
      let path = Path.Combine(Path.GetTempPath(), "sagefs-no-such-" + Guid.NewGuid().ToString "N" + ".args")
      readArgsFile path |> Expect.equal "missing" (Result.Error(ArgsFileMissing path))

    testCase "WHY — a path that is a directory is unreadable, and the error names it" <| fun _ ->
      let path = Path.GetTempPath()
      match readArgsFile path with
      | Result.Error(ArgsFileUnreadable(named, _)) -> Expect.equal "names the path" path named
      | other -> failtestf "expected Unreadable, got %A" other

    testCase "WHY — an empty file is an error, not a session with no arguments" <| fun _ ->
      withTempFile "" (fun path -> readArgsFile path |> Expect.equal "empty" (Result.Error(ArgsFileEmpty path)))

    testCase "WHY — a file of only blank lines is empty too" <| fun _ ->
      withTempFile "\n\n\n" (fun path -> readArgsFile path |> Expect.equal "empty" (Result.Error(ArgsFileEmpty path)))

    testProperty "WHY — a control character on a line is reported with that exact line number"
    <| fun (position: byte) ->
      let at = int position % (valid.Length + 1)
      let lines = Array.insertAt at "--bad\u0000flag" valid
      match validateArgs "x.args" lines with
      | Result.Error(ArgsFileBadLine(path, line, text, ControlCharacter)) ->
        Expect.equal "names the file" "x.args" path
        Expect.equal "one-based line of the bad text" (at + 1) line
        Expect.stringContains "quotes the text" "bad" text
      | other -> failtestf "expected a bad-line error, got %A" other

    testCase "WHY — the line number counts blank lines, so it matches what an editor shows" <| fun _ ->
      let lines = [| "fsi"; ""; ""; "--ok"; "oops-not-an-option" |]
      match validateArgs "y.args" lines with
      | Result.Error(ArgsFileBadLine("y.args", 5, "oops-not-an-option", NotAnOption)) -> ()
      | other -> failtestf "expected line 5 NotAnOption, got %A" other

    testCase "WHY — an option in the program-name slot is refused, since FSI would silently swallow it" <| fun _ ->
      match validateArgs "z.args" [| "--noninteractive"; "--nologo" |] with
      | Result.Error(ArgsFileBadLine("z.args", 1, "--noninteractive", OptionInProgramNameSlot)) -> ()
      | other -> failtestf "expected line 1 OptionInProgramNameSlot, got %A" other

    testCase "WHY — a line of only spaces is refused rather than passed to FSI as an argument" <| fun _ ->
      match validateArgs "w.args" [| "fsi"; "   " |] with
      | Result.Error(ArgsFileBadLine("w.args", 2, _, BlankButNotEmpty)) -> ()
      | other -> failtestf "expected line 2 BlankButNotEmpty, got %A" other

    testCase "WHY — the description names the file and the line" <| fun _ ->
      let text =
        describeArgsFileError (ArgsFileBadLine("/tmp/q.args", 3, "oops", NotAnOption))
      Expect.stringContains "file" "/tmp/q.args" text
      Expect.stringContains "line" "line 3" text
      Expect.stringContains "text" "oops" text

    testCase "WHY — the description of every other error names the file" <| fun _ ->
      for error in [ ArgsFileMissing "/p"; ArgsFileEmpty "/p"; ArgsFileUnreadable("/p", "denied") ] do
        Expect.stringContains "file" "/p" (describeArgsFileError error)
  ]
