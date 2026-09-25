namespace SageFs

// ── Translation validation, per rewrite instance ──────────────────────
//
// `type-migration-direction.md` step 5, and the "better-suited directions"
// section's first entry. The framing is deliberately NOT "prove arbitrary F#
// safe to rewrite" — it is Pnueli's translation validation: check THAT
// SPECIFIC rewrite of THAT SPECIFIC code is equivalent, every time it is
// performed, against today's typed tree. It cannot go stale the way a proof
// transcribed from the source does, and it fails closed.
//
// The gate is layered, and the layering matters:
//
//   step 4 (HolderRewrite)  asks "may this BINDING be rewritten?"
//   step 5 (this module)    asks "is THIS rewrite of ITS use sites sound?"
//
// Passing step 4 says nothing about a use site. A binding can satisfy every
// side condition and still be read inside a loop, captured by a lambda, or
// rebound — and each of those changes what the program MEANS when the read
// becomes a cell access. So the two gates compose, and this one is per use
// site, not per binding.
//
// `Undecidable` is a first-class value, not a fallback. "We could not tell
// whether this is safe" and "we checked, and it is safe" are different claims,
// and only the second is a licence to rewrite. The doc's whole safety posture
// depends on that distinction, so it is in the type rather than in a comment.

module TranslationValidation =

  /// One read or write of a binding being rewritten into a holder cell.
  type UseSite =
    { /// The binding's name, so a refusal names the thing that refused.
      Name: string
      /// A write becomes a cell mutation, which is not the same as rebinding.
      IsWrite: bool
      /// Captured by a lambda: the cell is SHARED where the value was COPIED.
      InLambda: bool
      /// Inside a loop: the rewrite changes how many times the read evaluates,
      /// which is observable for a binding with side effects on read.
      InsideLoop: bool
      /// Evaluated for its value at least once. A binding nobody reads has no
      /// observable behaviour to preserve, and rewriting it is pointless risk.
      Used: bool }

  /// The closed set of outcomes. A DU, not a bool, because "refuse, and here
  /// are all the reasons" is the common case and a bool discards them.
  [<RequireQualifiedAccess>]
  type Verdict =
    /// Provably equivalent. The reasons are recorded so a save can explain why
    /// the rewrite was safe rather than asserting it.
    | Equivalent of because: string list
    /// Provably NOT equivalent: the rewrite would change meaning, so refuse.
    | NotEquivalent of because: string list
    /// We cannot decide this one. A licence to proceed must NEVER be an
    /// `Undecidable`, so a caller cannot accidentally read "unknown" as "fine".
    | Undecidable of because: string list

  /// Everything we could not establish about a use site, collected. Named as a
  /// function so a caller with no evidence is forced through it, rather than
  /// spelling "unknown" as a boolean.
  let undecidable (name: string) (why: string) : Verdict =
    Verdict.Undecidable [ sprintf "cannot decide the rewrite of '%s': %s" name why ]

  /// Validate ONE use site. Every failing condition is reported, not just the
  /// first, so the message is something a user can act on.
  let validateUseSite (site: UseSite) : Verdict =
    let refusals = ResizeArray<string>()

    if not site.Used then
      refusals.Add(sprintf "'%s' is never read, so a cell would preserve nothing" site.Name)

    if site.IsWrite then
      refusals.Add(sprintf "'%s' is written here, and a cell mutation is not the same as rebinding" site.Name)

    if site.InLambda then
      refusals.Add(sprintf "'%s' is captured by a lambda, where the cell would be shared but the value was copied" site.Name)

    if site.InsideLoop then
      refusals.Add(sprintf "'%s' is read inside a loop, so the rewrite changes how many times the read evaluates" site.Name)

    match refusals.Count with
    | 0 -> Verdict.Equivalent [ sprintf "'%s' is read exactly once per enclosing evaluation" site.Name ]
    | _ -> Verdict.NotEquivalent(List.ofSeq refusals)

  /// Validate the WHOLE translation: a rewrite is only licensed when EVERY use
  /// site is individually equivalent. One safe site does not license an unsafe
  /// one, so this is a conjunction over the sites rather than a per-site
  /// result a caller could check selectively.
  let validateRewrite (bindingName: string) (sites: UseSite array) : Verdict =
    match sites with
    | [||] -> undecidable bindingName "no use sites were found, so there is nothing to compare"
    | _ ->
      let verdicts = sites |> Array.map validateUseSite
      let refusals =
        verdicts
        |> Array.toList
        |> List.collect (fun v ->
          match v with
          | Verdict.NotEquivalent reasons -> reasons
          | Verdict.Undecidable reasons -> reasons
          | Verdict.Equivalent _ -> [])

      match refusals with
      | [] ->
        let n = sites.Length
        Verdict.Equivalent [ sprintf "all %d use site(s) of '%s' are individually equivalent" n bindingName ]
      | reasons -> Verdict.NotEquivalent reasons
