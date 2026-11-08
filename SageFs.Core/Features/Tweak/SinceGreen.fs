/// "Since green" (docs/plans/live-bindings-watch-view.md, step 11): what moved after the tests last settled with no failures.
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
  | Differs of before: string * after: string
  | Appeared of after: string
  | Left of before: string

/// The place of a node: its binding and the labels down to it.
let pathKey (binding: string) (labels: string list) : string = binding

/// The binding a place belongs to.
let bindingOf (key: string) : string = key

/// Every node of a walk by its place.
let valuesOf (snapshot: LiveValueSnapshot) : Values = Map.empty

/// Whether the loop has settled with at least one test passed and none failed.
let isGreenSettle (activity: LiveTestActivity) : bool = false

/// The comparison point after the loop says `activity` and the walk holds `current`.
let update (green: Green) (activity: LiveTestActivity) (current: Values) : Green = green

/// What moved between the last green settle and now, by place.
let moves (green: Green) (current: Values) : Map<string, Moved> = Map.empty

/// The bindings with a node that moved.
let movedBindings (moved: Map<string, Moved>) : Set<string> = Set.empty
