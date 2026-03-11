# SageFs Persistence Layer Analysis for SQLite Migration (Synthesis 4.2)

## Executive Summary

The SageFs persistence layer currently uses **three independent binary formats** (.sagefm, .sagefs, .sagetc) with **no abstraction layer**. For SQLite migration, you'll need:
1. A common IPersistence<'T> interface
2. Compliance tests that verify binary↔SQLite round-trip equivalence
3. Migration tooling to convert existing binary files to SQLite

---

## 1. Current Binary Persistence Formats

### 1.1 Three Independent Formats
**All three formats share the same framing architecture but differ in magic bytes, sections, and payloads.**

| Format | Extension | Magic | Version | Purpose | Location |
|--------|-----------|-------|---------|---------|----------|
| **Daemon Manifest** | .sagefm | SFM1 | v1 | Session list, active session, timestamps | SageFs.Core/Features/ManifestPersistence.fs |
| **Session** | .sagefs | SFS3 | v3 | Interactions, outputs, references, code | SageFs.Core/Features/SessionPersistence.fs |
| **Test Cache** | .sagetc | STC1 | v1 | Coverage bitmaps, test outcomes | SageFs.Core/Features/TestCachePersistence.fs |

**File Locations in .sagefs/ directory:**
- daemon.sagefm — singleton daemon manifest
- sessions/{sessionId}.sagefs — one file per session
- cache/{projectHash}.sagetc — one file per project (hash computed from project list)

---

## 2. Persistence Data Model

### 2.1 Domain Types That Get Persisted

#### A. **Daemon-Level State** (from Replay.fs)
\\\sharp
type DaemonReplayState = {
  Sessions: Map<string, DaemonSessionRecord>
  ActiveSessionId: string option
}

type DaemonSessionRecord = {
  SessionId: string
  Projects: string list
  WorkingDir: string
  CreatedAt: DateTimeOffset
  StoppedAt: DateTimeOffset option
}
\\\

#### B. **Session-Level State** (from Replay.fs)
\\\sharp
type SessionReplayState = {
  Status: ReplayStatus
  EvalCount: int
  FailedEvalCount: int
  ResetCount: int
  HardResetCount: int
  LastEvalResult: string option
  WarmupErrors: string list
  EvalHistory: EvalRecord list      // ← Interactions to persist
  LastDiagnostics: DiagnosticEvent list
  StartedAt: DateTimeOffset option
  LastActivity: DateTimeOffset option
}

type EvalRecord = {
  Code: string
  Result: string
  TypeSignature: string option
  Duration: TimeSpan
  Timestamp: DateTimeOffset
}
\\\

#### C. **Test Results State** (from LiveTestingTypes.fs)
\\\sharp
type LiveTestState = {
  TestCoverageBitmaps: Map<TestId, CoverageBitmap>    // ← Persisted as IMAP
  LastResults: Map<TestId, TestRunResult>              // ← Persisted as TRES
  LastGeneration: RunGeneration
  // ... 15 other fields, mostly not persisted
}

type TestRunResult = {
  TestId: TestId
  TestName: string
  Result: TestResult
  Timestamp: DateTimeOffset
  Output: string option
}

[<RequireQualifiedAccess>]
type TestResult =
  | Passed of duration: TimeSpan
  | Failed of failure: TestFailure * duration: TimeSpan
  | Skipped of reason: string
  | NotRun
\\\

---

## 3. Binary Format Architecture

### 3.1 Shared Framing (64-byte header + section directory + CRC)

**All formats use:**
- **Header:** 64 bytes (magic, version, flags, timestamps, section count, file CRC)
- **Directory:** N × 20 bytes (tag, flags, offset, size, section CRC)
- **Sections:** Variable-length payloads, each with CRC-32 integrity check
- **Encoding:** Length-prefixed UTF-8 strings, little-endian integers, no varints

### 3.2 .sagefm v1 (Daemon Manifest)

**Header fields (64 bytes):**
- Magic: \SFM1\ (0x53, 0x46, 0x4D, 0x31)
- Format version: 1 (u16)
- Min reader version: 1 (u16)
- Section count: 1 (always) (u32)
- Flags: 0 (u32)
- Created at: Unix ms (i64)
- Total file size: u64
- Session count: u32
- Header CRC: u32 @ offset 0x24
- Active session ID: optional lp-string (fits in padding)

