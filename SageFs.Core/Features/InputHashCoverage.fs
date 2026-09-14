namespace SageFs.Features.LiveTesting

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
  let toolchainFingerprint (readFile: string -> string option) (repoRoot: string) : string =
    let propsFingerprint (name: string) =
      match readFile (System.IO.Path.Combine(repoRoot, name)) with
      | Some content -> InputHash.ofContent content
      | None -> "absent"

    InputHash.compute
      [ "packages"; propsFingerprint "Directory.Packages.props"
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
  let ofCoverage
    (toolchain: string)
    (readFile: string -> string option)
    (map: InstrumentationMap)
    (bitmap: CoverageBitmap)
    : string =
    "toolchain" :: toolchain
    :: (coveredFiles map bitmap
        |> List.collect (fun file ->
          let content =
            match readFile file with
            | Some c -> c
            | None -> missingSentinel

          [ file; content ]))
    |> InputHash.compute
