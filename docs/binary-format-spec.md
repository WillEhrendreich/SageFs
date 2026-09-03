# SageFs Binary Format Specification

**Version**: 1.1
**Date**: 2026-03-02
**Formats**: `.sagefs` v3 (session persistence), `.sagetc` v1 (test cache)

---

## 1. Overview

SageFs uses two binary file formats for persistence:

| Format | Extension | Magic | Purpose |
|--------|-----------|-------|---------|
| Session | `.sagefs` | `SFS3` | FSI session state: interactions, outputs, assembly blobs, references, profiling |
| Test cache | `.sagetc` | `STC1` | Coverage bitmaps, test outcomes, instrumentation map hash |

Both formats share a common framing: 64-byte header, section directory, and CRC-validated payloads. Magic bytes and section tags distinguish them. All multi-byte integers are **little-endian**.

### 1.1 Design Principles

Each principle is stated along with why it was chosen and what alternatives were rejected.

1. **The persistence format IS the network format.** One serialization path for disk and wire. Session snapshots travel across machine boundaries over HTTP. If you serialize to JSON on disk and transcode to binary for the wire, you get two serialization paths, two sets of bugs, and twice the testing surface. One format, one code path — disk and wire use identical bytes.

2. **Binary from day one, not deferred.** You design a format once. Migrating from JSON to binary later means maintaining two code paths, a migration tool, backward compatibility shims, and a testing matrix that doubles. Designing binary now costs a week; migrating later costs a month. Benchmarks confirmed the decision: 4–8× faster reads directly improve daemon cold-start time (see `docs/binary-format-benchmarks.md`).

3. **Section-based extensibility.** Unknown section tags are skipped by older readers via the section directory (tag → offset + size → skip). This was chosen over implicit pool ordering, where adding a new pool type reshuffles the layout. The section directory lets you add new section types without changing existing ones.

4. **CRC-32 integrity at two levels.** Header CRC validates structural metadata (the entire file); per-section CRCs validate payloads independently. See §4 for the full integrity rationale including threat model and algorithm choice.

5. **No trailing sentinel.** File size is in the header. No trailer block. This simplifies both the writer (no second pass to write a footer) and the reader (no seeking to the end to find a footer before parsing).

6. **Conservative encoding.** BARE-inspired (IETF draft-devault-bare-14): length-prefixed strings, fixed-width integers, little-endian throughout. No varints — fixed-width fields give predictable offsets and simpler readers at the cost of a few extra bytes. The BARE specification proves a serious binary format can be specified in a few pages and implemented in an afternoon.

7. **Schema out-of-band.** The format doesn't describe itself inline. The reader knows the schema because it's compiled into the reader code. The format version in the header tells the reader WHICH schema to use. This eliminates the overhead of self-describing formats (JSON keys, protobuf field numbers).

8. **Validate early, trust late.** All CRCs are checked when the file is opened. After validation passes, iterate the TOC without per-field checks. The section CRC guarantees structural integrity — if it passes, every field within is sound. Don't re-check CRCs per entry during replay.

9. **Human-inspectable with standard tools.** A well-designed binary format is MORE debuggable than minified JSON because every byte has a defined purpose. Magic bytes jump out in hex dumps. Length-prefixed fields are self-navigating. Section markers are visible signposts. `xxd session.sagefs | head` shows magic, version, section count immediately.

### 1.2 Notation

- `u8`, `u16`, `u32`, `u64`: unsigned integers, little-endian
- `i64`: signed 64-bit integer, little-endian
- `lp-string`: length-prefixed UTF-8 string (see §1.3)
- `utf8[N]`: N bytes of raw UTF-8
- All offsets are byte positions from file start unless stated otherwise

### 1.3 String Encoding

**Length-prefixed string (`lp-string`)**:
```
u32     byte_length     (0 is valid — represents an empty string)
u8[N]   utf8_bytes      (N = byte_length, no null terminator)
```

**Optional string (`lp-string-option`)** — used where `string option` semantics are needed:
```
0xFFFFFFFF              → None (no string)
0x00000000              → Some "" (empty string)
0x0000000N (N > 0)      → Some (N-byte UTF-8 string follows)
```

---

## 2. `.sagefs` v3 — Session Persistence Format

### 2.1 File Layout

