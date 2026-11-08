/// "Since green" (docs/plans/live-bindings-watch-view.md, step 11): what moved after the tests last settled with no failures. The
/// question the test loop raises is "what moved when my test went red?", and the answer is a comparison of the values now with the values
/// at the last green settle. The pane keeps the last green settle's values (`Green`) and replaces them only by another green settle, so
/// while the loop is red, running or held back the comparison point stays where the tests were last known good.
module SageFs.Features.Tweak.SinceGreen

open SageFs.Features.LiveTestActivity
open SageFs.Features.LiveValueTree

/// What a walk held, node by node: the text each node showed, by the node's place (`pathKey`).
type Values = Map<string, string>

/// The values at the last green settle, or that there has been none.
[<RequireQualifiedAccess>]
type Green =
  | NoGreenYet
  | GreenAt of Values

/// How one node differs from the last green settle.
[<RequireQualifiedAccess>]
type Moved =
  /// The node was there and showed something else.
  | Differs of before: string * after: string
  /// The node was not there at the last green settle.
  | Appeared of after: string
  /// The node was there at the last green settle and is not now.
  | Left of before: string

let private separator = "\u001f"

/// The place of a node: its binding and the labels down to it.
let pathKey (binding: string) (labels: string list) : string = binding :: labels |> String.concat separator

/// The binding a place belongs to.
let bindingOf (key: string) : string =
  // Ordinal: a culture-sensitive search treats the control character as ignorable and finds it at 0.
  match key.IndexOf(separator, System.StringComparison.Ordinal) with
  | -1 -> key
  | at -> key.Substring(0, at)

/// Every node of a walk by its place.
let valuesOf (snapshot: LiveValueSnapshot) : Values =
  let rec nodes (binding: string) (labels: string list) (node: LiveValueNode) : (string * string) list =
    (pathKey binding labels, node.Preview)
    :: (node.Children |> List.collect (fun child -> nodes binding (labels @ [ child.Label ]) child))
  snapshot.Bindings
  |> List.collect (fun b -> b.Root.Children |> List.collect (fun child -> nodes b.Name [ child.Label ] child) |> List.append [ (pathKey b.Name [], b.Root.Preview) ])
  |> Map.ofList

/// Whether the loop has settled with at least one test passed and none failed: the only moment the comparison point moves.
let isGreenSettle (activity: LiveTestActivity) : bool =
  match activity with
  | LiveTestActivity.Settled tally -> tally.Failed = 0 && tally.Passed > 0
  | LiveTestActivity.Off
  | LiveTestActivity.Discovering
  | LiveTestActivity.DiscoveryFailed _
  | LiveTestActivity.NoTestsFound _
  | LiveTestActivity.BlockedByCompileErrors _
  | LiveTestActivity.BlockedByFailedRebuild _
  | LiveTestActivity.Rebuilding _
  | LiveTestActivity.Running _ -> false

/// The comparison point after the loop says `activity` and the walk holds `current`: the walk, if this is a green settle, else what it was.
let update (green: Green) (activity: LiveTestActivity) (current: Values) : Green =
  match isGreenSettle activity with
  | true -> Green.GreenAt current
  | false -> green

/// What moved between the last green settle and now, by place. Nothing before the first green settle.
let moves (green: Green) (current: Values) : Map<string, Moved> =
  match green with
  | Green.NoGreenYet -> Map.empty
  | Green.GreenAt before ->
    let nowOrDiffers =
      current
      |> Map.toList
      |> List.choose (fun (key, after) ->
        match Map.tryFind key before with
        | Some was when was = after -> None
        | Some was -> Some(key, Moved.Differs(was, after))
        | None -> Some(key, Moved.Appeared after))
    let gone =
      before
      |> Map.toList
      |> List.choose (fun (key, was) ->
        match Map.containsKey key current with
        | true -> None
        | false -> Some(key, Moved.Left was))
    Map.ofList (nowOrDiffers @ gone)

/// The bindings with a node that moved.
let movedBindings (moved: Map<string, Moved>) : Set<string> =
  moved |> Map.toList |> List.map (fst >> bindingOf) |> Set.ofList
