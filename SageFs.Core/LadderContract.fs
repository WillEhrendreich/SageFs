namespace SageFs

/// What the symbol ladder (`list_members`, `list_references`, the VS Code Type Explorer) says about a
/// member's kind and a list's size, written ONCE.
///
/// The daemon and the extension both need these words and this split, and they cannot share the type
/// that carries them: `CompletionKind` lives in a file that depends on the F# compiler, and the
/// extension is compiled by Fable. So this file depends on nothing past FSharp.Core, and both projects
/// compile it (SageFs.Core.fsproj, and sagefs-vscode/src/SageFs.Vscode.fsproj by link, as
/// HatchUse.fs is linked into the agent hook). The daemon's `CompletionKind` maps onto `LadderKind`
/// with an exhaustive match (`CompletionKind.toLadder`), so a new kind breaks the build there until
/// someone says which side of the ladder it sits on; `LadderContractTests` pins that the two unions
/// carry the same names. Before this file the extension kept its own word list and a test scraped
/// both sources to compare them, which proves two copies agree today and nothing about tomorrow.

/// Whether a client can ask for the members of a name.
[<RequireQualifiedAccess>]
type LadderRung =
  | Drillable
  | Leaf

/// The kind FCS gave a member, as a name both sides spell the same way.
[<RequireQualifiedAccess>]
type LadderKind =
  | Namespace
  | Module
  | Class
  | Struct
  | Interface
  | Enum
  | Union
  | Type
  | Constant
  | Delegate
  | EnumMember
  | Event
  | Exception
  | Field
  | Method
  | OverriddenMethod
  | Property
  | Typedef
  | Variable
  | ExtensionMethod
  | TypeParameter
  | Keyword
  | Folder
  | File

/// A `kind` word the contract does not name. It carries the word, so a refusal can say what arrived.
type UnknownKindWord = UnknownKindWord of word: string

module LadderKind =
  /// The wire word for a kind: what `/api/completions` sends as `kind`.
  let label (kind: LadderKind) : string =
    match kind with
    | LadderKind.Namespace -> "Namespace"
    | LadderKind.Module -> "Module"
    | LadderKind.Class -> "Class"
    | LadderKind.Struct -> "Struct"
    | LadderKind.Interface -> "Interface"
    | LadderKind.Enum -> "Enum"
    | LadderKind.Union -> "Union"
    | LadderKind.Type -> "Type"
    | LadderKind.Constant -> "Constant"
    | LadderKind.Delegate -> "Delegate"
    | LadderKind.EnumMember -> "EnumMember"
    | LadderKind.Event -> "Event"
    | LadderKind.Exception -> "Exception"
    | LadderKind.Field -> "Field"
    | LadderKind.Method -> "Method"
    | LadderKind.OverriddenMethod -> "OverriddenMethod"
    | LadderKind.Property -> "Property"
    | LadderKind.Typedef -> "Typedef"
    | LadderKind.Variable -> "Variable"
    | LadderKind.ExtensionMethod -> "ExtensionMethod"
    | LadderKind.TypeParameter -> "TypeParameter"
    | LadderKind.Keyword -> "Keyword"
    | LadderKind.Folder -> "Folder"
    | LadderKind.File -> "File"

  /// Every case, so a word can be looked up by `label` without a second table to keep in step.
  /// `LadderContractTests` fails when a case of the union is missing here.
  let all : LadderKind list =
    [ LadderKind.Namespace; LadderKind.Module; LadderKind.Class; LadderKind.Struct; LadderKind.Interface
      LadderKind.Enum; LadderKind.Union; LadderKind.Type; LadderKind.Constant; LadderKind.Delegate
      LadderKind.EnumMember; LadderKind.Event; LadderKind.Exception; LadderKind.Field; LadderKind.Method
      LadderKind.OverriddenMethod; LadderKind.Property; LadderKind.Typedef; LadderKind.Variable
      LadderKind.ExtensionMethod; LadderKind.TypeParameter; LadderKind.Keyword; LadderKind.Folder
      LadderKind.File ]

  /// The wire boundary. A word nobody knows comes back as itself, so the caller can say what it saw.
  let tryParse (word: string) : Result<LadderKind, UnknownKindWord> =
    match all |> List.tryFind (fun kind -> label kind = word) with
    | Some kind -> Ok kind
    | None -> Error (UnknownKindWord word)

  /// The one definition of which kinds can be drilled into. Exhaustive on purpose.
  let rung (kind: LadderKind) : LadderRung =
    match kind with
    | LadderKind.Namespace
    | LadderKind.Module
    | LadderKind.Class
    | LadderKind.Struct
    | LadderKind.Interface
    | LadderKind.Enum
    | LadderKind.Union
    | LadderKind.Type -> LadderRung.Drillable
    | LadderKind.Constant
    | LadderKind.Delegate
    | LadderKind.EnumMember
    | LadderKind.Event
    | LadderKind.Exception
    | LadderKind.Field
    | LadderKind.Method
    | LadderKind.OverriddenMethod
    | LadderKind.Property
    | LadderKind.Typedef
    | LadderKind.Variable
    | LadderKind.ExtensionMethod
    | LadderKind.TypeParameter
    | LadderKind.Keyword
    | LadderKind.Folder
    | LadderKind.File -> LadderRung.Leaf

/// What a list's size means (`totalKind` on the wire): the count is exact, or only a floor because the list was cut.
[<RequireQualifiedAccess>]
type LadderSizeKind =
  | Exact
  | AtLeast

module LadderSizeKind =
  let label (size: LadderSizeKind) : string =
    match size with
    | LadderSizeKind.Exact -> "exact"
    | LadderSizeKind.AtLeast -> "atLeast"

  /// A word nobody knows is no size at all, and the reader must not guess one.
  let tryParse (word: string) : LadderSizeKind option =
    [ LadderSizeKind.Exact; LadderSizeKind.AtLeast ] |> List.tryFind (fun size -> label size = word)