```
┌──────────────────────────────────────────────────────┐
│  Header (64 bytes)                                   │
├──────────────────────────────────────────────────────┤
│  Section Directory (section_count × 20 bytes)        │
├──────────────────────────────────────────────────────┤
│  Section: META (metadata strings)              [req] │
├──────────────────────────────────────────────────────┤
│  Section: INPT (interactions + pools)          [req] │
├──────────────────────────────────────────────────────┤
│  Section: REFS (assembly references)           [req] │
├──────────────────────────────────────────────────────┤
│  Section: PROF (profiling data)                [opt] │
├──────────────────────────────────────────────────────┤
│  Section: BIND (runtime bindings)              [opt] │
├──────────────────────────────────────────────────────┤
│  (future sections skipped by older readers)          │
└──────────────────────────────────────────────────────┘
```

### 2.2 Header (64 bytes)

> **Rationale — Header size:** 64 bytes was a compromise between 16 bytes (minimal: magic + version + flags + section_count + total_size) and 96 bytes (generous: per-section CRCs in the header itself). 64 bytes provides enough room for CRCs, version info, and quick-stat fields (`interaction_count`) without waste. The 24 bytes of reserved fields give expansion room for future needs without requiring a format version bump.

```
Offset  Size  Type      Field                 Description
──────  ────  ────      ─────                 ───────────
0x00    4     u8[4]     magic                 "SFS3" (0x53, 0x46, 0x53, 0x33)
0x04    2     u16       format_version        = 3
0x06    2     u16       min_reader_version    = 3
0x08    4     u32       section_count         Number of sections in directory
0x0C    4     u32       flags                 Global flags (§2.3)
0x10    8     i64       created_at_ms         Unix epoch milliseconds
0x18    8     u64       total_file_size       Total file size in bytes
0x20    4     u32       interaction_count     Total interactions
0x24    4     u32       header_crc32          CRC-32 of entire file (§4)
0x28    4     u32       string_dedup_count    Unique strings in dedup table (0 if no dedup)
0x2C    4     u32       reserved_1            Must be 0
0x30    8     u64       reserved_2            Must be 0
0x38    8     u64       reserved_3            Must be 0
```

> **Rationale — `interaction_count` in header:** This lets the UI show "Restoring session (247 interactions)" without parsing the INPT section. One u32 in the header saves a section parse for every UX status message. The INPT section header redundantly stores this value for standalone section parsing.
>
> **Rationale — Magic includes version:** `SFS3` embeds the major version directly in the magic bytes so a hex dump immediately reveals the format version without parsing the version field. This follows the principle that the format should be human-inspectable with standard tools.

### 2.3 Global Flags

Bit field in `flags` (offset 0x0C):

| Bit | Name | Description |
|-----|------|-------------|
| 0 | `has_assembly_cache` | INPT section contains DLL/PDB blobs |
| 1 | `has_profiling` | PROF section present |
| 2 | `has_bindings` | BIND section present |
| 3 | `section_compression` | Some sections may be zstd-compressed |
| 4 | `has_dedup_table` | INPT string pool uses deduplication |
| 5–31 | reserved | Must be 0 |

### 2.4 Section Directory Entry (20 bytes)

> **Rationale — 20 bytes, not 16:** The extra 4 bytes (vs a minimal 16-byte entry) are the per-section CRC. This eliminates reading section payloads just to validate integrity. You read 64 + 20N bytes (header + directory), validate header CRC, and know exactly which sections are present, where they are, and their CRCs — before touching any payload data. For a 5-section file, the directory is 100 bytes; header + directory total is 164 bytes — less than two cache lines. The per-section CRC costs zero in practice.

Located immediately after header at offset `0x40`. Total size: `section_count × 20` bytes.

```
Offset  Size  Type      Field       Description
──────  ────  ────      ─────       ───────────
+0x00   2     u16       tag         Section type identifier (§2.5)
+0x02   2     u16       flags       Per-section flags
+0x04   8     u64       offset      Byte offset from file start to payload
+0x0C   4     u32       size        Payload size in bytes
+0x10   4     u32       crc32       CRC-32 of payload bytes
```

> **Rationale — u64 offset:** Session files with assembly caches can exceed 4GB, so u32 offsets are insufficient. Using u64 for the offset field accommodates arbitrarily large files while costing only 4 extra bytes per directory entry.

**Per-section flags**:

| Bit | Name | Description |
|-----|------|-------------|
| 0 | `is_compressed` | Payload is zstd-compressed; `size` = compressed size |
| 1–15 | reserved | Must be 0 |

When `is_compressed` is set, decompress before CRC validation. The CRC always covers the decompressed payload.

> **Rationale — Per-section compression:** Compression is opt-in at the section level rather than file-level because different sections have different compressibility profiles. META and REFS are tiny — compressing them adds overhead for negligible savings. INPT with blob pools benefits significantly. Section-level granularity lets the writer make the right choice per-section.

### 2.5 Section Tags

