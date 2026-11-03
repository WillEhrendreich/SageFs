/// Every `contentText` the VS Code extension hands VS Code that is not a fixed string goes through
/// `InlineDecorationsPure.asOneLine`. VS Code keeps only the first line of a decoration's `contentText`
/// (`contentText.match(/^.*$/m)[0]`, VS Code 1.142), so one line break in the text cuts it there: on Windows, where
/// FSI ends its lines with CR LF, a two-binding eval showed only its first binding (issue #132). The renderers are
/// proven in `InlineDecorationsPure.golden-tests.mjs`; this keeps a NEW ghost text from skipping the guard.
module SageFs.Tests.VscodeGhostTextTests

open System.IO
open Expecto
open Expecto.Flip

let private assignment = "\"contentText\" ==>"

[<Tests>]
let tests =
  testList "VS Code ghost text" [
    testCase "WHY - every contentText the extension sets is a fixed string, a fixed label, or passes through asOneLine, because a line break in it cuts it short in VS Code" <| fun _ ->
      let root = RepoPaths.requireRepoRoot ()
      let sourceDir = Path.Combine(root, "sagefs-vscode", "src")
      let offenders =
        [ for file in Directory.EnumerateFiles(sourceDir, "*.fs", SearchOption.TopDirectoryOnly) do
            File.ReadAllLines file
            |> Array.indexed
            |> Array.choose (fun (index, line) ->
              match line.IndexOf assignment with
              | -1 -> None
              | at when line.TrimStart().StartsWith "//" -> ignore at; None
              | at ->
                let rightHandSide = line.Substring(at + assignment.Length).TrimStart()
                let fixedText = rightHandSide.StartsWith "\""
                let fixedLabel = rightHandSide.StartsWith "InlineDecorationsPure.evalInProgressLabel"
                let guarded = rightHandSide.Contains "asOneLine"
                match fixedText || fixedLabel || guarded with
                | true -> None
                | false -> Some (sprintf "%s:%d  %s" (Path.GetFileName file) (index + 1) (line.Trim())))
            |> List.ofArray ]
        |> List.concat
      offenders |> Expect.isEmpty (sprintf "pass the text through InlineDecorationsPure.asOneLine: %s" (String.concat "; " offenders))
  ]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant tests |> ignore
