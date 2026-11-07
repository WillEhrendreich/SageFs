/// How much chrome the live bindings pane shows at a given dock height. The bindings themselves are what the pane is for, so the chrome
/// (the title row, the counts, the filter, the sentence, the buttons) gives way in steps as the dock gets shorter, and never takes more than
/// `chromeShare` of the dock. This is the one decision, as a pure function; the stylesheet draws it with container queries on the dock's
/// height, and a test holds the stylesheet's thresholds to `belowRem`, so the two cannot disagree.
module SageFs.Features.DockCompaction

/// The steps, roomiest first.
[<RequireQualifiedAccess>]
type Compaction =
  /// Everything, as before.
  | Roomy
  /// The sentence under the title is gone: its words are the title's tooltip and a note in the Display panel.
  | NoSentence
  /// The counts and the filter join the title's row, and the Display panel takes the tree's place while it is open (above this it
  /// is a region above the tree).
  | OneLine
  /// Paddings and gaps shrink further; the buttons keep their icon and word at the page's one 28px height.
  | Compact
  /// The three buttons are icons with accessible names and a tooltip, still 28px square.
  | IconsOnly

[<RequireQualifiedAccess>]
type SentenceLine =
  | Shown
  | InTooltip

[<RequireQualifiedAccess>]
type CountsPlace =
  | OwnRow
  | OnTitleRow

/// A button is never hidden by compaction: it is its icon and its word, or its icon with its word as the accessible name and tooltip.
[<RequireQualifiedAccess>]
type ButtonLook =
  | IconAndWord
  | IconWithName

/// Where the Display panel opens: a scrollable region above the tree that shares the dock with it, or in the tree's place. It is never
/// a popover over other things, because a popover was clipped in a short dock and floated over the Output section on the stacked layout.
[<RequireQualifiedAccess>]
type PanelPlacement =
  | AboveTree
  | ReplacesTree

[<RequireQualifiedAccess>]
type Spacing =
  | Roomy
  | Tight
  | Tightest

type Chrome =
  { Sentence: SentenceLine
    Counts: CountsPlace
    Buttons: ButtonLook
    Panel: PanelPlacement
    Spacing: Spacing }

/// The steps, roomiest first.
let all : Compaction list =
  [ Compaction.Roomy; Compaction.NoSentence; Compaction.OneLine; Compaction.Compact; Compaction.IconsOnly ]

/// The dock height, in rem, below which a step applies (until the next step's height). The roomiest applies at any taller height.
let belowRem (step: Compaction) : float option =
  match step with
  | Compaction.Roomy -> None
  | Compaction.NoSentence -> Some 26.0
  | Compaction.OneLine -> Some 20.0
  | Compaction.Compact -> Some 14.0
  | Compaction.IconsOnly -> Some 9.0

/// The share of the dock's height the chrome may take, as a percentage. What the chrome does not take is the tree's, which scrolls on its
/// own. 40 is what a single row of the title and its 28px buttons needs in the smallest dock the divider allows (5rem).
let [<Literal>] ChromeSharePercent = 40

/// The step for a dock this tall. A height exactly at a step's limit is still the roomier step.
let levelFor (heightRem: float) : Compaction =
  // The steps whose limit the height is under; the most compact of them is the step (the limits only fall, so it is the lowest limit).
  all
  |> List.filter (fun step ->
    match belowRem step with
    | Some limit -> heightRem < limit
    | None -> false)
  |> List.tryLast
  |> Option.defaultValue Compaction.Roomy

/// What each step shows. Exhaustive, so a new step has to say.
let chromeFor (step: Compaction) : Chrome =
  match step with
  | Compaction.Roomy ->
    { Sentence = SentenceLine.Shown; Counts = CountsPlace.OwnRow; Buttons = ButtonLook.IconAndWord; Panel = PanelPlacement.AboveTree; Spacing = Spacing.Roomy }
  | Compaction.NoSentence ->
    { Sentence = SentenceLine.InTooltip; Counts = CountsPlace.OwnRow; Buttons = ButtonLook.IconAndWord; Panel = PanelPlacement.AboveTree; Spacing = Spacing.Tight }
  | Compaction.OneLine ->
    { Sentence = SentenceLine.InTooltip; Counts = CountsPlace.OnTitleRow; Buttons = ButtonLook.IconAndWord; Panel = PanelPlacement.ReplacesTree; Spacing = Spacing.Tight }
  | Compaction.Compact ->
    { Sentence = SentenceLine.InTooltip; Counts = CountsPlace.OnTitleRow; Buttons = ButtonLook.IconAndWord; Panel = PanelPlacement.ReplacesTree; Spacing = Spacing.Tightest }
  | Compaction.IconsOnly ->
    { Sentence = SentenceLine.InTooltip; Counts = CountsPlace.OnTitleRow; Buttons = ButtonLook.IconWithName; Panel = PanelPlacement.ReplacesTree; Spacing = Spacing.Tightest }