| Tag | Hex | Name | Required | Description |
|-----|-----|------|----------|-------------|
| 1 | 0x0001 | META | Yes | Metadata (versions, paths, session ID) |
| 2 | 0x0002 | INPT | Yes | Interactions (TOC + string pool + blob pool) |
| 3 | 0x0003 | REFS | Yes | Assembly/NuGet references |
| 4 | 0x0004 | PROF | No | Per-interaction profiling/timing |
| 5 | 0x0005 | BIND | No | Runtime binding snapshot |
| 6+ | — | — | No | Reserved for future use |

Readers MUST skip unknown section tags by following the directory entry's `offset` and `size`. This is the primary forward-compatibility mechanism: new sections can be added in future format versions without breaking old readers.

> **Rationale — PROF as separate section:** Profiling data is optional, cold, and write-once. It's never read during normal restore — only when diagnosing performance. Keeping it separate means readers that don't care about profiling skip it entirely (section directory → tag 0x0004 → skip). It doesn't pollute the hot TOC entries with 28 extra bytes per interaction. But it's invaluable when needed: "interaction #47 spent 180ms in typecheck and 2ms in everything else" turns the snapshot from a backup file into a diagnostic artifact.
>
> **Rationale — BIND as separate section:** Runtime binding state (variable names, types, values) is useful for session restoration UX but not required for replay correctness. Making it optional avoids bloating files when bindings aren't needed.
>
> **Note on skip-by-omission:** The current reader looks for specific required tags (META, INPT, REFS) and silently ignores others — which is correct forward-compatible skipping by omission. Future readers MUST NOT add checks that error on unknown section tags, as that would break forward compatibility.

### 2.6 META Section (tag 0x0001)

Flat sequence of fields. No internal header.

```
Field                  Type            Description
─────                  ────            ───────────
sagefs_version         lp-string       e.g. "3.0.0"
fsharp_version         lp-string       e.g. "12.9.100.0"
dotnet_version         lp-string       e.g. "10.0.0"
project_path           lp-string       Absolute path to .fsproj
working_directory      lp-string       Working directory at save time
eval_count             u32             Total successful evaluations
failed_eval_count      u32             Total failed evaluations
session_id             lp-string       UUID of the session
```

> **Rationale — Flat fields, known extensibility gap:** META uses a flat field sequence in fixed order with no length prefix or field tags. This is the simplest possible encoding — and intentionally so, since META fields are read once at restore time and never iterated. However, this means adding a field requires a version bump (increment `min_reader_version`), because old readers don't know how many bytes to skip. Every other section in the format has extensibility: INPT has `toc_entry_stride`, top-level has section tags, PROF/REFS/BIND use count-prefixed arrays. META is the exception. A future v4 could add `field_count:u32` or a mini TLV scheme, but for now the simplicity tradeoff is acceptable given how rarely META's field set changes.

### 2.7 INPT Section (tag 0x0002)

> **Rationale — Pool architecture inside section envelope:** This section merges two design philosophies: a section directory (for top-level forward compatibility and independent CRC validation) with a pool architecture (for cache-friendly access patterns). The section directory tells you WHERE the inputs section is. The internal pool layout tells you HOW to read it efficiently. Fixed-stride TOC entries enable O(1) random access to any interaction via `base + i * stride`. Contiguous string and blob pools eliminate per-entry allocation during iteration.

**Section header (16 bytes)**:

```
Offset  Size  Type      Field                 Description
──────  ────  ────      ─────                 ───────────
+0x00   4     u32       interaction_count     Redundant with file header (for standalone parsing)
+0x04   4     u32       toc_entry_stride      Bytes per TOC entry (currently 48)
+0x08   4     u32       string_pool_size      String pool size in bytes
+0x0C   4     u32       blob_pool_size        Blob pool size in bytes
```

**TOC entries** (`interaction_count × toc_entry_stride` bytes, immediately after section header):

```
Offset  Size  Type      Field                 Description
──────  ────  ────      ─────                 ───────────
+0x00   4     u32       code_string_offset    Offset into string pool
+0x04   4     u32       code_string_length    Byte length in string pool
+0x08   4     u32       output_string_offset  Offset into string pool
+0x0C   4     u32       output_string_length  0 if no output
+0x10   4     u32       asm_blob_offset       Offset into blob pool
+0x14   4     u32       asm_blob_length       0 if no assembly
+0x18   4     u32       pdb_blob_offset       Offset into blob pool
+0x1C   4     u32       pdb_blob_length       0 if no PDB
+0x20   8     i64       timestamp_ms          Unix epoch milliseconds
+0x28   2     u16       kind                  Interaction kind (§2.7.1)
+0x2A   2     u16       entry_flags           Per-entry flags (§2.7.2)
+0x2C   4     u32       duration_micros       Evaluation duration in microseconds
```

