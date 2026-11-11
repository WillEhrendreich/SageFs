/// Pure decisions behind the text a tree row shows to a screen reader and the leaves the API Browser
/// shows when a listing fails.
///
/// WHY this module exists: VS Code joins `TreeItem.label` and `TreeItem.description` for a screen
/// reader with no separator, so Hot Reload rows read "Foo.fs● watching" and Session Context rows
/// read "Session Context4 assemblies | 12 namespaces". Only the Sessions rows set an accessible name.
/// The API Browser's failure leaves ("Not connected", "Error parsing response", "Could not list
/// members") were dead ends: a row with a warning icon and nothing to press.
///
/// No Fable dependency; tested under `dotnet fsi` (tests/TreeRowsContractTests.fsx).
module SageFs.Vscode.TreeRowsPure

/// Glyphs that mark state visually and are read aloud as their Unicode names.
let private decorativeGlyphs = [| '●'; '○' |]

/// The name a screen reader says for a row: the label, an em dash, then the description with its
/// decoration removed. A pipe separator becomes a comma, because a reader says "pipe".
let accessibleName (label: string) (description: string) : string =
  let spoken =
    (match description with
     | null -> ""
     | d -> d).Split(decorativeGlyphs)
    |> String.concat ""
    |> fun d -> d.Replace(" | ", ", ")
    |> fun d -> d.Trim()
  match spoken with
  | "" -> label
  | d -> sprintf "%s — %s" label d

/// What the API Browser says when a listing did not produce members.
[<RequireQualifiedAccess>]
type ApiBrowserLeaf =
  /// There is no client: the daemon is not running.
  | NotConnected
  /// The reply came but could not be read.
  | ParseFailed
  /// The daemon could not look, and said why.
  | CouldNotList of reason: string
  /// The name is real and has no members. Nothing is wrong, so nothing is offered.
  | NoMembers

type ApiBrowserLeafView = {
  Label: string
  Description: string
  /// The theme icon id, without the `$(...)` wrapper.
  Icon: string
  Tooltip: string
  /// The command a click runs, or None when there is nothing for the person to do.
  Command: string option
}

let leafView (leaf: ApiBrowserLeaf) : ApiBrowserLeafView =
  match leaf with
  | ApiBrowserLeaf.NotConnected ->
    { Label = "Not connected"
      Description = ""
      Icon = "warning"
      Tooltip = "SageFs is not running. Click to start it."
      Command = Some "sagefs.start" }
  | ApiBrowserLeaf.ParseFailed ->
    { Label = "Error parsing response"
      Description = ""
      Icon = "warning"
      Tooltip = "The daemon's reply could not be read. Click to open the SageFs output."
      Command = Some "sagefs.showOutput" }
  | ApiBrowserLeaf.CouldNotList reason ->
    { Label = "Could not list members"
      Description = reason
      Icon = "warning"
      Tooltip = sprintf "The daemon could not list this name: %s. Click to open the SageFs output." reason
      Command = Some "sagefs.showOutput" }
  | ApiBrowserLeaf.NoMembers ->
    { Label = "No members"
      Description = ""
      Icon = "info"
      Tooltip = "This name has no members to list."
      Command = None }

/// One of each leaf, so a contract test covers every case. A new case fails `leafView`'s match
/// until it is handled; add its sample here.
let apiBrowserLeafSamples : ApiBrowserLeaf list =
  [ ApiBrowserLeaf.NotConnected
    ApiBrowserLeaf.ParseFailed
    ApiBrowserLeaf.CouldNotList "session not active"
    ApiBrowserLeaf.NoMembers ]
