# SageFs Persistence Layer — Documentation Index

## 📚 Quick Navigation

### For Quick Understanding
👉 **START HERE:** [PERSISTENCE_QUICK_REF.md](./PERSISTENCE_QUICK_REF.md) (10 KB)
- Overview tables
- Three formats at a glance
- SQL schema outlines
- Implementation roadmap

### For Deep Technical Details
📖 **DETAILED ANALYSIS:** [PERSISTENCE_ANALYSIS.md](./PERSISTENCE_ANALYSIS.md) (15 KB)
- Complete binary format specifications
- Domain types (DaemonReplayState, SessionReplayState, LiveTestState)
- Persistence API and current implementation
- Compliance test checklist
- Code examples and patterns

---

## 🎯 Key Questions Answered

### Q1: What is the current binary persistence format?
**Answer:** Three independent formats with shared 64-byte header architecture:
- ✅ .sagefm v1 — Daemon manifest (sessions, active session)
- ✅ .sagefs v3 — Session persistence (eval history, code, references)
- ✅ .sagetc v1 — Test cache (coverage bitmaps, test results)

All use CRC-32 integrity checks, length-prefixed UTF-8 strings, and little-endian encoding.

**File:** SageFs.Core/BinaryFormat.fs (lines 1-86 define primitives)

---

### Q2: What are the persistence operations (read, write, query)? What's the public API?
**Answer:** No abstraction layer yet. Three separate APIs:

`sharp
module DaemonPersistence =
  val saveManifest: string → DaemonReplayState → Result<string, string>
  val loadManifest: string → Result<DaemonReplayState, ManifestLoadError>
  val saveSession: string → sessionId → projectPath → workDir → refs 
                   → SessionReplayState → Result<string, string>
  val loadSession: string → sessionId → Result<SessionReplayState, string>
  val saveTestCache: string → projects → LiveTestState → Result<string, string>
  val loadTestCache: string → projects → Result<LiveTestState, string>
`

**Key missing piece:** No IPersistence<'T> abstraction for dual-backend support.

**Files:**
- SageFs.Core/Features/ManifestPersistence.fs (311 lines)
- SageFs.Core/Features/SessionPersistence.fs (509 lines)
- SageFs.Core/Features/TestCachePersistence.fs (418 lines)
- SageFs.Core/Features/DaemonPersistence.fs (79 lines)

---

### Q3: Are there existing persistence tests? What patterns do they test?
**Answer:** Yes! Two test files with 5 established patterns:

| Pattern | File | Example |
|---------|------|---------|
| **Roundtrip** | ManifestPersistenceTests.fs (line 13) | Write → Read → Assert |
| **CRC corruption** | ManifestPersistenceTests.fs (line 103) | Flip bits, verify CRC rejects |
| **Version checking** | ManifestPersistenceTests.fs (line 174) | Patch version, re-compute CRC |
| **Bounds validation** | ManifestPersistenceTests.fs (line 120) | Verify "too small" error |
| **Property-based** | BinaryFormatTests.fs (line 48) | FsCheck, 100 iterations |

**Files:**
- SageFs.Tests/ManifestPersistenceTests.fs
- SageFs.Tests/BinaryFormatTests.fs

---

### Q4: What is .sagefm format? How is data serialized?
**Answer:** Daemon manifest — list of sessions with active session reference.

**Structure:**
- **Magic:** "SFM1" (0x53, 0x46, 0x4D, 0x31)
- **Header (64 bytes):** Version, flags, timestamps, session count, CRC
- **Section:** SESS (0x53455353)
  - Entries: SessionId, Projects list, WorkingDir, CreatedAt (ms), StoppedAt (ms or -1)
- **Size:** 200-500 bytes typically

**Serialization:**
- Length-prefixed strings: u32 length + UTF-8 bytes
- Fixed-width integers: little-endian
- Optional timestamps: -1 = None, otherwise Unix milliseconds
- Atomic writes: write to .tmp, then move

**Files:**
- SageFs.Core/Features/ManifestPersistence.fs (lines 7-111, writer; 113-230, reader)
- docs/binary-format-spec.md (section 1.3)

---

### Q5: What are the key domain types that get persisted (test results, session state, eval history)?
**Answer:** Three main types from Replay.fs and LiveTestingTypes.fs:

**A. Daemon State**
`sharp
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
`

**B. Session State**
`sharp
type SessionReplayState = {
  EvalHistory: EvalRecord list  // ← Persisted as INPT section
  EvalCount: int
  FailedEvalCount: int
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
`

**C. Test State**
`sharp
type LiveTestState = {
  TestCoverageBitmaps: Map<TestId, CoverageBitmap>  // ← IMAP section
  LastResults: Map<TestId, TestRunResult>            // ← TRES section
}

type TestRunResult = {
  TestId: TestId
  Result: TestResult  // Passed | Failed | Skipped | NotRun
  Timestamp: DateTimeOffset
}
`

**NOT persisted:** LastDiagnostics, ResetCount, FlakyHistory, StatusEntries (runtime only)

**Files:**
- SageFs.Core/Features/Replay.fs (lines 1-250)
- SageFs.Core/Features/LiveTestingTypes.fs (lines 1085-1141)

---

### Q6: Is there already a persistence abstraction/interface that both binary and SQLite could implement?
**Answer:** **NO.** This is a missing piece for the SQLite migration.