Current stride is 48 bytes. A v4 writer may extend entries to 64 bytes by setting `toc_entry_stride = 64`; v3 readers advance by their known stride (48) and skip trailing bytes.

> **Rationale — Fixed stride with extensible stride field:** When iterating 5,000 interactions, the reader computes `base + i * stride` and reads raw fields — no deserialization, no allocation. The stride field means future format versions can extend entries (appending new fields at the end) without breaking old readers, who advance by their known stride and ignore trailing bytes. This is the same trick as BARE's conservative extensibility.
>
> **Rationale — Output strings in TOC:** The original design omitted FSI output text. But for session replay UX, seeing what each interaction PRODUCED is critical — the REPL user expects `val x : int = 42` to appear after restoration. Storing output means we don't need to re-capture stdout during replay; we display the cached output directly. This stores the result, not just the input.

#### 2.7.1 Interaction Kind

| Value | Name | Example |
|-------|------|---------|
| 0 | `interaction` | `let x = 42;;` |
| 1 | `expression` | `x + 1;;` |
| 2 | `directive` | `#r "nuget: FsCheck"` |
| 3 | `script_load` | `#load "foo.fsx"` |

#### 2.7.2 Entry Flags

| Bit | Name | Description |
|-----|------|-------------|
| 0 | `failed` | Evaluation produced an error |
| 1 | `has_side_effects` | Printed to stdout or mutated state |
| 2 | `has_output` | `output_string_length > 0` |
| 3–15 | reserved | Must be 0 |

**String pool**: Contiguous UTF-8 bytes immediately after all TOC entries. Strings are referenced by `(offset, length)` pairs from TOC entries. No delimiters or null terminators.

**Blob pool**: Raw assembly DLL and PDB bytes immediately after string pool. Referenced by `(offset, length)` pairs.

#### 2.7.3 String Deduplication (Optional)

> **Rationale — Semantic compression before byte compression:** A typical REPL session has 200 interactions. Type signatures repeat heavily: "int" appears ~100 times, "string" ~50 times. Without dedup, the string pool stores 150 copies of short strings. With dedup, each is stored once. For network transfer, this is the difference between 50KB and 12KB of string data — before compression. Zstd catches redundancy anyway, but semantic dedup means the compressor starts from a better baseline. This is compression-oriented design: reduce redundancy at the structural level first, then let byte compression handle the rest.

When `has_dedup_table` flag is set in the file header, repeated strings in the string pool are stored once. Multiple TOC entries reference the same `(offset, length)` pair.

A dedup metadata table is appended after the raw string pool bytes:

```
Field                  Type            Description
─────                  ────            ───────────
magic                  u32             0x44445550 ("DDUP")
unique_count           u32             Number of unique strings
[repeated unique_count times:]
  offset               u32             Offset into string pool
  length               u32             String length in bytes
  ref_count            u32             Number of TOC entries referencing this string
```

### 2.8 REFS Section (tag 0x0003)

```
Field                  Type            Description
─────                  ────            ───────────
ref_count              u32             Number of references
[repeated ref_count times:]
  ref_kind             u8              Reference kind (§2.8.1)
  path                 lp-string       Path or package identifier
```

> **Rationale — Typed references with discriminator byte:** Session restore needs to know which DLLs, NuGet packages, scripts, and include paths were loaded so it can `#r` them in the correct order. A single flat list with a `ref_kind` discriminator byte (rather than separate sections per kind) keeps the encoding simple while allowing the reader to process them in original load order. Order matters because later references may depend on assemblies loaded by earlier ones.

#### 2.8.1 Reference Kind

| Value | Name | Example |
|-------|------|---------|
| 0 | `dll_path` | `C:\path\to\assembly.dll` |
| 1 | `nuget` | `FSharp.Compiler.Service,41.0.9` |
| 2 | `include_path` | `C:\path\to\include` |
| 3 | `loaded_script` | `C:\path\to\script.fsx` |

### 2.9 PROF Section (tag 0x0004)

> **Rationale — Per-phase timing baked into the format:** Understanding WHY a session restores slowly requires knowing which compiler phase is the bottleneck. Without per-phase timing, all you know is "180ms total." With PROF data, you know interaction #47 spent 180ms in the type-checker's constraint solver and 2ms in everything else. The PROF section turns the snapshot from a backup file into a diagnostic artifact. It lives in a separate section because it's cold data — only read when diagnosing performance, never during normal restore.

