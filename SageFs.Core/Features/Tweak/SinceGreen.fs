/// "Since green" (docs/plans/live-bindings-watch-view.md, step 11): what moved after the tests last settled with no failures. The question
/// the test loop raises is "what moved when my test went red?", and the answer is a comparison of the values now with the values at the
/// last green settle. The daemon keeps the walk the last green settle saw (`Green`) and replaces it only on another green settle, which is
/// an edge: one point for each run's settle, taken where the loop settles and never where somebody draws the tab.
module SageFs.Features.Tweak.SinceGreen

open SageFs.Features.LiveTestActivity
open SageFs.Features.LiveValueTree

/// The walk at the last green settle, and the generation of the run that settled, or that there has been none. The walk is the
/// snapshot itself: nothing is copied or flattened when the point is taken, and a comparison reads the two trees side by side.
[<RequireQualifiedAccess>]
type Green =
  | NoGreenYet
  | GreenAt of generation: int64 * walk: LiveValueSnapshot

/// What the loop just did, as far as the comparison point is concerned. A settle is the edge the point is taken at; everything else
/// (running, rebuilding, held back by compile errors, live testing off) does not touch it.
[<RequireQualifiedAccess>]
type SettleEvent =
  /// Run `generation` settled with at least one test passed and none failed.
  | GreenSettled of generation: int64
  /// Run `generation` settled with a failure, or with nothing passed.
  | RedSettled of generation: int64
  /// The loop is not at a settle.
  | NotSettled

/// What the loop's activity is, as an event for the run it is reading.
let settleEvent (generation: int64) (activity: LiveTestActivity) : SettleEvent =
  SettleEvent.NotSettled

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

/// The comparison point after the loop did `event` while the walk held `walk`. A green settle takes the point once for its generation:
/// the same generation read again (the values moved since, a rebuild finished) takes nothing, so the point is the walk at the settle and
/// not the walk at whenever somebody last looked. Any other event leaves it where it was.
let settle (green: Green) (event: SettleEvent) (walk: LiveValueSnapshot) : Green =
  green

/// The child of `children` that has `label`, trying the same position first because a walk has the same shape from one push to the next.
let private childLabeled (index: int) (label: string) (children: LiveValueNode list) : LiveValueNode option =
  match List.tryItem index children with
  | Some same when same.Label = label -> Some same
  | _ -> children |> List.tryFind (fun child -> child.Label = label)

/// Every difference between a node at the green settle and now, by place. `path` is the labels down to the node, nearest first, so a place
/// is spelled only for a node that differs.
let rec private diffNodes (binding: string) (path: string list) (before: LiveValueNode) (after: LiveValueNode) (found: (string * Moved) list) : (string * Moved) list =
  let here =
    match before.Preview = after.Preview with
    | true -> found
    | false -> (pathKey binding (List.rev path), Moved.Differs(before.Preview, after.Preview)) :: found
  let afterwards =
    after.Children
    |> List.indexed
    |> List.fold
      (fun acc (index, child) ->
        match childLabeled index child.Label before.Children with
        | Some was -> diffNodes binding (child.Label :: path) was child acc
        | None -> (pathKey binding (List.rev (child.Label :: path)), Moved.Appeared child.Preview) :: acc)
      here
  before.Children
  |> List.indexed
  |> List.fold
    (fun acc (index, was) ->
      match childLabeled index was.Label after.Children with
      | Some _ -> acc
      | None -> (pathKey binding (List.rev (was.Label :: path)), Moved.Left was.Preview) :: acc)
    afterwards

/// What moved between the last green settle and now, by place: the topmost node of a subtree that appeared or left, and every node that
/// shows something else. Nothing before the first green settle.
let moves (green: Green) (current: LiveValueSnapshot) : Map<string, Moved> =
  match green with
  | Green.NoGreenYet -> Map.empty
  | Green.GreenAt(_, before) ->
    let was = before.Bindings |> List.map (fun b -> b.Name, b) |> Map.ofList
    let now = current.Bindings |> List.map (fun b -> b.Name, b) |> Map.ofList
    let changed =
      current.Bindings
      |> List.fold
        (fun acc b ->
          match Map.tryFind b.Name was with
          | Some earlier -> diffNodes b.Name [] earlier.Root b.Root acc
          | None -> (pathKey b.Name [], Moved.Appeared b.Root.Preview) :: acc)
        []
    let gone =
      before.Bindings
      |> List.filter (fun b -> not (Map.containsKey b.Name now))
      |> List.map (fun b -> pathKey b.Name [], Moved.Left b.Root.Preview)
    Map.ofList (changed @ gone)

/// The bindings with a node that moved.
let movedBindings (moved: Map<string, Moved>) : Set<string> =
  moved |> Map.toList |> List.map (fst >> bindingOf) |> Set.ofList