**Sections:**
- **SESS** (0x53455353) — session entry list
  - count: u32
  - For each: SessionId, projects list, working dir, created ms, stopped ms (or -1)

### 3.3 .sagefs v3 (Session Persistence)

**Header fields (64 bytes):**
- Magic: \SFS3\ (0x53, 0x46, 0x53, 0x33)
- Format version: 3 (u16)
- Min reader version: 3 (u16)
- Section count: 3+ (u32)
- Flags: u32 (has_assembly_cache, has_profiling, has_bindings, section_compression, has_dedup_table)
- Created at: Unix ms (i64)
- Total file size: u64
- Interaction count: u32
- Header CRC: u32 @ offset 0x24
- String dedup count: u32
- Reserved: 20 bytes

**Directory entries (20 bytes each):**
- Tag: u16 (0x4D45=META, 0x494E=INPT, 0x5245=REFS, 0x0004=PROF, 0x0005=BIND)
- Flags: u16
- Offset: u64
- Size: u32
- CRC: u32

**Sections:**
- **META** (0x4D45) — session metadata
  - SageFsVersion, FSharpVersion, DotNetVersion, ProjectPath, WorkingDirectory, SessionId
  - EvalCount, FailedEvalCount
  
- **INPT** (0x494E) — interactions with string pool
  - String pool architecture: deduplicates code and output strings
  - TOC: interaction count (u32), stride (u16=48), entries
  - Each entry (48 bytes): codeOffset, outputOffset, timestampMs, kind, flags, durationMicros, 24-byte pad
  - String pool: size (u32), then strings (length-prefixed UTF-8)
  
- **REFS** (0x5245) — assembly references
  - count: u32
  - For each: kind (byte), path (lp-string)

- **PROF** (optional) (0x0004) — profiling timings per interaction
- **BIND** (optional) (0x0005) — runtime bindings (name, type, value)

### 3.4 .sagetc v1 (Test Cache)

**Header fields (64 bytes):** Similar to SFS3 but:
- Magic: \STC1\ (0x53, 0x54, 0x43, 0x31)
- Format version: 1 (u16)
- Section count: 3 (always) (u32)
- Test count: u32
- Imap generation: u32 @ offset 0x28 (instrumentation map version)

**Directory entries (16 bytes each):** Tag (u32), Offset (u64), CRC (u32)

**Sections:**
- **IMAP** (0x494D4150) — instrumentation map identity
  - count: u32
  - For each: TestId (lp-string), bitmap word count (u32), bitmap words (u64 array)
  
- **TCOV** (0x54434F56) — test coverage metadata (optional, not fully used)
  - count: u32
  - For each: TestId (lp-string), bit count (u32)
  
- **TRES** (0x54524553) — test results
  - count: u32
  - For each: TestId (lp-string), outcome (byte: 0=Pass, 1=Fail, 2=Skip, 3=Error), duration (u32 ms), message (optional lp-string)

---

## 4. Current Persistence API

### 4.1 No Abstraction Layer
**Each format has its own read/write functions. No common interface.**

\\\sharp
// ManifestPersistence.fs
module DaemonPersistence =
  let saveManifest: string → DaemonReplayState → Result<string, string>
  let loadManifest: string → Result<DaemonReplayState, ManifestLoadError>

// SessionPersistence.fs
module DaemonPersistence =
  let saveSession: string → sessionId → projectPath → workDir → refs → SessionReplayState → Result<string, string>
  let loadSession: string → sessionId → Result<SessionReplayState, string>

// TestCachePersistence.fs
module DaemonPersistence =
  let saveTestCache: string → projects → LiveTestState → Result<string, string>
  let loadTestCache: string → projects → Result<LiveTestState, string>
\\\

**Mapping Functions:**
\\\sharp
module ManifestMapping =
  let fromReplayState: DaemonReplayState → ManifestData
  let toReplayState: ManifestData → DaemonReplayState

