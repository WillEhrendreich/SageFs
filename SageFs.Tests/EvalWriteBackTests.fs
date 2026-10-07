/// The write-to-source slice of "A REPL eval changes the running app": whether an eval may be
/// spliced back into the file it came from is ONE pure decision — the eval text, the file path and
/// block start line the call supplied, and the file's current source go in, and out comes either
/// the exact splice target (the declaration the eval redefines, its line range, the replacement
/// text) or a `NotPersisted` reason a caller can act on. This file pins that decision and that it
/// is WIRED: the eval path calls the writer, and the tool result carries the verdict as a field.
module SageFs.Tests.EvalWriteBackTests

open System.IO
open Expecto
open Expecto.Flip
open SageFs.Features.Tweak.EvalWriteBack

/// The file every structural case splices against: one module, one function, at line 3.
let private demo = "module Demo\n\nlet add a b = a + b\n"

let private repoRoot = RepoPaths.repoPathFull [||]

/// The refusal reason — or the test fails: decide's error half is always `NotPersisted`.
let private refused (outcome: Result<'a, WriteBack>) : string =
  match outcome with
  | Ok _ -> failtest "expected the eval not to be persisted"
  | Error (WriteBack.NotPersisted reason) -> reason
  | Error (WriteBack.Written _) -> failtest "expected a not-persisted verdict, got a written one"

/// The splice target — or the test fails.
let private spliced (outcome: Result<Splice, WriteBack>) : Splice =
  match outcome with
  | Ok splice -> splice
  | Error (WriteBack.NotPersisted reason) -> failtestf "expected a splice target, got not persisted: %s" reason
  | Error (WriteBack.Written _) -> failtest "expected a splice target, got a written verdict"

[<Tests>]
let tests =
  testList "Eval write-back" [

    testCase "no file_path: not persisted, and the reason names the argument to pass" <| fun _ ->
      decide "let add a b = a + b + 1" None (Some 3) demo
      |> refused
      |> Expect.stringContains "says what to pass" "file_path"

    testCase "no block_start_line: not persisted, and the reason names the argument to pass" <| fun _ ->
      decide "let add a b = a + b + 1" (Some "Demo.fs") None demo
      |> refused
      |> Expect.stringContains "says what to pass" "block_start_line"

    testCase "a submission that will not parse is refused, with the parser's own reason" <| fun _ ->
      decide "let add a b = (" (Some "Demo.fs") (Some 3) demo
      |> refused
      |> Expect.stringContains "says it does not parse" "does not parse"

    testCase "a file that will not parse cannot be spliced, and the reason names the file" <| fun _ ->
      let reason =
        decide "let add a b = a + b + 1" (Some "Demo.fs") (Some 3) "module Demo\n\nlet add a b = (\n"
        |> refused
      reason |> Expect.stringContains "says the file does not parse" "does not parse"
      reason |> Expect.stringContains "names the file" "Demo.fs"

    testCase "no declaration contains block_start_line, and the reason names the line" <| fun _ ->
      let reason =
        decide "let add a b = a + b + 1" (Some "Demo.fs") (Some 99) demo
        |> refused
      reason |> Expect.stringContains "says what was not found" "no declaration"
      reason |> Expect.stringContains "names the line" "line 99"

    testCase "a name mismatch is refused, naming the declaration in the file and what the eval redefines" <| fun _ ->
      let reason =
        decide "let other () = \"yo\"" (Some "Demo.fs") (Some 3) demo
        |> refused
      reason |> Expect.stringContains "names the declaration in the file" "add"
      reason |> Expect.stringContains "names what the eval redefines" "other"

    testCase "a multi-statement submission is refused: one declaration at a time" <| fun _ ->
      decide "let a = 1\nlet b = 2" (Some "Demo.fs") (Some 3) demo
      |> refused
      |> Expect.stringContains "says one at a time" "one declaration at a time"

    testCase "a bare expression redefines nothing, and the reason says so" <| fun _ ->
      decide "printfn \"hello\"" (Some "Demo.fs") (Some 3) demo
      |> refused
      |> Expect.stringContains "says it redefines nothing" "redefines no declaration"

    testCase "happy path: the exact splice range, the matched name, and the replacement" <| fun _ ->
      let splice = spliced (decide "let add a b = a + b + 1" (Some "Demo.fs") (Some 3) demo)
      splice.Declaration.Name |> Expect.equal "splices over the declaration the eval redefined" "add"
      splice.Declaration.StartLine |> Expect.equal "splices from the declaration's first line" 3
      splice.Declaration.EndLine |> Expect.equal "to its last line" 3
      splice.Replacement |> Expect.equal "the eval text, verbatim" "let add a b = a + b + 1"
      applySplice demo splice
      |> Expect.equal "only that declaration's lines changed" (Ok "module Demo\n\nlet add a b = a + b + 1\n")

    testCase "a multi-line declaration splices across its whole range" <| fun _ ->
      let source = "module Demo\n\nlet scale n =\n  n * 2\n"
      let eval = "let scale n =\n  n * 3"
      let splice = spliced (decide eval (Some "Demo.fs") (Some 3) source)
      splice.Declaration.StartLine |> Expect.equal "splices from line 3" 3
      splice.Declaration.EndLine |> Expect.equal "through line 4" 4
      applySplice source splice
      |> Expect.equal "both declaration lines replaced, the rest untouched" (Ok "module Demo\n\nlet scale n =\n  n * 3\n")

    testCase "the interactive terminator is not part of what is spliced into the file" <| fun _ ->
      let splice = spliced (decide "let add a b = a + b + 1;;" (Some "Demo.fs") (Some 3) demo)
      splice.Replacement |> Expect.equal "no ;; in the file" "let add a b = a + b + 1"

    testCase "the verdict has the exact words a caller reads for both outcomes" <| fun _ ->
      WriteBack.describe (WriteBack.NotPersisted "no file_path")
      |> Expect.equal "the refusal line" "not persisted: no file_path"
      WriteBack.describe (WriteBack.Written("Demo.fs", 3))
      |> Expect.equal "the success line" "written back to Demo.fs:3"

    testCase "the decision is wired: the eval path calls the writer and the tool result carries the verdict" <| fun _ ->
      let read relative = File.ReadAllText(Path.Combine(repoRoot, relative))
      read "SageFs/Mcp.fs"
      |> Expect.stringContains "evalFSharpCodeWithOutcome calls the writer after a successful eval" "EvalWriteBackWire.persist"
      read "SageFs/McpTools.fs"
      |> Expect.stringContains "send_fsharp_code surfaces the verdict as a structured field" "withWriteBack"
      read "SageFs/EvalWriteBackWire.fs"
      |> Expect.stringContains "the writer journals before it renames, through the nudge door's own commit" "commit production"
      read "SageFs.Core/SageFs.Core.fsproj"
      |> Expect.stringContains "the pure module is compiled after ReloadPlanning" "Features\\Tweak\\EvalWriteBack.fs"
  ]
