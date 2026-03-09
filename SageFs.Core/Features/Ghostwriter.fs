namespace SageFs.Features

/// A binding in scope for suggestion generation.
type ScopeBinding = {
  Name: string
  TypeSig: string
  Value: string option
}

/// A suggested next evaluation.
type Suggestion = {
  Code: string
  Explanation: string
  Confidence: float
}

module Ghostwriter =

  let private listSuggestions (b: ScopeBinding) =
    [ { Code = sprintf "%s |> List.length" b.Name; Explanation = "Count items"; Confidence = 0.9 }
      { Code = sprintf "%s |> List.head" b.Name; Explanation = "Get first item"; Confidence = 0.8 }
      { Code = sprintf "%s |> List.rev" b.Name; Explanation = "Reverse the list"; Confidence = 0.7 }
      { Code = sprintf "%s |> List.distinct" b.Name; Explanation = "Remove duplicates"; Confidence = 0.6 }
      { Code = sprintf "%s |> List.sort" b.Name; Explanation = "Sort ascending"; Confidence = 0.6 } ]

  let private arraySuggestions (b: ScopeBinding) =
    [ { Code = sprintf "%s |> Array.length" b.Name; Explanation = "Count elements"; Confidence = 0.9 }
      { Code = sprintf "%s |> Array.head" b.Name; Explanation = "Get first element"; Confidence = 0.8 }
      { Code = sprintf "%s |> Array.sort" b.Name; Explanation = "Sort ascending"; Confidence = 0.7 } ]

  let private optionSuggestions (b: ScopeBinding) =
    [ { Code = sprintf "%s |> Option.defaultValue _" b.Name; Explanation = "Unwrap with default"; Confidence = 0.9 }
      { Code = sprintf "%s |> Option.map (fun x -> x)" b.Name; Explanation = "Transform inner value"; Confidence = 0.8 }
      { Code = sprintf "%s |> Option.isSome" b.Name; Explanation = "Check if has value"; Confidence = 0.7 } ]

  let private resultSuggestions (b: ScopeBinding) =
    [ { Code = sprintf "%s |> Result.map (fun x -> x)" b.Name; Explanation = "Transform Ok value"; Confidence = 0.9 }
      { Code = sprintf "%s |> Result.mapError (fun e -> e)" b.Name; Explanation = "Transform Error value"; Confidence = 0.8 }
      { Code = sprintf "%s |> Result.isOk" b.Name; Explanation = "Check if succeeded"; Confidence = 0.7 } ]

  let private mapSuggestions (b: ScopeBinding) =
    [ { Code = sprintf "%s |> Map.toList" b.Name; Explanation = "Convert to key-value list"; Confidence = 0.9 }
      { Code = sprintf "%s |> Map.count" b.Name; Explanation = "Count entries"; Confidence = 0.8 }
      { Code = sprintf "%s |> Map.keys |> Seq.toList" b.Name; Explanation = "Get all keys"; Confidence = 0.7 } ]

  let private stringSuggestions (b: ScopeBinding) =
    [ { Code = sprintf "%s.Length" b.Name; Explanation = "Get string length"; Confidence = 0.8 }
      { Code = sprintf "%s |> String.length" b.Name; Explanation = "Get string length (functional)"; Confidence = 0.8 }
      { Code = sprintf "%s.ToUpper()" b.Name; Explanation = "Convert to uppercase"; Confidence = 0.6 } ]

  let private numericSuggestions (b: ScopeBinding) =
    [ { Code = sprintf "%s |> abs" b.Name; Explanation = "Absolute value"; Confidence = 0.6 }
      { Code = sprintf "%s |> string" b.Name; Explanation = "Convert to string"; Confidence = 0.5 } ]

  let private suggestForBinding (b: ScopeBinding) : Suggestion list =
    let t = b.TypeSig
    match () with
    | _ when t.EndsWith(" list") || t.EndsWith("list") -> listSuggestions b
    | _ when t.EndsWith(" array") || t.EndsWith("[]") -> arraySuggestions b
    | _ when t.Contains("option") -> optionSuggestions b
    | _ when t.StartsWith("Result<") || t.Contains("Result<") -> resultSuggestions b
    | _ when t.StartsWith("Map<") || t.Contains("Map<") -> mapSuggestions b
    | _ when t = "string" -> stringSuggestions b
    | _ when t = "int" || t = "float" || t = "decimal" || t = "int64" -> numericSuggestions b
    | _ -> []

  /// Generate suggestions from current bindings in scope.
  let suggest (bindings: ScopeBinding list) : Suggestion list =
    bindings
    |> List.collect suggestForBinding
    |> List.sortByDescending (fun s -> s.Confidence)

  /// Format a single suggestion for display.
  let formatSuggestion (s: Suggestion) : string =
    sprintf "💡 %s  — %s" s.Code s.Explanation

  /// Format a panel of suggestions.
  let formatPanel (suggestions: Suggestion list) : string =
    match suggestions with
    | [] -> "💡 Suggestions: none"
    | _ ->
      let lines =
        suggestions
        |> List.mapi (fun i s -> sprintf "  %d. %s" (i + 1) (formatSuggestion s))
      sprintf "💡 Suggestions:\n%s" (lines |> String.concat "\n")