```
Field                  Type            Description
─────                  ────            ───────────
entry_count            u32             Number of profiled interactions
[repeated entry_count times:]
  interaction_index    u32             Index into INPT TOC
  parse_micros         u32             Parse phase duration (µs)
  typecheck_micros     u32             Type-check phase duration (µs)
  codegen_micros       u32             Code generation duration (µs)
  emit_micros          u32             IL emit duration (µs)
  load_micros          u32             Assembly load duration (µs)
  total_micros         u32             Wall-clock total (µs)
```

Per-entry size: 28 bytes.

### 2.10 BIND Section (tag 0x0005)

```
Field                  Type            Description
─────                  ────            ───────────
binding_count          u32             Number of bound values
[repeated binding_count times:]
  name                 lp-string       Binding name (e.g. "x")
  type_sig             lp-string       Type signature (e.g. "int")
  value_kind           u8              Value encoding (§2.10.1)
  [if value_kind ∉ {0, 7}:]
    value_length       u32             Byte length of value
    value_bytes        u8[N]           Raw value (little-endian for numeric)
```

> **Rationale — Optional section, tagged value encoding:** BIND captures FSI `let` bindings (name + type + value) for display in the TUI/GUI value inspector. It is optional (tag BIND may be absent from the section directory) because: (1) not all sessions have inspectable bindings, (2) binding values can be large, and (3) the feature is informational, not required for session restore. The `value_kind` discriminator with explicit `null` and `unsupported` sentinels avoids forcing serialization of complex F# types — the format stores what it can represent natively (primitives, strings, byte arrays) and marks everything else as `unsupported`, letting the UI show the type signature without the value.

#### 2.10.1 Value Kind

| Value | Name | Encoding |
|-------|------|----------|
| 0 | `null` | No value bytes follow |
| 1 | `i32` | 4 bytes, little-endian |
| 2 | `i64` | 8 bytes, little-endian |
| 3 | `f64` | 8 bytes, IEEE 754 |
| 4 | `bool` | 1 byte (0 = false, 1 = true) |
| 5 | `string` | UTF-8 bytes |
| 6 | `bytes` | Raw bytes |
| 7 | `unsupported` | No value bytes follow |

---

## 3. `.sagetc` v1 — Test Cache Format

> **Rationale — Reuse SFS framing, different magic/tags:** The test cache format reuses the same 64-byte header layout, section directory, CRC scheme, and lp-string encoding as `.sagefs`. This avoids a second binary format implementation and its associated testing burden. The formats differ only in magic bytes (`STC1` vs `SFS3`), section tags, and payload schemas. A single `BinaryFormat` module handles low-level I/O for both.

### 3.1 File Layout

```
┌──────────────────────────────────────────────────────┐
│  Header (64 bytes)                                   │
├──────────────────────────────────────────────────────┤
│  Section Directory (3 × 16 bytes = 48 bytes)         │
├──────────────────────────────────────────────────────┤
│  Section: IMAP (instrumentation map identity)        │
├──────────────────────────────────────────────────────┤
│  Section: TCOV (test coverage bitmaps)               │
├──────────────────────────────────────────────────────┤
│  Section: TRES (test results)                        │
└──────────────────────────────────────────────────────┘
```

### 3.2 Header (64 bytes)

```
Offset  Size  Type      Field                 Description
──────  ────  ────      ─────                 ───────────
0x00    4     u8[4]     magic                 "STC1" (0x53, 0x54, 0x43, 0x31)
0x04    2     u16       format_version        = 1
0x06    2     u16       min_reader_version    = 1
0x08    4     u32       section_count         = 3
0x0C    4     u32       flags                 Reserved, must be 0
0x10    8     i64       created_at_ms         Unix epoch milliseconds
0x18    8     u64       total_file_size       Total file size in bytes
0x20    4     u32       test_count            Total test entries (coverage + results)
0x24    4     u32       header_crc32          CRC-32 of entire file (§4)
0x28    4     u32       imap_generation       Instrumentation map generation counter
0x2C    20    u8[20]    reserved              Must be zero
```

### 3.3 Section Directory Entry (16 bytes)

Located at offset `0x40`. Fixed count of 3 entries = 48 bytes.

```
Offset  Size  Type      Field       Description
──────  ────  ────      ─────       ───────────
+0x00   4     u32       tag         Section identifier (ASCII, little-endian)
+0x04   8     u64       offset      Byte offset from file start to payload
+0x0C   4     u32       crc32       CRC-32 of payload bytes
```

