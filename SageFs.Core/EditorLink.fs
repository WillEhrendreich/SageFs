namespace SageFs

open System

/// Which editor a clickable `file:line:column` link opens. One home for the rule: the worker's dev-reload page and the
/// Settings page both ask it, so the page cannot show one editor while the links open another.
///
/// `SAGEFS_EDITOR` names the editor; when it is unset the conventional `EDITOR` variable is read; when neither is set
/// the answer is VS Code. The text is judged by what it contains, which is how a path such as `/opt/cursor/bin/cursor`
/// or a name such as `Rider` is told from the rest: a name with `rider` or `jetbrains` in it is JetBrains, one with
/// `cursor` is Cursor, and every other editor gets the VS Code link, which is the scheme most editors register.
[<RequireQualifiedAccess>]
type EditorChoice =
  | VsCode
  | Cursor
  | JetBrains

[<RequireQualifiedAccess>]
module EditorChoice =
  [<Literal>]
  let envVar = "SAGEFS_EDITOR"

  /// The conventional variable read when `SAGEFS_EDITOR` is not set.
  [<Literal>]
  let fallbackEnvVar = "EDITOR"

  let all : EditorChoice list = [ EditorChoice.VsCode; EditorChoice.Cursor; EditorChoice.JetBrains ]

  let name (choice: EditorChoice) : string =
    match choice with
    | EditorChoice.VsCode -> "vscode"
    | EditorChoice.Cursor -> "cursor"
    | EditorChoice.JetBrains -> "jetbrains"

  /// The editor a piece of text names. Total: text that names none is VS Code.
  let ofText (text: string | null) : EditorChoice =
    let lower = (match text with | null -> "" | t -> t).ToLowerInvariant()
    match lower.Contains "rider" || lower.Contains "jetbrains" with
    | true -> EditorChoice.JetBrains
    | false ->
      match lower.Contains "cursor" with
      | true -> EditorChoice.Cursor
      | false -> EditorChoice.VsCode

  /// The editor the two variables name: `SAGEFS_EDITOR` when it has text, else `EDITOR`, else VS Code.
  let choose (sagefsEditor: string | null) (editor: string | null) : EditorChoice =
    match sagefsEditor with
    | null | "" -> ofText editor
    | text -> ofText text

  let fromEnvironment () : EditorChoice =
    choose (Environment.GetEnvironmentVariable envVar) (Environment.GetEnvironmentVariable fallbackEnvVar)

  /// The link pattern, with `{file}`, `{line}` and `{col}` for the page's script to fill in.
  let urlPattern (choice: EditorChoice) : string =
    match choice with
    | EditorChoice.JetBrains -> "jetbrains://rider/navigate/reference?path={file}&line={line}&column={col}"
    | EditorChoice.Cursor -> "cursor://file/{file}:{line}:{col}"
    | EditorChoice.VsCode -> "vscode://file/{file}:{line}:{col}"
