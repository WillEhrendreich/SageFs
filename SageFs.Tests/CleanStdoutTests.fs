module SageFs.Tests.CleanStdoutTests

open Expecto
open Expecto.Flip
open SageFs.AppState

let cleanStdoutTests =
  testList "cleanStdout" [
    testCase "strips ANSI escape sequences" <| fun _ ->
      let input = "\x1b[32mGreen text\x1b[0m normal"
      let result = cleanStdout input
      result |> Expect.equal "should strip ANSI colors" "Green text normal"

    testCase "strips timestamp prefix" <| fun _ ->
      let input = "[15:30:02 INF] Test passed"
      let result = cleanStdout input
      result |> Expect.equal "should strip [HH:mm:ss LVL] prefix" "Test passed"

    testCase "strips Expecto suffix" <| fun _ ->
      let input = "Test passed  <Expecto>"
      let result = cleanStdout input
      result |> Expect.equal "should strip <Expecto> suffix" "Test passed"

    testCase "removes Expecto Running lines" <| fun _ ->
      let input = "Expecto Running...\nreal output"
      let result = cleanStdout input
      result |> Expect.equal "should remove Expecto Running lines" "real output"

    testCase "removes progress bar lines" <| fun _ ->
      let input = "3/10 |===      |\nreal output"
      let result = cleanStdout input
      result |> Expect.equal "should remove progress bar lines" "real output"

    testCase "removes blank lines" <| fun _ ->
      let input = "line1\n\n\n  \nline2"
      let result = cleanStdout input
      result |> Expect.equal "should remove blank/whitespace-only lines" "line1\nline2"

    testCase "reformats Expecto summary" <| fun _ ->
      let input = "EXPECTO! 5 tests run in 00:00:00.123 for MyTests \u2013 3 passed, 1 ignored, 1 failed, 0 errored. Failure!"
      let result = cleanStdout input
      result |> Expect.stringContains "should reformat summary" "MyTests: 5 tests"
      result |> Expect.stringContains "should include passed count" "3 passed"

    testCase "handles combined ANSI + timestamp + suffix" <| fun _ ->
      let input = "\x1b[32m[15:30:02 INF] Test passed  <Expecto>\x1b[0m"
      let result = cleanStdout input
      result |> Expect.equal "should handle all transformations together" "Test passed"

    testCase "converts cursor-reset to newline" <| fun _ ->
      let input = "line1\x1b[10Dline2"
      let result = cleanStdout input
      result |> Expect.equal "cursor-reset should become newline" "line1\nline2"

    testCase "handles empty input" <| fun _ ->
      let result = cleanStdout ""
      result |> Expect.equal "empty input should return empty" ""

    testCase "handles whitespace-only input" <| fun _ ->
      let result = cleanStdout "   \n   \n   "
      result |> Expect.equal "whitespace-only should return empty" ""

    testCase "preserves normal output unchanged" <| fun _ ->
      let input = "val x: int = 42"
      let result = cleanStdout input
      result |> Expect.equal "normal output should pass through" "val x: int = 42"

    testCase "cleanStdout processes 500 lines in under 1000µs" <| fun _ ->
      let bigInput =
        [| for i in 1 .. 500 ->
             sprintf "  [15:%02d:%02d INF] Evaluation line %d with some content  <Expecto>" (i/60) (i%60) i |]
        |> String.concat "\n"
      // Warmup (JIT + regex compilation)
      cleanStdout bigInput |> ignore
      let sw = System.Diagnostics.Stopwatch.StartNew()
      let iters = 100
      for _ in 1 .. iters do
        cleanStdout bigInput |> ignore
      sw.Stop()
      let usPerOp = float sw.Elapsed.TotalMicroseconds / float iters
      printfn "cleanStdout: %.1f µs/op (%d iterations)" usPerOp iters
      (usPerOp, 1000.0) |> Expect.isLessThan "cleanStdout should be under 1000µs for 500 lines"
  ]