> **Rationale — STC directory has no size field (known weakness):** STC v1 directory entries are 16 bytes (4 tag + 8 offset + 4 CRC) compared to SFS's 20 bytes (which add an explicit `size:u32`). In STC, section size is computed from the gap between consecutive offsets, with the last section's size computed as `total_file_size - last_offset`. If `total_file_size` in the header is corrupted, the last section's computed size is wrong and may cause out-of-bounds reads. The SFS format's explicit size field is strictly better — it allows validating `offset + size <= total_file_size` per section before any read. This is a known design weakness that is not fixable without a breaking change (STC v2). The CRC check on the full file mitigates the risk: a corrupted `total_file_size` will fail the header CRC, so the file is rejected before the size computation ever runs.

**Section identifiers** (as u32 little-endian):

| Tag | ASCII | Hex | Description |
|-----|-------|-----|-------------|
| IMAP | "IMAP" | 0x50414D49 | Instrumentation map identity |
| TCOV | "TCOV" | 0x564F4354 | Test coverage bitmaps |
| TRES | "TRES" | 0x53455254 | Test results |

### 3.4 IMAP Section — Instrumentation Map Identity

> **Rationale — Coverage bitmaps as the persistence unit:** The instrumentation map associates each test with a bitmap of probe hits. When the underlying source code changes, the probe layout changes (probes are invalidated), so the `imap_generation` counter in the header tracks whether the cached bitmaps are still valid. If the generation counter in the cache doesn't match the live instrumentation map, the entire cache is discarded — incremental invalidation at the individual probe level would require tracking source-to-probe mappings, which is a different (much more complex) feature. The bitmap encoding (packed u32 words) is chosen for direct `BitArray` interop in .NET and cache-friendly sequential access during coverage aggregation.

```
Field                  Type            Description
─────                  ────            ───────────
entry_count            u32             Number of coverage entries
[repeated entry_count times:]
  test_id              u32             Test identifier
  bitmap_word_count    u32             Number of u32 bitmap words
  bitmap_words         u32[N]          Coverage bitmap (N = bitmap_word_count)
```

Each bit in a bitmap word represents one instrumentation probe. Bit 0 of word 0 is probe 0, bit 31 of word 0 is probe 31, bit 0 of word 1 is probe 32, etc.

### 3.5 TCOV Section — Coverage Summary

```
Field                  Type            Description
─────                  ────            ───────────
entry_count            u32             Number of entries
[repeated entry_count times:]
  test_id              u32             Test identifier
  total_probe_bits     u32             Total number of probes (= bitmap_word_count × 32)
```

### 3.6 TRES Section — Test Results

> **Rationale — Separate section from coverage bitmaps:** Test results (pass/fail/skip/error + duration + message) are stored separately from coverage bitmaps because they have different access patterns and lifetimes. The TUI's live test panel reads TRES on startup to show previous results immediately, without needing to load or interpret the (much larger) coverage bitmaps. The coverage bitmaps (IMAP) are loaded later, only when the coverage gutter feature is active.

```
Field                  Type            Description
─────                  ────            ───────────
entry_count            u32             Number of result entries
[repeated entry_count times:]
  test_id              u32             Test identifier
  outcome              u8              Test outcome (§3.6.1)
  duration_ms          u32             Test duration in milliseconds
  message              lp-string-option  Result message (§1.3)
```

#### 3.6.1 Test Outcome

| Value | Name |
|-------|------|
| 0 | Pass |
| 1 | Fail |
| 2 | Skip |
| 3 | Error |
| 4 | NotRun |
| 5 | AssertionFailed (v2) |
| 6 | ExceptionThrown (v2) |
| 7 | TimedOut (v2) |

Values 0-4 are the STC v1 codes. Values 5-7 were added in format v2 so the
test-cache codec is lossless across restarts: v1 collapsed every failure kind
into `Fail` (1), and readers decoded byte 1 as an assertion failure. The v2
writer emits byte 5/6/7 for `AssertionFailed`/`ExceptionThrown`/`TimedOut`
respectively and sets `format_version = 2` while leaving
`min_reader_version = 1` — a v1 reader can still parse a v2 file (it treats
bytes 5-7 as assertion failures, which is strictly no worse than what its own
writer stored). The v2 reader continues to decode legacy byte 1 as
`AssertionFailed`, exactly matching what v1 writers produced. A `TimedOut`
entry's timeout span is carried in the message string ("Timed out after
`<N>s`") so the kind and duration both round-trip.

---

## 4. CRC-32 Integrity

### 4.0 Threat Model & Algorithm Choice

CRC-32 serves as a **data integrity check**, not a security mechanism. The threat model is:

