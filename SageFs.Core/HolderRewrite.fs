namespace SageFs

// ── The holder-rewrite side conditions ────────────────────────────────
//
// `type-migration-direction.md` step 4: a load-time rewrite that injects a
// holder, gated by DECIDABLE side conditions and failing closed per binding.
//
// The document's proof obligation is not "arbitrary F# is safe to rewrite" —
// it is "recognise a decidable subset that is provably safe and refuse
// everything else". That is the same shape as rule 2's escape classifier,
// which treats anything it cannot follow as an escape, and it is why the
// input here is a set of small DUs rather than booleans: `IsInline` and
// "we could not check whether it is inline" must be different values, because
// only the first is a licence to rewrite.
//
// Pure: facts in, a decision out. No IO, so the DST folds the real function
// and the compiler enforces the closed vocabularies.
module HolderRewrite =

  type Inline =
    /// Not `inline`. The only value that permits a rewrite.
    | NotInline
    /// `inline` bindings are expanded at their use sites, so a cell would be
    /// bypassed entirely and the rewrite would be a silent no-op.
    | IsInline

  type LiteralAttr =
    /// No `[<Literal>]`. The only value that permits a rewrite.
    | NoLiteralAttr
    /// A `[<Literal>]` is substituted by the compiler at use sites.
    | HasLiteralAttr

  type AddressTaken =
    /// We established the binding is never address-taken (`&x`).
    | NeverAddressTaken
    /// The binding's address is taken, so it is not merely a value — the cell
    /// would not be what the program holds.
    | AddressTaken of how: string

  type ByRefUsage =
    /// Never passed by reference and not byref-like.
    | NeverByRef
    /// Passed by reference (`in`/`out`/`ref`), which a holder cannot
    /// represent without changing the program's meaning.
    | PassedByRef of how: string
    /// A byref-like type (`Span<'T>` and friends). The CLR forbids these in a
    /// reference-type field, so a holder is not an option at all.
    | ByRefLike of why: string

  type InQuotation =
    /// Not referenced from a quotation.
    | NotInQuotation
    /// Referenced inside a quotation, where the rewrite cannot see the use.
    | InsideQuotation of how: string

  type PinnedStorage =
    /// No interop or `[<ThreadStatic>]`-style attribute pins its storage.
    | NotPinned
    | Pinned of attr: string

  type Visibility =
    /// Module-private: nothing outside this assembly binds the field, so
    /// rewriting the access cannot break an external caller.
    | ModulePrivate
    /// Public: an external caller compiled against the FIELD, not against our
    /// holder, so the rewrite would change what it sees.
    | PublicAcrossAssembly

  type CopySemantics =
    | ReferenceSemantics
    /// A struct whose copy semantics the rewrite preserves at every use site.
    | StructCopySemanticsPreserved
    /// A struct whose copy semantics we have not verified, so it refuses.
    | StructCopySemanticsUnknown

  /// Everything we know — or could not find out — about one binding.
  type BindingFacts =
    { Name: string
      IsModuleLevel: bool
      Inline: Inline
      Literal: LiteralAttr
      AddressTaken: AddressTaken
      ByRef: ByRefUsage
      InQuotation: InQuotation
      Pinned: PinnedStorage
      Visibility: Visibility
      Copy: CopySemantics }

  /// The decision. A DU rather than a bool, because "refuse, and here are all
  /// the reasons" is the common case and a bool would throw them away.
  [<RequireQualifiedAccess>]
  type Decision =
    /// Every condition held. The reasons are recorded so a save can explain
    /// why the rewrite was safe rather than asserting it.
    | Rewrite of because: string list
    /// At least one condition failed. The call site is left untouched and the
    /// process restarts, exactly as today.
    | Refuse of because: string list

  /// Classify one binding. EVERY failing condition is reported, not just the
  /// first, so the message is something a user can act on rather than a
  /// symptom they have to chase.
  let classify (facts: BindingFacts) : Decision =
    let refusals = ResizeArray<string>()

    if not facts.IsModuleLevel then
      refusals.Add "not a module-level binding, so there is no single cell to hold it"

    match facts.Inline with
    | IsInline -> refusals.Add "an inline binding is expanded at its use sites, so a cell would be bypassed"
    | NotInline -> ()

    match facts.Literal with
    | HasLiteralAttr -> refusals.Add "a [<Literal>] is substituted by the compiler, so a cell would be bypassed"
    | NoLiteralAttr -> ()

    match facts.AddressTaken with
    | AddressTaken how ->
      refusals.Add(sprintf "the address of '%s' is taken (%s), so it is not just a value" facts.Name how)
    | NeverAddressTaken -> ()

    match facts.ByRef with
    | PassedByRef how -> refusals.Add(sprintf "'%s' is passed by reference (%s), which a holder cannot represent" facts.Name how)
    | ByRefLike why -> refusals.Add(sprintf "'%s' is byref-like (%s), which the CLR forbids in a cell" facts.Name why)
    | NeverByRef -> ()

    match facts.InQuotation with
    | InsideQuotation how -> refusals.Add(sprintf "'%s' is used inside a quotation (%s), so the rewrite cannot see it" facts.Name how)
    | NotInQuotation -> ()

    match facts.Pinned with
    | Pinned attr -> refusals.Add(sprintf "'%s' carries %s, which pins its storage" facts.Name attr)
    | NotPinned -> ()

    match facts.Visibility with
    | PublicAcrossAssembly ->
      refusals.Add(sprintf "'%s' is public, so an external caller binds the field rather than our holder" facts.Name)
    | ModulePrivate -> ()

    match facts.Copy with
    | StructCopySemanticsUnknown ->
      refusals.Add(sprintf "'%s' is a struct whose copy semantics are unverified" facts.Name)
    | ReferenceSemantics
    | StructCopySemanticsPreserved -> ()

    match refusals.Count with
    | 0 ->
      Decision.Rewrite
        [ "module level"
          "not inline, not [<Literal>]"
          "never address-taken, never byref, not byref-like"
          "not used inside a quotation"
          "no storage-pinning attribute"
          "module-private, so no external caller binds the field" ]
    | _ -> Decision.Refuse(List.ofSeq refusals)

  /// The facts for a binding we could gather NOTHING about. A caller with no
  /// evidence is forced through here, so "unknown" can never be spelled as
  /// "fine".
  let unknown (name: string) : Decision =
    Decision.Refuse [ sprintf "no evidence gathered for '%s', so the rewrite is refused by default" name ]
