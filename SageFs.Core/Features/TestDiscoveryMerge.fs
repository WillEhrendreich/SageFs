namespace SageFs.Features.LiveTesting

/// Pure merge of the compiled test-discovery baseline with the latest
/// FSI-evaluated ("dynamic") discovery, keyed by `TestId`. `dynamic` always
/// wins on a shared `TestId` — it reflects the currently-eval'd buffer,
/// which is the freshest truth about what a test IS right now, even before
/// the on-disk file is saved.
///
/// This is the foundation the worker's live `GetTestDiscovery` and the
/// daemon's model merge both compose over (see
/// live-testing-asyoutype-plan.md §2, Brief 1) — it is the single source of
/// truth for "which version of a test wins" so that an edited-but-unsaved
/// test overrides its compiled twin instead of appearing as a duplicate.
module TestDiscoveryMerge =

  /// Merge `compiled` with `dynamic`; `dynamic` overrides `compiled` per
  /// `TestId`. Total — never throws, handles empty and duplicate-`TestId`
  /// inputs.
  ///
  /// Order: entries carried from `compiled` (overridden or not) keep
  /// `compiled`'s relative order; entries that exist ONLY in `dynamic` are
  /// appended afterward, in `dynamic`'s relative order. A `TestId` that
  /// repeats within a single input array keeps only that array's FIRST
  /// occurrence (a real discovered-test set should never do this, but the
  /// merge never faults on it).
  let merge (compiled: TestCase[]) (dynamic: TestCase[]) : TestCase[] =
    if Array.isEmpty dynamic then
      compiled
    elif Array.isEmpty compiled then
      dynamic
    else
      // Last occurrence wins for a TestId that repeats within `dynamic`
      // itself — consistent with "the latest FSI eval wins".
      let dynamicById =
        dynamic |> Array.fold (fun acc (t: TestCase) -> Map.add t.Id t acc) Map.empty

      let seen = System.Collections.Generic.HashSet<TestId>()

      let overridden =
        compiled
        |> Array.choose (fun c ->
          if seen.Contains c.Id then
            None
          else
            seen.Add c.Id |> ignore
            match Map.tryFind c.Id dynamicById with
            | Some d -> Some d
            | None -> Some c)

      let dynamicOnly =
        dynamic
        |> Array.choose (fun d ->
          if seen.Contains d.Id then
            None
          else
            seen.Add d.Id |> ignore
            Some d)

      Array.append overridden dynamicOnly