- **Truncated writes:** If SageFs is killed mid-write, the OS file system journal protects FS metadata (directory entries, allocation tables) but NOT application data. A half-written `.sagefs` file will have valid sectors and pass OS-level checks, but be semantically incomplete. CRC catches this.
- **Cross-system transfer:** Files copied between machines via USB, network share, cloud sync, or HTTP transfer can be corrupted in transit by faulty hardware, interrupted connections, or encoding errors.
- **Bit rot:** While rare on modern SSDs, magnetic drives can experience silent data corruption where sectors read successfully but contain wrong data. The OS and file system do not detect this — the sectors pass hardware checks, but the payload is corrupted.
- **Truncated/partial cache loads:** The `.sagetc` test cache is loaded eagerly on daemon startup for <100ms cold-start UX. A fast integrity check prevents parsing garbage bytes from an incomplete previous write.

CRC-32 is **explicitly NOT a security measure.** It provides zero protection against intentional tampering — CRC-32 collisions can be forged trivially (in milliseconds). If tamper-proofing is ever needed (e.g., signed session exports for cross-organization sharing), the appropriate tools are HMAC-SHA256 or Ed25519 signatures. Flag bits 5–31 in the header are reserved, and a future `is_signed` flag + `SIGN` section tag are logical extensions if that need arises. But that is a different feature from integrity checking.

**Why CRC-32, not something else:**

| Alternative | Why not |
|-------------|---------|
| No checksum (trust the OS) | OS file system journals protect FS metadata, not application payloads. Truncated writes, bit rot, and transfer corruption are real threats that the OS does not catch. |
| SHA-256 | Cryptographic hashes are 10–100× slower than CRC-32 and provide security properties we don't need. Our threat model is accidental corruption, not adversarial tampering. SHA-256 would add measurable overhead to the cold-start read path for zero benefit. |
| xxHash / xxh3 | Faster than CRC-32 on large payloads, but adds a dependency. CRC-32 is available in `System.IO.Hashing` (standard library) and has decades of proven correctness in PNG, ZIP, Ethernet, and SCTP. The performance difference is negligible for our file sizes (typically <1MB). |
| Adler-32 | Faster than CRC-32 but has weaker error detection properties. CRC-32 is guaranteed to detect all single-bit, double-bit, and burst errors up to 32 bits. Adler-32 has no such guarantees. |

**Precedent:** PNG uses CRC-32 per chunk. ZIP uses CRC-32 per file. GIF uses CRC. Ethernet frames use CRC-32. SQLite uses page checksums. These are all integrity checks, not security measures, and CRC-32 is the industry-standard choice for this purpose.

Both formats use CRC-32 (ISO 3309 / ITU-T V.42, polynomial `0xEDB88320` reflected).

### 4.1 Header CRC

The header CRC covers the **entire file** (header + TOC + all section payloads), not just the 64-byte header. This ensures the TOC directory entries are also integrity-protected.

1. Copy the complete file bytes to a buffer
2. Zero bytes at offsets `0x24–0x27` (the CRC field itself)
3. Compute `CRC32(buffer[0..file_size-1])`
4. Store result at offset `0x24`

Readers validate by copying the entire file, zeroing `[0x24..0x27]`, computing CRC, and comparing to the stored value.

> **Rationale — Full-file CRC:** An earlier design computed CRC over only the 64-byte header, leaving the TOC directory entries (section tags, offsets, per-section CRCs) at bytes 64–111+ unprotected. A bit flip in the TOC region (e.g., corrupting a section offset) could silently cause the reader to parse the wrong bytes as a section payload. The section CRC would then fail, but the error message would be misleading (reporting a payload CRC mismatch when the real problem is a corrupted offset). The full-file approach matches PNG and ZIP: one CRC covers the entire structure, so any single-bit flip anywhere in the file is detected at the first validation step. This creates a fail-fast funnel: header CRC fails → entire file rejected before any parsing.

### 4.2 Section CRC

Each section directory entry contains a `crc32` field covering the section's payload bytes:

```
CRC32(file[offset .. offset + size - 1])
```

For `.sagefs` with compression: decompress first, then CRC the decompressed bytes.

> **Rationale — Two-level CRC:** The header CRC alone would suffice for detecting any corruption. Section CRCs add a second benefit: **per-section error localization.** When the header CRC fails, you know the file is corrupted but not where. Section CRCs let a diagnostic tool (`sagefs inspect --validate`) report which specific section is damaged, enabling partial recovery of undamaged sections in future tooling. The two-level approach also enables progressive validation: validate the header CRC (cheap, covers structure), then validate only the sections you actually need to parse.

### 4.3 Validation Order

