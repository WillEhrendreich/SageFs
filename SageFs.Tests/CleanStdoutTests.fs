module SageFs.Tests.CleanStdoutTests

open Expecto
open SageFs.AppState

let cleanStdoutTests =
  testList "cleanStdout" [
    testCase "strips ANSI escape sequences" <| fun _ ->
      let input = "\x1b[32mGreen text\x1b[0m normal"
      let result = cleanStdout input
      Expect.equal result "Green text normal" "should strip ANSI colors"

    testCase "strips timestamp prefix" <| fun _ ->
      let input = "[15:30:02 INF] Test passed"
      let result = cleanStdout input
      Expect.equal result "Test passed" "should strip [HH:mm:ss LVL] prefix"

    testCase "strips Expecto suffix" <| fun _ ->
      let input = "Test passed  <Expecto>"
      let result = cleanStdout input
      Expect.equal result "Test passed" "should strip <Expecto> suffix"

    testCase "removes Expecto Running lines" <| fun _ ->
      let input = "Expecto Running...\nreal output"
      let result = cleanStdout input
      Expect.equal result "real output" "should remove Expecto Running lines"

    testCase "removes progress bar lines" <| fun _ ->
      let input = "3/10 |===      |\nreal output"
      let result = cleanStdout input
      Expect.equal result "real output" "should remove progress bar lines"

    testCase "removes blank lines" <| fun _ ->
      let input = "line1\n\n\n  \nline2"
      let result = cleanStdout input
      Expect.equal result "line1\nline2" "should remove blank/whitespace-only lines"

    testCase "reformats Expecto summary" <| fun _ ->
      let input = "EXPECTO! 5 tests run in 00:00:00.123 for MyTests \u2013 3 passed, 1 ignored, 1 failed, 0 errored. Failure!"
      let result = cleanStdout input
      Expect.stringContains result "MyTests: 5 tests" "should reformat summary"
      Expect.stringContains result "3 passed" "should include passed count"

    testCase "handles combined ANSI + timestamp + suffix" <| fun _ ->
      let input = "\x1b[32m[15:30:02 INF] Test passed  <Expecto>\x1b[0m"
      let result = cleanStdout input
      Expect.equal result "Test passed" "should handle all transformations together"

    testCase "converts cursor-reset to newline" <| fun _ ->
      let input = "line1\x1b[10Dline2"
      let result = cleanStdout input
      Expect.equal result "line1\nline2" "cursor-reset should become newline"

    testCase "handles empty input" <| fun _ ->
      let result = cleanStdout ""
      Expect.equal result "" "empty input should return empty"

    testCase "handles whitespace-only input" <| fun _ ->
      let result = cleanStdout "   \n   \n   "
      Expect.equal result "" "whitespace-only should return empty"

    testCase "preserves normal output unchanged" <| fun _ ->
      let input = "val x: int = 42"
      let result = cleanStdout input
      Expect.equal result "val x: int = 42" "normal output should pass through"

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
      Expect.isLessThan usPerOp 1000.0 "cleanStdout should be under 1000µs for 500 lines"
  ]
