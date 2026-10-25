namespace SageFs

open SageFs.Features.AutoCompletion

/// What the symbol ladder says about a member's kind (docs/progressive-map-research-2026-10-24.md section 9):
/// whether a client can ask for its members, and the words a count line uses. The kind itself is the one FCS
/// gave the member, carried from the worker as a `CompletionKind`.
module McpMemberKinds =

  /// Whether a client can ask for the members of a name. The same split the VS Code Type Explorer
  /// makes with its `drillableKinds` set (sagefs-vscode/src/TypeExplorerProvider.fs): that file is
  /// Fable and cannot reference this assembly, so `McpMemberPageTests` reads its set and fails
  /// when it stops equalling the kinds this function calls Drillable.
  [<RequireQualifiedAccess>]
  type Rung =
    | Drillable
    | Leaf

  /// Exhaustive over `CompletionKind` on purpose: a new kind breaks the build here until someone
  /// decides which side of the ladder it sits on.
  let rungOf (kind: CompletionKind) : Rung =
    match kind with
    | CompletionKind.Namespace
    | CompletionKind.Module
    | CompletionKind.Class
    | CompletionKind.Struct
    | CompletionKind.Interface
    | CompletionKind.Enum
    | CompletionKind.Union
    | CompletionKind.Type -> Rung.Drillable
    | CompletionKind.Constant
    | CompletionKind.Delegate
    | CompletionKind.EnumMember
    | CompletionKind.Event
    | CompletionKind.Exception
    | CompletionKind.Field
    | CompletionKind.Method
    | CompletionKind.OverriddenMethod
    | CompletionKind.Property
    | CompletionKind.Typedef
    | CompletionKind.Variable
    | CompletionKind.ExtensionMethod
    | CompletionKind.TypeParameter
    | CompletionKind.Keyword
    | CompletionKind.Folder
    | CompletionKind.File -> Rung.Leaf

  let rungLabel (rung: Rung) : string =
    match rung with
    | Rung.Drillable -> "drillable"
    | Rung.Leaf -> "leaf"

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