1. Read 64-byte header
2. Validate header CRC (reject the entire file if this fails)
3. Read section directory
4. For each section, validate its CRC before parsing payload

> **Rationale — Validate early, trust late:** All integrity checks happen at file-open time. After validation passes, the reader iterates TOC entries and reads pool data without per-field re-validation. The section CRC guarantee means: if the CRC passed, every byte within the section is exactly what the writer wrote. This avoids defensive per-field bounds checking in the hot parsing path while maintaining full integrity confidence.

---

## 5. Versioning

### 5.1 Version Fields

- `format_version`: The version of the format that wrote this file
- `min_reader_version`: The minimum reader version required to parse this file

A reader with version `R` can read a file if `R >= min_reader_version`.

> **Rationale — Two version fields:** `format_version` tells tooling which writer produced the file (useful for diagnostics and statistics). `min_reader_version` is the operational field — it gates whether a reader can parse the file at all. A v4 writer that only uses v3-compatible features can set `min_reader_version = 3`, allowing v3 readers to open the file. This decouples "what wrote the file" from "what can read it."

### 5.2 Forward Compatibility

**`.sagefs`**: The INPT section's `toc_entry_stride` field enables forward compatibility. A v3 reader encountering a v4 file with 64-byte TOC entries (stride = 64) reads its known 48 bytes per entry and skips the remaining 16.

**Both formats**: Unknown section tags are skipped by following the directory entry's offset and size (`.sagefs`) or offset and next-section offset (`.sagetc`).

> **Rationale — Conservative extensibility over negotiation:** New sections get new tags. New TOC fields append to the end. Old readers skip what they don't understand. There is no version negotiation, no feature capability exchange, no conditional parsing beyond the `min_reader_version` gate. This follows the BARE encoding philosophy: extend by appending, never by redefining.

### 5.3 Format Differences Summary

| Aspect               | `.sagefs` v3                             | `.sagetc` v1                |
| -------------------- | ---------------------------------------- | --------------------------- |
| Magic                | `SFS3` (0x33534653)                      | `STC1` (0x31435453)         |
| Header size          | 64 bytes                                 | 64 bytes                    |
| Directory entry size | 20 bytes (tag, flags, offset, size, crc) | 16 bytes (tag, offset, crc) |
| Section count        | Variable (3–5+)                          | Fixed: 3                    |
| Required sections    | META, INPT, REFS                         | IMAP, TCOV, TRES            |
| Optional sections    | PROF, BIND                               | None                        |
| Compression support  | Yes (per-section zstd)                   | No                          |
| String deduplication | Optional (DDUP)                          | N/A                         |
| Stride extensibility | Yes (toc_entry_stride)                   | N/A                         |

### 5.4 Reader Behavior (MUST)

Readers **MUST** enforce the following after CRC validation:

1. **Version gate**: If `min_reader_version > READER_VERSION`, return an error with both version numbers. Current reader versions: SFS = 3, STC = 1.
2. **Enum validation**: Unknown enum byte values (e.g., `Outcome`, `InteractionKind`, `RefKind`) **MUST** produce an error, not a silent default. After the version check passes, unknown enum values indicate corruption, not forward-compatibility.
3. **CRC mismatch**: If header CRC or any section CRC fails, return an error with expected and actual values.

> **Rationale — Enum errors over defaults:** An earlier design mapped unknown enum values to defaults (e.g., unknown outcome → `Fail`). This makes illegal states representable by mapping them to legal states — exactly backwards. Once the `min_reader_version` gate passes, the reader knows it understands all enum variants the writer could have produced. Unknown values at that point indicate file corruption, not forward-compatibility, and must be reported as errors. The version gate is the correct forward-compatibility mechanism; enum defaults are the wrong one.

### 5.5 Atomic Writes

Writers use write-to-tmp-then-rename to avoid partial writes:
1. Write bytes to `<path>.tmp`
2. `File.Move(<path>.tmp, <path>, overwrite=true)`

On startup, readers **SHOULD** delete orphaned `.sagefs.tmp` and `.sagetc.tmp` files in their respective directories. These indicate interrupted writes and contain incomplete data.

> **Rationale — Write-to-tmp-then-rename:** If the process crashes between `File.WriteAllBytes` and completion, the primary file is left untouched — the reader loads the old (complete) file on next startup, which is correct behavior. `File.Move` with `overwrite: true` is atomic at the filesystem level on NTFS, ext4, and APFS. This means concurrent writes from multiple SageFs instances (e.g., racing on the same cache file) result in last-writer-wins, which is acceptable for a cache. The `.tmp` cleanup policy exists because crash-between-write-and-move leaves orphaned temp files that may confuse users debugging stale caches.
