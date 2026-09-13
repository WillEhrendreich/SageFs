namespace SageFs.Features.LiveTesting

open System
open System.IO
open System.Security.Cryptography
open System.Text

/// Content-addressed test identity and result cache
/// (sagefs-multiagent-vision.md §5.3 "Test identity, then the matrix").
///
/// Today `TestId.create` hashes only `fullName|framework`
/// (`LiveTestingTypes.fs:96-100`), so two checkouts of one repository — the
/// main checkout and any worktree — produce identical test ids, and the
/// daemon-wide `TestSessionMap` (last-writer-wins) lets whichever session
/// finishes discovery *last* steal every shared test's attribution from the
/// others (§1.3). `TestRunKey` structurally ends that: it keys a run's
/// outcome by `(TestId, SessionId, InputHash)`, so two sessions — even two
/// checkouts of the same repo — can never collide on the same key, and a
/// session's own results are found only under its own `SessionId`.
///
/// This module is pure and has no IO: no daemon, no persistence, no file
/// watching. Wiring `InputHash` to real coverage bitmaps and file content is
/// a later phase (§5.3, Phase 2 item 13); here it is just the key, the hash,
/// and an immutable content-addressed cache.
module InputHash =

  /// A pure, total, deterministic digest over an ordered sequence of input
  /// contents — the bytes that determine a test's outcome. Per §5.3 that is
  /// conventionally the test's own source slice followed by the content of
  /// every file the test's `CoverageBitmap` says it transitively exercises;
  /// this module has no way to see bitmaps or disk, so the caller assembles
  /// the content list (in whatever stable order it chooses — e.g. the test
  /// source first, then dependency files sorted by path) and this function
  /// only hashes it.
  ///
  /// Each input is length-prefixed before it is folded into the digest, so
  /// two different splits of the same bytes (e.g. `["ab"; "c"]` vs.
  /// `["a"; "bc"]`) can never hash to the same value by naive concatenation —
  /// the sequence `contents` itself is part of what is hashed, not just its
  /// concatenated bytes.
  ///
  /// 16 hex chars = 64 bits of entropy from SHA256, matching the convention
  /// `TestId.create` already uses (`LiveTestingTypes.fs:94-95`).
  let compute (contents: string seq) : string =
    use stream = new MemoryStream()
    use writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen = true)

    for content in contents do
      let bytes = Encoding.UTF8.GetBytes(content: string)
      writer.Write(bytes.Length)
      writer.Write(bytes)

    writer.Flush()
    let hash = SHA256.HashData(stream.ToArray())
    Convert.ToHexString(hash).Substring(0, 16)

  /// Convenience for a single already-assembled input string.
  let ofContent (content: string) : string = compute (Seq.singleton content)

/// The content-addressed identity of one run of one test in one session.
/// `SessionId` is what structurally ends cross-checkout test-result stealing
/// (§1.3): two checkouts of the same repo produce the same `TestId` for a
/// shared test, but distinct `SessionId`s, so their `TestRunKey`s — and
/// therefore their cached outcomes — never collide.
type TestRunKey = {
  TestId: TestId
  SessionId: string
  InputHash: string
}

module TestRunKey =
  let create (testId: TestId) (sessionId: string) (inputHash: string) : TestRunKey =
    { TestId = testId
      SessionId = sessionId
      InputHash = inputHash }

/// A pure, immutable, content-addressed cache of test run results.
///
/// "Verification is a cache lookup" (§5.4): a landing (or a member session)
/// whose inputs are byte-identical to a prior run finds the result in the
/// time it takes to compute `InputHash` — no re-run. Because the key
/// includes only `InputHash` (not, say, a monotonic run counter), two
/// different `TestRunKey`s that happen to share the same `TestId` and
/// `InputHash` but differ only in `SessionId` can be populated with the very
/// same `TestRunResult` value the caller already has in hand — that is the
/// "share a result instead of re-running" behavior §5.3 describes; nothing
/// special is needed on the cache's part, the caller just inserts the result
/// it already looked up under the new key.
type TestResultCache = private TestResultCache of Map<TestRunKey, TestRunResult>

module TestResultCache =
  let empty: TestResultCache = TestResultCache Map.empty

  let lookup (key: TestRunKey) (TestResultCache map) : TestRunResult option = Map.tryFind key map

  let insert (key: TestRunKey) (result: TestRunResult) (TestResultCache map) : TestResultCache =
    TestResultCache(Map.add key result map)

  let count (TestResultCache map) : int = Map.count map
