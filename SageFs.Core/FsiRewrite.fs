module SageFs.FsiRewrite

open System

// Utility function to rewrite F# code patterns that don't work in FSI
// but work in compiled code

let rewriteInlineUseStatements (code: string) : string =
  // Simple approach: Replace "use" with "let" when it appears indented
  // FSI can't handle "use" inside expression contexts, but "let" works fine
  // This is safe because in most cases, the disposal happens at the end anyway
  
  let lines = code.Split('\n')
  let mutable rewritten = false
  
  let rewrittenLines = 
    lines |> Array.mapi (fun i line ->
      let trimmed = line.TrimStart()
      // If line starts with "use " and has indentation (not at column 0)
      if trimmed.StartsWith("use ") && line.Length > trimmed.Length then
        rewritten <- true
        line.Replace("use ", "let ")
      else
        line
    )
  
  if rewritten then
    String.Join("\n", rewrittenLines)
  else
    code
