module SageFs.FsiRewrite

open System

// Utility function to rewrite F# code patterns that don't work in FSI
// but work in compiled code

let rewriteInlineUseStatements (code: string) : string =
  let lines = code.Split('\n')
  let mutable rewritten = false
  let rewrittenLines =
    lines |> Array.mapi (fun i line ->
      let trimmed = line.TrimStart()
      if trimmed.StartsWith("use ", System.StringComparison.Ordinal) && line.Length > trimmed.Length then
        rewritten <- true
        let indent = line.Length - trimmed.Length
        line.Substring(0, indent) + "let " + trimmed.Substring(4)
      else
        line
    )
  if rewritten then
    String.Join("\n", rewrittenLines)
  else
    code
