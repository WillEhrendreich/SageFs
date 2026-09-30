namespace SageFs

open System.Text.RegularExpressions

/// A line that opens a source file, which FSI will not take as an eval.
[<RequireQualifiedAccess>]
type TopLevelDeclaration =
  | NoDeclaration
  | Namespace of name: string
  | Module of name: string

/// Checks on code before it reaches the session, for the mistakes whose FSI error
/// points at nothing an agent can act on.
module EvalPreflight =

  // `module [public|private|internal] [rec] A.B` with an optional `=`. FSI accepts a
  // single-name module with `=` ("module X ="); a dotted name is refused even with
  // `=`, and a module line with no `=` is the file-level form. Measured against FSI.
  let private moduleLine =
    Regex(@"^module\s+(?:(?:public|private|internal|rec)\s+)*([A-Za-z_][\w']*(?:\.[A-Za-z_][\w']*)*)\s*(=)?", RegexOptions.Compiled)

  let private namespaceLine =
    Regex(@"^namespace\s+(?:rec\s+)?(global|[A-Za-z_][\w'.]*)", RegexOptions.Compiled)

  /// Blank lines, `//` comments and attribute lines come before a declaration.
  let private isHeaderLine (line: string) : bool =
    let trimmed = line.Trim()
    trimmed = "" || trimmed.StartsWith "//" || (trimmed.StartsWith "[<" && trimmed.EndsWith ">]")

  let topLevelDeclaration (code: string) : TopLevelDeclaration =
    let first =
      (match isNull code with | true -> "" | false -> code).Split('\n')
      |> Array.tryFind (fun line -> not (isHeaderLine line))
    match first with
    | None -> TopLevelDeclaration.NoDeclaration
    | Some line ->
      let text = line.Trim()
      let ns = namespaceLine.Match text
      let md = moduleLine.Match text
      match ns.Success, md.Success with
      | true, _ -> TopLevelDeclaration.Namespace ns.Groups[1].Value
      | false, true ->
        let name = md.Groups[1].Value
        let hasEquals = md.Groups[2].Success
        let dotted = name.Contains '.'
        match hasEquals && not dotted with
        | true -> TopLevelDeclaration.NoDeclaration
        | false -> TopLevelDeclaration.Module name
      | false, false -> TopLevelDeclaration.NoDeclaration

  /// What to tell the agent, in the tool's own terms. Empty when there is nothing to say.
  let hint (declaration: TopLevelDeclaration) : string =
    let how =
      "FSI takes definitions, not the line that opens a source file. To load the file, send it with file_path=<absolute path> and eval_mode=file and SageFs wraps it in the right module. To try one definition, send just the let bindings without the declaration line."
    match declaration with
    | TopLevelDeclaration.NoDeclaration -> ""
    | TopLevelDeclaration.Namespace name -> sprintf "'namespace %s' is a source-file declaration, and FSI refuses it as an eval. %s" name how
    | TopLevelDeclaration.Module name -> sprintf "'module %s' is a source-file declaration, and FSI refuses it as an eval. %s" name how
