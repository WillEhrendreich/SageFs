module SageFs.Simulation.HolderRewriteSim

/// WHY — the side-condition classifier is the gate on a SOURCE REWRITE: get it
/// wrong and SageFs silently changes the meaning of a user's program, which is
/// worse than restarting. The property worth folding is therefore not "does it
/// refuse X" but "does it EVER rewrite a binding that has any unsafe fact" —
/// across the full cross-product of conditions, which hand-written cases
/// cannot enumerate.
///
/// The twin is the bug a real implementation would have: check only SOME
/// conditions (say, inline and literal) and treat the rest as fine. That must
/// produce a rewrite of an unsafe binding, and so must break the invariant.

open SageFs
open SageFs.HolderRewrite

let baseTimeUtc = System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc)

/// The safe side of every condition, and the three ways a binding can break
/// each. Generated rather than enumerated by hand so the cross-product is
/// real.
type Scenario =
  { Seed: int
    Name: string
    IsModuleLevel: bool
    Inline: Inline
    Literal: LiteralAttr
    AddressTaken: AddressTaken
    ByRef: ByRefUsage
    InQuotation: InQuotation
    Pinned: PinnedStorage
    Visibility: Visibility
    Copy: CopySemantics }

type Trace =
  { Scenario: Scenario
    Rewritten: bool
    /// True when the binding had at least one fact that must force a refusal.
    Unsafe: bool
    /// The refusal reasons, so a failure can name the condition.
    Reasons: string list }

let private pick (rng: System.Random) options =
  options |> List.item (rng.Next(options.Length))

/// The unsafe value of each condition, or the safe one.
let scenarioOf (seed: int) : Scenario =
  let rng = System.Random(seed)

  { Seed = seed
    Name = sprintf "b%d" (seed % 50)
    IsModuleLevel = rng.Next(4) <> 0
    Inline = if rng.Next(3) = 0 then HolderRewrite.IsInline else HolderRewrite.NotInline
    Literal = if rng.Next(3) = 0 then HolderRewrite.HasLiteralAttr else HolderRewrite.NoLiteralAttr
    AddressTaken =
      if rng.Next(3) = 0 then
        HolderRewrite.AddressTaken "&x"
      else
        HolderRewrite.NeverAddressTaken
    ByRef =
      match rng.Next(4) with
      | 0 -> HolderRewrite.PassedByRef "out parameter"
      | 1 -> HolderRewrite.ByRefLike "Span<int>"
      | _ -> HolderRewrite.NeverByRef
    InQuotation =
      if rng.Next(4) = 0 then
        HolderRewrite.InsideQuotation "<@ @>"
      else
        HolderRewrite.NotInQuotation
    Pinned = if rng.Next(4) = 0 then HolderRewrite.Pinned "[<ThreadStatic>]" else HolderRewrite.NotPinned
    Visibility =
      if rng.Next(3) = 0 then HolderRewrite.PublicAcrossAssembly else HolderRewrite.ModulePrivate
    Copy =
      match rng.Next(3) with
      | 0 -> HolderRewrite.StructCopySemanticsUnknown
      | 1 -> HolderRewrite.StructCopySemanticsPreserved
      | _ -> HolderRewrite.ReferenceSemantics }

let private factsOf (s: Scenario) : BindingFacts =
  { Name = s.Name
    IsModuleLevel = s.IsModuleLevel
    Inline = s.Inline
    Literal = s.Literal
    AddressTaken = s.AddressTaken
    ByRef = s.ByRef
    InQuotation = s.InQuotation
    Pinned = s.Pinned
    Visibility = s.Visibility
    Copy = s.Copy }

/// Whether the binding has ANY fact that must force a refusal. Derived
/// independently of the classifier, so a bug in the classifier cannot hide
/// behind it.
let unsafeOf (s: Scenario) : bool =
  let unsafeInline = match s.Inline with | HolderRewrite.IsInline -> true | _ -> false
  let unsafeLiteral = match s.Literal with | HolderRewrite.HasLiteralAttr -> true | _ -> false
  let unsafeAddress = match s.AddressTaken with | HolderRewrite.AddressTaken _ -> true | _ -> false

  let unsafeByRef =
    match s.ByRef with
    | HolderRewrite.PassedByRef _ -> true
    | HolderRewrite.ByRefLike _ -> true
    | HolderRewrite.NeverByRef -> false

  let unsafeQuotation = match s.InQuotation with | HolderRewrite.InsideQuotation _ -> true | _ -> false
  let unsafePinned = match s.Pinned with | HolderRewrite.Pinned _ -> true | _ -> false
  let unsafeVisibility = match s.Visibility with | HolderRewrite.PublicAcrossAssembly -> true | _ -> false
  let unsafeCopy = match s.Copy with | HolderRewrite.StructCopySemanticsUnknown -> true | _ -> false

  not s.IsModuleLevel
  || unsafeInline
  || unsafeLiteral
  || unsafeAddress
  || unsafeByRef
  || unsafeQuotation
  || unsafePinned
  || unsafeVisibility
  || unsafeCopy

/// The real classifier.
let run (scenario: Scenario) : Trace =
  match classify (factsOf scenario) with
  | Decision.Rewrite _ -> { Scenario = scenario; Rewritten = true; Unsafe = unsafeOf scenario; Reasons = [] }
  | Decision.Refuse reasons -> { Scenario = scenario; Rewritten = false; Unsafe = unsafeOf scenario; Reasons = reasons }

/// TWIN — checks only the two cheapest conditions and treats the rest as fine.
/// This is the bug the invariant must catch: a rewrite of an unsafe binding.
let runPartialCheckTwin (scenario: Scenario) : Trace =
  let checkedInline = match scenario.Inline with | HolderRewrite.IsInline -> false | _ -> true
  let checkedLiteral = match scenario.Literal with | HolderRewrite.HasLiteralAttr -> false | _ -> true

  let rewritten = checkedInline && checkedLiteral && scenario.IsModuleLevel

  { Scenario = scenario
    Rewritten = rewritten
    Unsafe = unsafeOf scenario
    Reasons = if rewritten then [] else [ "the twin's partial check refused" ] }
