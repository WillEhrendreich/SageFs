namespace SageFs.Features.LiveTesting

/// Why a test's coverage cannot be a cache key.
[<RequireQualifiedAccess>]
type UntrustedHash =
  /// The build has no instrumentation, so there is nothing a bitmap could say.
  | NoInstrumentation
  /// The bitmap is of another size than the instrumentation (a stale bitmap against a rebuilt map): bitmap probes, map probes.
  | BitmapDoesNotMatchMap of bitmapProbes: int * mapProbes: int
  /// The bitmap hit nothing, so it names no file and its hash would be the toolchain's alone.
  | CoversNothing

/// A test's coverage as a cache key: the key, or why there is none.
[<RequireQualifiedAccess>]
type HashTrust =
  | Trusted of hash: string
  | Untrusted of UntrustedHash

/// Coverage-aware wiring for `InputHash` (`TestRunKey.fs`) — the "later
/// phase" that file's own doc comment defers: computing a test's
/// `InputHash` from the real source content its `CoverageBitmap` says it
/// exercises, instead of an arbitrary caller-assembled content list.
///
/// Compile-order note: in `SageFs.Core.fsproj`, `TestRunKey.fs` is already
/// compiled AFTER `LiveTestingTypes.fs` (which owns `SequencePoint` /
/// `InstrumentationMap` / `CoverageBitmap`), so it could technically see the
/// coverage types directly. This module still lives in its own file rather
/// than inside `TestRunKey.fs`, so that file keeps the "pure, no way to see
/// bitmaps or disk" contract its own doc comment promises, and any future
/// resort of the coverage types relative to `TestRunKey.fs` cannot break
/// this module's compile — it is placed after both in the fsproj.
module InputHashCoverage =

  /// The distinct source files whose instrumented slots are hit in
  /// `bitmap`, sorted for a deterministic hash regardless of slot iteration
  /// order. A `bitmap`/`map` length mismatch (e.g. a stale bitmap against a
  /// rebuilt instrumentation map) cannot read out of range: iteration is
  /// bounded to `0 .. map.Slots.Length - 1`, and `CoverageBitmap.isSet`
  /// itself fails closed (`false`) for any index outside `bitmap.Count`.
  let coveredFiles (map: InstrumentationMap) (bitmap: CoverageBitmap) : string list =
    [ 0 .. map.Slots.Length - 1 ]
    |> List.filter (fun i -> CoverageBitmap.isSet i bitmap)
    |> List.map (fun i -> map.Slots.[i].File)
    |> List.distinct
    |> List.sort

  /// Sentinel folded into the hash in place of content for a covered file
  /// the reader could not read (deleted, permission error, ...). Distinct
  /// from any content a `Read`-style function could plausibly return, so a
  /// covered file's disappearance changes the hash rather than silently
  /// dropping out of it.
  [<Literal>]
  let private missingSentinel = " <missing>"

  /// A stable fingerprint of the BUILD/TOOLCHAIN identity — the pinned
  /// dependency versions (`Directory.Packages.props`), the build
  /// config/TFM/`DefineConstants` (`Directory.Build.props`), the loaded
  /// `FSharp.Core` version, and the running .NET runtime. Folded into every
  /// test's `InputHash` (see `ofCoverage`) so that ANY toolchain change — an
  /// SDK bump, a dependency version change, a config/constant change — is
  /// structurally a cache MISS even when zero source files were edited.
  /// Without it a landing could serve a stale "verified" result after a
  /// compiler/dependency change: the covered source is byte-identical, so its
  /// content hash is unchanged, yet the compiled/executed behaviour differs
  /// (roast-6 #1 — a bounded false-green).
  ///
  /// Pure: `readFile` is injected (a props file that can't be read folds in as
  /// `"absent"`, so its appearance/disappearance also shifts the fingerprint).
  /// A stable token for the SageFs daemon's OWN test-verification semantics —
  /// how it discovers, selects (affected-set), runs, and interprets the
  /// pass/fail of a test. It leads the toolchain fingerprint so that a change to
  /// the daemon itself invalidates every cached landing result, even when the
  /// user project's deps and covered source are byte-identical. Roast-7 §7: the
  /// old key captured the user project's toolchain (Directory.Packages.props →
  /// Expecto version, FSharp.Core, runtime) but NOT the daemon that decides what
  /// a "pass" means — so a daemon upgrade that changed the affected-set
  /// algorithm (e.g. roast-7 §4) or the runner's outcome mapping would serve a
  /// stale "verified" result. Derived from SageFs.Core's own assembly version
  /// (auto-bumped per release by the version hook) so it advances automatically
  /// with every daemon build — conservative (a version bump that did not change
  /// semantics still invalidates), which is the correct trade for a cache whose
  /// wrong answer is "landed an unverified change".
  let sagefsSemanticsVersion () : string =
    string (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version)

  let toolchainFingerprint (sagefsSemantics: string) (readFile: string -> string option) (repoRoot: string) : string =
    let propsFingerprint (name: string) =
      match readFile (System.IO.Path.Combine(repoRoot, name)) with
      | Some content -> InputHash.ofContent content
      | None -> "absent"

    InputHash.compute
      [ "sagefs"; sagefsSemantics
        "packages"; propsFingerprint "Directory.Packages.props"
        "build"; propsFingerprint "Directory.Build.props"
        "fsharpcore"; string (typeof<int list>.Assembly.GetName().Version)
        "runtime"; System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription ]

  /// A test's `InputHash`, computed from the `toolchain` fingerprint PLUS the
  /// real content of every source file its `bitmap` says it exercises (§5.3).
  /// The `toolchain` fingerprint (see `toolchainFingerprint`) leads the hashed
  /// sequence so a compiler/dependency/config change invalidates every cached
  /// result even when the covered source is byte-identical. For each covered
  /// file, in sorted order, feeds BOTH the file's path and its content (or
  /// `missingSentinel` when `readFile` returns `None`) into `InputHash.compute`
  /// — the path guards against two different covered file sets colliding just
  /// because their contents happen to match, and `InputHash.compute` already
  /// length-prefixes every element, so a path/content pair can never be split
  /// ambiguously across entries. An empty covered set still produces a stable
  /// hash (of just the toolchain fingerprint), rather than throwing.
  ///
  /// Pure: `readFile` is injected so the caller decides how (and whether)
  /// to touch disk — this function performs no IO itself.
  let private ofFiles (toolchain: string) (readFile: string -> string option) (files: string list) : string =
    "toolchain" :: toolchain
    :: (files
        |> List.collect (fun file ->
          let content =
            match readFile file with
            | Some c -> c
            | None -> missingSentinel

          [ file; content ]))
    |> InputHash.compute

  let ofCoverage
    (toolchain: string)
    (readFile: string -> string option)
    (map: InstrumentationMap)
    (bitmap: CoverageBitmap)
    : string =
    ofFiles toolchain readFile (coveredFiles map bitmap)

  /// The files a test may depend on: the ones its coverage names, and `blind`, the files coverage cannot see (those evaluated
  /// over the instrumented build, which run as uninstrumented evaluated code). Each once, sorted.
  let seenFiles (blind: string list) (map: InstrumentationMap) (bitmap: CoverageBitmap) : string list =
    coveredFiles map bitmap @ blind |> List.distinct |> List.sort

  /// What the affected-test selection is told a test depends on: `seenFiles`, except that coverage which names no file stays
  /// empty. `AffectedTests.affected` runs a test whose covered files are empty, and evaluated files must not turn that empty into
  /// an answer that skips the test.
  let selectionFiles (blind: string list) (map: InstrumentationMap) (bitmap: CoverageBitmap) : string list =
    match coveredFiles map bitmap with
    | [] -> []
    | _ -> seenFiles blind map bitmap

  /// Whether a test's coverage can be a cache key, and the key when it can. A key has to see the code: a hash that is the same
  /// whatever the files say would hand one landing the verdict cached for another. So a test whose bitmap hit nothing, whose bitmap
  /// is of another size than the instrumentation, or that has no instrumentation at all, has no key and is always run.
  ///
  /// `blind` is the files coverage cannot see, folded into the key as files the test may depend on (`seenFiles`): an edit to one
  /// of them must be a different key.
  let trustBeyond
    (blind: string list)
    (toolchain: string)
    (readFile: string -> string option)
    (map: InstrumentationMap)
    (bitmap: CoverageBitmap)
    : HashTrust =
    match map.Slots.Length with
    | 0 -> HashTrust.Untrusted UntrustedHash.NoInstrumentation
    | _ ->
      match bitmap.Count = map.TotalProbes && bitmap.Count > 0 with
      | false -> HashTrust.Untrusted (UntrustedHash.BitmapDoesNotMatchMap (bitmap.Count, map.TotalProbes))
      | true ->
        match coveredFiles map bitmap with
        | [] -> HashTrust.Untrusted UntrustedHash.CoversNothing
        | _ -> HashTrust.Trusted (ofFiles toolchain readFile (seenFiles blind map bitmap))

  /// `trustBeyond` with no file coverage cannot see.
  let trust
    (toolchain: string)
    (readFile: string -> string option)
    (map: InstrumentationMap)
    (bitmap: CoverageBitmap)
    : HashTrust =
    trustBeyond [] toolchain readFile map bitmap

  /// The key, for the one seam that still takes an option (`LandingCache.verify`: a test with no key is run and never cached).
  let keyOf (trusted: HashTrust) : string option =
    match trusted with
    | HashTrust.Trusted hash -> Some hash
    | HashTrust.Untrusted _ -> None
