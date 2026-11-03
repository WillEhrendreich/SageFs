namespace SageFs

open SageFs.Features.AutoCompletion

/// What the symbol ladder says about a member's kind (docs/progressive-map-research-2026-10-24.md section 9):
/// whether a client can ask for its members, and the words a count line uses. The kind itself is the one FCS
/// gave the member, carried from the worker as a `CompletionKind`.
module McpMemberKinds =

  /// Whether a client can ask for the members of a name. The split is `LadderKind.rung` (LadderContract.fs),
  /// which the VS Code Type Explorer compiles too, so the daemon and the extension cannot disagree.
  let rungOf (kind: CompletionKind) : LadderRung =
    kind |> CompletionKind.toLadder |> LadderKind.rung

  let rungLabel (rung: LadderRung) : string =
    match rung with
    | LadderRung.Drillable -> "drillable"
    | LadderRung.Leaf -> "leaf"

  /// The words a count line uses for a kind: singular and plural. Exhaustive, so a new kind must name itself.
  let kindNoun (kind: CompletionKind) : string * string =
    match kind with
    | CompletionKind.Class -> "class", "classes"
    | CompletionKind.Constant -> "constant", "constants"
    | CompletionKind.Delegate -> "delegate", "delegates"
    | CompletionKind.Enum -> "enum", "enums"
    | CompletionKind.EnumMember -> "enum member", "enum members"
    | CompletionKind.Event -> "event", "events"
    | CompletionKind.Exception -> "exception", "exceptions"
    | CompletionKind.Field -> "field", "fields"
    | CompletionKind.Interface -> "interface", "interfaces"
    | CompletionKind.Method -> "method", "methods"
    | CompletionKind.OverriddenMethod -> "overridden method", "overridden methods"
    | CompletionKind.Module -> "module", "modules"
    | CompletionKind.Namespace -> "namespace", "namespaces"
    | CompletionKind.Property -> "property", "properties"
    | CompletionKind.Struct -> "struct", "structs"
    | CompletionKind.Typedef -> "type abbreviation", "type abbreviations"
    | CompletionKind.Type -> "type", "types"
    | CompletionKind.Union -> "union", "unions"
    | CompletionKind.Variable -> "value", "values"
    | CompletionKind.ExtensionMethod -> "extension method", "extension methods"
    | CompletionKind.TypeParameter -> "type parameter", "type parameters"
    | CompletionKind.Keyword -> "keyword", "keywords"
    | CompletionKind.Folder -> "folder", "folders"
    | CompletionKind.File -> "file", "files"

  let kindCountPhrase (kind: CompletionKind) (count: int) : string =
    let singular, plural = kindNoun kind
    sprintf "%d %s" count (match count with | 1 -> singular | _ -> plural)
