namespace SageFs

open System
open System.IO
open System.Threading.Tasks
open SageFs.Features
open SageFs.Features.AutoCompletion
open SageFs.McpRouteError
open SageFs.McpSessionRouting
open SageFs.McpTools

/// The edge for the signature-at-caret tool. Reads the file and asks the session for completions, then
/// lets the pure `SignatureQuery` decide the word under the caret, which candidate it names, and the
/// signature. No new wire message: the description rides the `GetDescription` closure that
/// `McpCompletions` already plumbs across the host boundary, so the isolated host stays free of SageFs.
module McpSignatureRoute =

  /// Single-case wrappers: the route takes several strings and ints side by side, and a wrong argument
  /// order would be silent. The wrappers make a swap a compile error instead of an SAGE-005 finding.
  type Agent = Agent of string
  type SourcePath = SourcePath of string
  type CaretLine = CaretLine of int
  type CaretColumn = CaretColumn of int

  let private renderRefusal (r: SignatureQuery.Refusal) =
    SignatureQuery.Refusal.describe r

  let private renderView (v: SignatureQuery.View) =
    sprintf "%s : %s\nKind: %s" v.Name v.Signature v.Kind

  /// Read the source file at the edge. A missing or unreadable file is a named refusal, never an exception.
  let private readSource (path: string) : Result<string[], SignatureQuery.Refusal> =
    if String.IsNullOrWhiteSpace path || not (File.Exists path) then
      Error(SignatureQuery.Refusal.NoFilePath)
    else
      try Ok(File.ReadAllLines path)
      with :? System.IO.IOException -> Error(SignatureQuery.Refusal.NoFilePath)
         | :? System.UnauthorizedAccessException -> Error(SignatureQuery.Refusal.NoFilePath)

  let signatureAt
    (ctx: McpContext)
    (Agent agent)
    (sessionId: string option)
    (workingDirectory: string option)
    (SourcePath filePath)
    (CaretLine line)
    (CaretColumn column)
    : Task<string> =
    task {
      let! resolution = resolveSessionId ctx agent sessionId workingDirectory
      match resolution with
      | Routable sid ->
        match SignatureQuery.plan { SignatureQuery.Request.File = filePath; Line = line; Column = column } with
        | Error refusal -> return renderRefusal refusal
        | Ok request ->
          match readSource request.File with
          | Error refusal -> return renderRefusal refusal
          | Ok source ->
            let lineText = if request.Line - 1 < source.Length then source.[request.Line - 1] else ""
            let word = SignatureQuery.wordAt lineText request.Column

            if String.IsNullOrEmpty word then
              return renderRefusal SignatureQuery.Refusal.NoWordAtCaret
            else
              match SignatureQuery.caretOf source request with
              | Error refusal -> return renderRefusal refusal
              | Ok caret ->
                // The completion path wants the whole source and an absolute cursor; the caret from
                // SignatureQuery is exactly that. Every member so the exact name is present.
                let fullSource = String.concat "\n" source
                let! answer = McpCompletions.getCompletionsLimited ctx agent fullSource caret CompletionLimit.EveryMember (Some sid) None
                match answer with
                | CompletionAnswer.Unavailable reason ->
                  return "signature: the session could not look up completions — " + CompletionUnavailable.describe reason
                | CompletionAnswer.Answered(items, _) ->
                  let names = items |> Seq.map (fun i -> i.DisplayText) |> Seq.toArray
                  match SignatureQuery.pickIndex word names with
                  | None -> return renderRefusal (SignatureQuery.Refusal.NoMatchingCandidate word)
                  | Some index ->
                    let item = items |> Seq.item index
                    let tooltip =
                      match item.GetDescription with
                      | Some getText -> getText () |> Array.map (fun t -> t.Text) |> String.concat ""
                      | None -> ""
                    let fcsAnswer : SignatureQuery.FcsAnswer =
                      { Name = item.DisplayText
                        Tooltip = tooltip
                        Kind = item.Kind.ToString() }
                    match SignatureQuery.render fcsAnswer with
                    | Error refusal -> return renderRefusal refusal
                    | Ok view -> return renderView view
      | other ->
        return "signature: no session to ask — " + formatSessionResolution other
    }