**Current state:**
- Each format has separate reader/writer functions
- No common IPersistence<'T> interface
- Binary ↔ Memory mappings exist (ManifestMapping, SessionMapping, TestCacheMapping)
- Mapping functions bridge domain types ↔ binary types

**What you need to add:**
`sharp
type IPersistence<'T> =
  abstract Save: key: string → data: 'T → Async<Result<string, string>>
  abstract Load: key: string → Async<Result<'T, string>>
  abstract Delete: key: string → Async<Result<unit, string>>
  abstract List: ?pattern: string → Async<string list>

// Two implementations:
type BinaryPersistence<'T> (writer, reader) : IPersistence<'T>
type SqlitePersistence<'T> (dbPath, tableName) : IPersistence<'T>
`

**Files:**
- SageFs.Core/Features/DaemonPersistence.fs (79 lines, high-level API without interface)

---

## 📊 Format Specifications Summary

| Aspect | .sagefm | .sagefs | .sagetc |
|--------|---------|---------|---------|
| **Magic** | "SFM1" | "SFS3" | "STC1" |
| **Version** | 1 | 3 | 1 |
| **Header size** | 64 bytes | 64 bytes | 64 bytes |
| **Section count** | 1 | 3-5 | 3 |
| **Directory entry** | 16 bytes (u32 tag, u64 offset, u32 CRC) | 20 bytes (u16 tag, u16 flags, u64 offset, u32 size, u32 CRC) | 16 bytes (u32 tag, u64 offset, u32 CRC) |
| **Key section** | SESS | INPT | TRES, IMAP |
| **Typical size** | 200-500 B | 50-500 KB | 10-100 KB |
| **Spec location** | binary-format-spec.md §1 | binary-format-spec.md §2 | binary-format-spec.md §3 |

---

## 🧪 Compliance Test Coverage

### Current Testing (2 test files, 5 patterns)
- ✅ Roundtrip equivalence (Write → Read → Compare)
- ✅ CRC corruption detection
- ✅ Format version validation
- ✅ Bounds checking (file too small, truncated)
- ✅ Property-based testing (FsCheck, 100 iterations)

### Missing for SQLite Migration
- ☐ Binary ↔ SQLite ↔ Memory roundtrip
- ☐ Field preservation (all types, all fields)
- ☐ Encoding edge cases (UTF-8 special chars, timestamps, timespans)
- ☐ Integrity checks (SQLite constraints, transaction rollback)
- ☐ Performance benchmarks (binary vs SQLite)

---

## 🔧 Implementation Checklist

### Phase 1: Interface & Schema
- [ ] Define IPersistence<'T> abstraction
- [ ] Create SQL schema (3 tables minimum: manifest, sessions, test_results)
- [ ] Implement SqlitePersistence<'T>
- [ ] Implement BinaryPersistence<'T> wrapper

### Phase 2: Compliance Tests
- [ ] Roundtrip tests (Binary ↔ SQLite ↔ Memory)
- [ ] Field preservation tests (all domain types, all fields)
- [ ] UTF-8 encoding tests (special characters, very long strings)
- [ ] Timestamp/timespan tests (edge cases, DST boundaries)
- [ ] Integrity tests (constraints, rollback)

### Phase 3: Migration & Integration
- [ ] Migration tool (bulk convert .sagefs → SQLite)
- [ ] Performance benchmarks (target: SQLite ≤ 2× binary)
- [ ] Config option (choose backend: Binary vs SQLite)
- [ ] Deprecation path (support both, encourage SQLite)

---

## 📖 Additional Resources

### Inside the Repository
- **Format specification:** docs/binary-format-spec.md (41 KB, definitive)
- **Performance benchmarks:** docs/binary-format-benchmarks.md
- **Architecture notes:** IMPROVEMENT_PLAN.md (vision + LARP affordance model)
- **Existing tests:** SageFs.Tests/{ManifestPersistenceTests.fs, BinaryFormatTests.fs}

### Key Source Files (1,700+ lines)
`
SageFs.Core/
├── BinaryFormat.fs (86 lines) — CRC-32, lp-string primitives
├── Features/
│   ├── ManifestPersistence.fs (311 lines) — .sagefm reader/writer
│   ├── SessionPersistence.fs (509 lines) — .sagefs reader/writer
│   ├── TestCachePersistence.fs (418 lines) — .sagetc reader/writer
│   ├── DaemonPersistence.fs (79 lines) — High-level API (no interface)
│   ├── Replay.fs (250+ lines) — Domain types (manifest, session)
│   └── LiveTestingTypes.fs (1,200+ lines) — Domain types (test state)
`

---

## 🚀 Getting Started

1. **Read the quick reference:** PERSISTENCE_QUICK_REF.md (10 min read)
2. **Deep dive into analysis:** PERSISTENCE_ANALYSIS.md (30 min read)
3. **Study the format spec:** docs/binary-format-spec.md (key sections)
4. **Review existing tests:** SageFs.Tests/ManifestPersistenceTests.fs (patterns)
5. **Define IPersistence<'T>** interface
6. **Create SQL schema** based on domain types
7. **Implement compliance tests** using established patterns

---

**Last updated:** Analysis completed
**Scope:** Complete SageFs persistence layer for SQLite migration compliance testing
**Documentation:** 25 KB (2 files), 1,700+ lines of code reviewed

