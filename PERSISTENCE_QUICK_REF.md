# SageFs Persistence Layer — Quick Reference for SQLite Compliance Tests

## 📋 Overview Table

| Aspect | Detail |
|--------|--------|
| **Current Formats** | 3 independent binary formats (.sagefm, .sagefs, .sagetc) |
| **No Abstraction** | Each format has separate read/write — no IPersistence interface |
| **Serialization** | Length-prefixed UTF-8, little-endian, CRC-32 per section |
| **File Locations** | ~/.sagefs/ (daemon.sagefm, sessions/*.sagefs, cache/*.sagetc) |
| **Test Coverage** | ManifestPersistenceTests.fs, BinaryFormatTests.fs (property-based) |
| **Migration Goal** | Dual-backend support (binary + SQLite), with compliance tests |

---

## 🔧 Key Files to Study

\\\
SageFs.Core/
├── BinaryFormat.fs                          ← CRC-32, length-prefixed strings
├── Features/
│   ├── ManifestPersistence.fs (311 lines)   ← .sagefm: daemon sessions
│   ├── SessionPersistence.fs (509 lines)    ← .sagefs: eval history + refs
│   ├── TestCachePersistence.fs (418 lines)  ← .sagetc: test results + coverage
│   ├── DaemonPersistence.fs (79 lines)      ← High-level API (no interface!)
│   ├── Replay.fs (250+ lines)               ← Domain types for persistence
│   └── LiveTestingTypes.fs (1200+ lines)    ← Test state + coverage bitmaps

SageFs.Tests/
├── ManifestPersistenceTests.fs              ← Roundtrip, CRC, fields, version checks
└── BinaryFormatTests.fs                     ← FsCheck property tests (100 iterations)

docs/
└── binary-format-spec.md (41 KB)            ← Complete binary format spec
\\\

---

## 📊 Three Formats at a Glance

### .sagefm (Daemon Manifest) — 64-byte header + 1 section

**What:** Session list (which sessions exist, which is active)
**Where:** ~/.sagefs/daemon.sagefm (singleton)
**Domain Type:** \DaemonReplayState\ = \Map<string, DaemonSessionRecord> + string option\

| Field | Type | Size | Purpose |
|-------|------|------|---------|
| Magic | "SFM1" | 4 | Identifies format |
| Version | 1 | u16 | Format version |
| SessionCount | u32 | 4 | How many sessions in SESS |
| CreatedAtMs | i64 | 8 | Daemon created timestamp |
| ActiveSessionId | option<string> | ~20 | Which session is active |

**SESS Section:**
- Entries: SessionId, Projects (list), WorkingDir, CreatedAt (ms), StoppedAt (ms or -1)

**Size:** Typical: 200-500 bytes

---

### .sagefs (Session Persistence) — 64-byte header + 3-5 sections

**What:** Eval history, code, outputs, references, metadata
**Where:** ~/.sagefs/sessions/{sessionId}.sagefs
**Domain Type:** \SessionReplayState\ → \SfsData\ (binary form)

| Section | Tag | Required | Content |
|---------|-----|----------|---------|
| META | 0x4D45 | ✅ | Metadata: version strings, counts |
| INPT | 0x494E | ✅ | Interactions + deduplicated string pool |
| REFS | 0x5245 | ✅ | Assembly references (DLL paths, NuGet) |
| PROF | 0x0004 | ❌ | Profiling data (optional) |
| BIND | 0x0005 | ❌ | Runtime bindings (optional) |

**INPT Section (Interactions):**
- String pool: deduplicates code + output strings (saves ~60% space)
- TOC: interaction count, stride=48 bytes each
- Entry: codeOffset, outputOffset, timestampMs, kind (Interaction/Expression/Directive/ScriptLoad), flags (Failed|HasOutput|HasSideEffects), durationMicros

**Typical Size:** 50-500 KB per session (with 1000 interactions)

---

### .sagetc (Test Cache) — 64-byte header + 3 sections

**What:** Test results + coverage bitmaps
**Where:** ~/.sagefs/cache/{projectHash}.sagetc (one per project)
**Domain Type:** \LiveTestState\ → \StcData\ (binary form)

| Section | Tag | Content |
|---------|-----|---------|
| IMAP | 0x494D4150 | Instrumentation map: TestId → coverage bitmap |
| TCOV | 0x54434F56 | Test coverage metadata (bits per test) |
| TRES | 0x54524553 | Test results: outcome, duration, message |

**TRES Section (Results):**
- Entry: TestId, outcome (0=Pass, 1=Fail, 2=Skip, 3=Error), durationMs, message (optional)

**Typical Size:** 10-100 KB per project

---

## 🎯 What Gets Persisted

### Daemon Manifest
- ✅ SessionId, projects, working directory
- ✅ CreatedAt, StoppedAt timestamps
- ✅ ActiveSessionId reference
- ✅ 7-day pruning filter (applied on load)

### Session File
- ✅ EvalRecord list: Code, Result, TypeSignature, Duration, Timestamp
- ✅ Kind (Interaction/Expression/Directive/ScriptLoad)
- ✅ Flags (Failed, HasOutput, HasSideEffects)
- ✅ References: kind + path
- ✅ Metadata: version strings, EvalCount, FailedEvalCount

### Test Cache
- ✅ TestId → CoverageBitmap (uint64 array)
- ✅ TestId → TestResult (outcome + duration + message)
- ✅ ImapGeneration (version counter)

### NOT Persisted
- ❌ LastDiagnostics
- ❌ ResetCount, HardResetCount
- ❌ FlakyHistory, FailureNarratives
- ❌ StatusEntries, RunPhases (runtime only)

---

## 🧪 Test Patterns (from ManifestPersistenceTests.fs)

### Pattern 1: Roundtrip
\\\sharp
let bytes = ManifestWriter.write data
let result = ManifestReader.read bytes
Expect.equal "field" loaded.Field data.Field
\\\

### Pattern 2: CRC Corruption Detection
\\\sharp
let corrupted = Array.copy bytes
corrupted.[bytes.Length - 1] <- corrupted.[bytes.Length - 1] ^^^ 0xFFuy
match ManifestReader.read corrupted with
| Error msg -> Expect.stringContains "error mentions CRC" msg "CRC"
\\\

### Pattern 3: Version Checking
\\\sharp
let patched = Array.copy bytes
patched.[4] <- 99uy; patched.[5] <- 0uy  // Patch version field
// Re-compute header CRC so version check runs
let forCrc = Array.copy patched
forCrc.[36..39] <- [|0uy;0uy;0uy;0uy|]
let crc = Crc32.computeAll forCrc
// Verify reader rejects unknown version
\\\

### Pattern 4: Bounds Validation
\\\sharp
match ManifestReader.read [|1uy; 2uy|] with
| Error msg -> Expect.stringContains "mentions small" msg "too small"
\\\

### Pattern 5: Property-Based (from BinaryFormatTests.fs)
\\\sharp
let genStcData = gen {
  let! covCount = Gen.choose(0, 20)
  let! covs = Gen.listOfLength covCount genCoverageEntry
  // ... generate random data
  return { CoverageEntries = covs; ResultEntries = results; ... }
}
\\\

---

## 📐 Binary Format Anatomy

### Universal 64-byte Header
\\\
Offset  Size  Field
──────────────────────────────
0x00    4     Magic ("SFM1", "SFS3", or "STC1")
0x04    2     Format version
0x06    2     Min reader version
0x08    4     Section count
0x0C    4     Flags (feature bits)
0x10    8     CreatedAtMs (Unix ms)
0x18    8     TotalFileSize (u64)
0x20    4     Quick stat (interaction_count or test_count)
0x24    4     HeaderCRC (entire file with bytes 36-39 zeroed)
0x28    36    Remaining fields + padding
\\\

### Section Directory Entry (20 or 16 bytes)
\\\
SFS3: Tag (u16) | Flags (u16) | Offset (u64) | Size (u32) | CRC (u32)
STC1: Tag (u32) | Offset (u64) | CRC (u32)
\\\

### Integrity Model
- **Whole-file CRC** @ offset 0x24 (covers header + directory + all payloads)
- **Per-section CRC** in directory entries (validates each section independently)
- **No per-field CRC** (trust CRC after validation — don't re-check per entry)

---

## 🔄 Domain Types for SQL Schema

### Table: Manifest
\\\sql
CREATE TABLE manifest (
  id INTEGER PRIMARY KEY,
  created_at_ms INTEGER,
  active_session_id TEXT
);

CREATE TABLE sessions (
  session_id TEXT PRIMARY KEY,
  projects TEXT,  -- JSON array or delimited list
  working_dir TEXT,
  created_at_ms INTEGER,
  stopped_at_ms INTEGER  -- NULL if still alive
);
\\\

### Table: SessionEvals
\\\sql
CREATE TABLE evals (
  id INTEGER PRIMARY KEY,
  session_id TEXT,
  code TEXT,
  result TEXT,
  type_signature TEXT,
  duration_micros INTEGER,
  timestamp_ms INTEGER,
  kind INTEGER,  -- 0=Interaction, 1=Expression, 2=Directive, 3=ScriptLoad
  flags INTEGER  -- bitfield: Failed|HasOutput|HasSideEffects
);

CREATE TABLE references (
  id INTEGER PRIMARY KEY,
  session_id TEXT,
  kind INTEGER,  -- 0=DllPath, 1=NuGet, 2=IncludePath, 3=LoadedScript
  path TEXT
);
\\\

### Table: TestCache
\\\sql
CREATE TABLE test_results (
  test_id TEXT PRIMARY KEY,
  outcome INTEGER,  -- 0=Pass, 1=Fail, 2=Skip, 3=Error
  duration_ms INTEGER,
  message TEXT
);

CREATE TABLE coverage_bitmaps (
  test_id TEXT PRIMARY KEY,
  bitmap_words BLOB,  -- uint64 array as binary
  word_count INTEGER
);
\\\

---

## ✅ Compliance Test Suite Outline

\\\sharp
[<Tests>]
let sqliteComplianceTests = testList "SQLite ↔ Binary equivalence" [
  // 1. Roundtrip: Binary → Memory → SQLite → Memory → Binary
  testCase "manifest roundtrips through SQLite" <| fun _ ->
    let original = generateManifest()
    let binary1 = ManifestWriter.write original
    let memory1 = ManifestReader.read binary1 |> Result.get
    let sqliteRows = sqliteStore.Save memory1
    let memory2 = sqliteStore.Load()
    let binary2 = ManifestWriter.write memory2
    Expect.equal "binary identical" binary1 binary2

  // 2. Field preservation
  testCase "all manifest fields preserved" <| fun _ ->
    // Generate random manifest, save/load via SQLite, verify each field
    
  // 3. Encoding edge cases
  testCase "UTF-8 special characters roundtrip" <| fun _ ->
    
  // 4. Timestamp edge cases  
  testCase "Unix timestamps preserved (1970, 2038, far future)" <| fun _ ->
    
  // 5. Integrity checks
  testCase "corrupt SQLite detected" <| fun _ ->
    
  // 6. Migration tool
  testCase "migration: binary files → SQLite" <| fun _ ->
    // Load .sagefs from disk, convert to SQLite, verify equivalence
]
\\\

---

## 🚀 Implementation Roadmap

1. **Define IPersistence<'T>** interface (generic read/write/list/delete)
2. **Map binary types to SQL schema** (3 tables per format)
3. **Implement SqlitePersistence.fs** (factory, CRUD, transactions)
4. **Write compliance tests** (roundtrip, fields, encoding, performance)
5. **Create migration tool** (binary → SQLite converter)
6. **Add config option** (choose backend: Binary vs SQLite)
7. **Performance benchmarks** (compare read/write/startup times)
8. **Deprecation path** (support both, encourage SQLite migration)

---

## 📖 References

- **Binary Format Spec:** docs/binary-format-spec.md (41 KB, comprehensive)
- **Tests:** SageFs.Tests/{ManifestPersistenceTests.fs, BinaryFormatTests.fs}
- **Domain Types:** SageFs.Core/Features/{Replay.fs, LiveTestingTypes.fs}
- **Current API:** SageFs.Core/Features/DaemonPersistence.fs (79 lines)