module SessionMapping =
  let fromReplayState: sessionId → projectPath → workDir → refs → SessionReplayState → SfsData
  let toReplayState: SfsData → SessionReplayState

module TestCacheMapping =
  let fromLiveTestState: LiveTestState → StcData
  let toLiveTestState: StcData → LiveTestState
\\\

---

## 5. Existing Persistence Tests

### 5.1 Test Files and Coverage

| Test File | Location | Coverage |
|-----------|----------|----------|
| **ManifestPersistenceTests.fs** | SageFs.Tests/ | ✅ Binary format, CRC, roundtrip, field preservation |
| **BinaryFormatTests.fs** | SageFs.Tests/ | ✅ Property-based tests (FsCheck), SFS3 + STC1 formats |
| **ThemePersistenceTests.fs** | SageFs.Tests/ | ✅ Theme serialization (separate concern) |

### 5.2 ManifestPersistenceTests.fs Pattern

\\\sharp
[<Tests>]
let manifestBinaryTests = testList "DaemonManifest binary format" [
  testCase "empty manifest roundtrips" <| fun _ ->
    let data = { Entries = []; ActiveSessionId = None; CreatedAtMs = 1709500000000L }
    let bytes = ManifestWriter.write data
    let result = ManifestReader.read bytes
    // Assert fields match

  testCase "CRC detects corruption" <| fun _ ->
    let bytes = ManifestWriter.write data
    let corrupted = Array.copy bytes
    corrupted.[bytes.Length - 1] <- corrupted.[bytes.Length - 1] ^^^ 0xFFuy
    match ManifestReader.read corrupted with
    | Error msg -> (msg.Contains("CRC")) |> Expect.isTrue "error mentions CRC"
    | Ok _ -> failwith "Should have detected corruption"
\\\

**Test Patterns:**
1. **Roundtrip tests:** Write → Read → Assert equality
2. **Corruption detection:** Flip bits, verify CRC rejects
3. **Format version checks:** Patch version field, re-compute CRC, verify error
4. **Bounds validation:** Validate against file too small, truncated streams
5. **Property-based tests:** Random data generation, 100 iterations

---

## 6. .sagefm Format Details (Daemon Manifest Example)

### 6.1 File Structure
\\\
Offset  Size  Content
─────────────────────────────────────────────────────────
0x00    4     Magic: "SFM1"
0x04    2     Format version: 1
0x06    2     Min reader version: 1
0x08    4     Section count: 1
0x0C    4     Flags: 0
0x10    8     Created at MS
0x18    8     Total file size
0x20    4     Session count
0x24    4     Header CRC (whole-file check with bytes 36-39 zeroed)
0x28    ~24   Active session ID (optional lp-string) + padding
0x40    16    Directory entry: tag=SESS(0x53455353), offset, CRC
0x50    ...   SESS payload: session entries
\\\

### 6.2 Key Design Decisions
1. **CRC covers entire file** — not just header. Detects payload corruption.
2. **Atomic writes** — write to .tmp, then move (atomic on modern filesystems).
3. **Header CRC @ offset 36** — 4 bytes, contains CRC of file with these bytes zeroed.
4. **Active session optional** — represents string option via sentinel (0xFFFFFFFF = None).
5. **7-day pruning** — sessions stopped >7 days ago are filtered during load (in ManifestMapping.toReplayState).

---

## 7. Key Insights for SQLite Migration

### 7.1 What to Persist (Compliance Tests Should Cover)

**Manifest (.sagefm):**
- Session ID, project list, working directory
- Created/stopped timestamps
- Active session reference
- Alive session filtering (7-day cutoff on StoppedAt)

**Session (.sagefs):**
- Eval history: code, result, type signature, duration, timestamp
- Kind (Interaction, Expression, Directive, ScriptLoad)
- Flags (Failed, HasSideEffects, HasOutput)
- References: kind (DllPath, NuGet, IncludePath, LoadedScript), path
- Metadata: SageFsVersion, FSharpVersion, DotNetVersion, ProjectPath, WorkingDirectory, SessionId

**Test Cache (.sagetc):**
- Test ID → coverage bitmap mapping
- Test results: TestId, outcome (Pass/Fail/Skip/Error), duration, message
- Instrumentation map generation (version counter)

### 7.2 What's NOT Persisted
- LastDiagnostics (from SessionReplayState)
- ResetCount, HardResetCount (not in SfsData mapping)
- Most fields in LiveTestState (only coverage + results matter)
- FlakyHistory, FailureNarratives, StatusEntries (runtime only)

### 7.3 Persistence Abstraction for Dual-Backend Support

You'll want to create:

\\\sharp
// IPersistence.fs
type IPersistence<'T> =
  abstract member Save: key: string → data: 'T → Async<Result<string, string>>
  abstract member Load: key: string → Async<Result<'T, string>>
  abstract member Delete: key: string → Async<Result<unit, string>>
  abstract member List: ?pattern: string → Async<string list>

// BinaryPersistence.fs
type BinaryPersistence<'T> (writer: 'T → byte[], reader: byte[] → Result<'T, string>) =
  interface IPersistence<'T> with
    member _.Save key data = ...
    member _.Load key data = ...

// SqlitePersistence.fs
type SqlitePersistence<'T> (dbPath: string, tableName: string) =
  interface IPersistence<'T> with
    member _.Save key data = ...
    member _.Load key data = ...
\\\

---

## 8. Compliance Test Checklist for SQLite Migration

### A. Roundtrip Equivalence
- [ ] Binary → Memory → Binary produces identical bytes
- [ ] SQLite → Memory → SQLite produces identical rows
- [ ] Binary → Memory → SQLite → Memory → Binary produces same final state

### B. Field Preservation
- [ ] All string fields (code, paths, IDs) preserved exactly
- [ ] All numeric fields (counts, durations, timestamps) preserved exactly
- [ ] All optional fields (type signatures, messages, stopped times) preserved
- [ ] All collection fields (projects list, eval history, references) order preserved

### C. Encoding Validation
- [ ] UTF-8 strings with special characters (emoji, Unicode) round-trip
- [ ] Empty strings, very long strings (>1MB) preserved
- [ ] Unix timestamps edge cases (year 1970, 2038, far future) preserved
- [ ] TimeSpan/Duration across DST boundaries

### D. Integrity & Error Handling
- [ ] Corrupt binary files detected (CRC mismatch)
- [ ] Truncated binary files detected (bounds check)
- [ ] Invalid format versions rejected
- [ ] Unknown section tags skipped (forward compatibility)
- [ ] SQLite constraint violations caught

### E. Performance Benchmarks
- [ ] Binary write ≤ 50ms for 1000 interactions
- [ ] Binary read ≤ 30ms for 1000 interactions
- [ ] SQLite write ≤ 100ms for 1000 interactions
- [ ] SQLite read ≤ 60ms for 1000 interactions
- [ ] Migration tool ≤ 5s for typical projects

---

## 9. File Paths Summary

| Component | File Path |
|-----------|-----------|
| Binary primitives | SageFs.Core/BinaryFormat.fs (lines 1-86) |
| Daemon manifest | SageFs.Core/Features/ManifestPersistence.fs (lines 7-311) |
| Session persistence | SageFs.Core/Features/SessionPersistence.fs (lines 1-509) |
| Test cache persistence | SageFs.Core/Features/TestCachePersistence.fs (lines 1-418) |
| Daemon-level coordination | SageFs.Core/Features/DaemonPersistence.fs (lines 1-79) |
| Domain types | SageFs.Core/Features/Replay.fs, LiveTestingTypes.fs |
| Existing tests | SageFs.Tests/ManifestPersistenceTests.fs, BinaryFormatTests.fs |
| Format spec | docs/binary-format-spec.md (41 KB, comprehensive) |

---

## 10. Next Steps for Implementation

1. **Define IPersistence<'T> interface** — generic abstraction for both backends
2. **Create SQL schema** — map binary types to normalized tables
3. **Implement SQLitePersistence** — factory + CRUD operations
4. **Write compliance test suite** — property-based + deterministic tests
5. **Migration tool** — read binary files, write to SQLite, verify equivalence
6. **Dual-backend support** — config flag to choose backend
7. **Deprecation path** — binary format still supported for reads, SQLite for writes

