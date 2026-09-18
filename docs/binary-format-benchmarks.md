# SageFs Binary Format Benchmarks

Benchmark results comparing the `.sagefs` v3 and `.sagetc` v1 binary formats against JSON (System.Text.Json) serialization.

> **Note:** the `.sagefs` per-session format is no longer written in production — durable session state is now the daemon manifest (`.sagefm` v1). These numbers stand as a benchmark of the binary-vs-JSON codec approach the current formats share, not of an active persistence path.

---

## 1. Methodology

- **Runtime**: .NET 10 Preview, F# Interactive (FSI session via SageFs)
- **Measurement**: `System.Diagnostics.Stopwatch`, averaged over 100 iterations (after 10 warmup)
- **JSON baseline**: `System.Text.Json.JsonSerializer` with default options
- **Binary**: Hand-coded `BinaryWriter`/`BinaryReader` using `MemoryStream`
- **Metrics**: File size (bytes), read time (µs), write time (µs), allocation (KB/op via `GC.GetAllocatedBytesForCurrentThread`)

---

## 2. `.sagefs` v3 — Session Persistence

### 2.1 Scenario

200 interactions, each with:
- ~80-character source code string
- ~120-character output string
- No assembly blobs (text-only session)
- META section with typical version strings and paths
- REFS section with 15 assembly references

### 2.2 Results

| Metric | Binary | JSON | Ratio |
|--------|--------|------|-------|
| **File size** | 29.5 KB | 45.2 KB | **1.53× smaller** |
| **Write time** | 278 µs | 16,015 µs | **57.7× faster** |
| **Read time** | 138 µs | 564 µs | **4.1× faster** |
| **Allocation** | 295 KB/op | 3,394 KB/op | **11.5× less** |

### 2.3 Analysis

- **Write** performance advantage is dramatic (57×) because JSON must escape strings, allocate intermediate `Utf8JsonWriter` buffers, and pretty-print (or minify) the output. Binary writes raw bytes sequentially.
- **Read** speedup of 4.1× is the operationally important number — daemon cold start reads the session file to restore state.
- **Size** reduction of 1.53× is modest because text-only sessions have no assembly blobs. With DLL/PDB caching (typical production usage), binary size advantage grows significantly because JSON would base64-encode blob data (33% overhead).
- **Allocation** reduction of 11.5× reduces GC pressure during startup, especially relevant for the daemon process which may restore multiple sessions.

---

## 3. `.sagetc` v1 — Test Cache

### 3.1 Scenarios

Five scenarios varying test count and coverage bitmap size:

| Scenario | Tests | Bitmap Words | Probes per Test |
|----------|-------|-------------|-----------------|
| Small | 100 | 32 | 1,024 |
| Medium | 500 | 32 | 1,024 |
| Large | 1,000 | 32 | 1,024 |
| Wide | 100 | 1,000 | 32,000 |
| Large+Wide | 1,000 | 1,000 | 32,000 |

Each test entry includes: test ID, outcome (random Pass/Fail/Skip/Error), duration, and an optional message string (50% `None`, 50% random 10–50 char string).

### 3.2 File Size

| Scenario | Binary | JSON | Binary/JSON |
|----------|--------|------|-------------|
| 100 tests, 32 words | 16,908 B | 18,126 B | 0.93× (7% smaller) |
| 500 tests, 32 words | 84,097 B | 91,913 B | 0.91× (9% smaller) |
| 1,000 tests, 32 words | 168,108 B | 184,209 B | 0.91× (9% smaller) |
| 100 tests, 1,000 words | 404,108 B | 363,728 B | 1.11× (11% **larger**) |
| 1,000 tests, 1,000 words | 4,040,108 B | 3,640,266 B | 1.11× (11% **larger**) |

**Size verdict**: Binary is ~9% smaller for typical workloads (≤32 bitmap words). For very large bitmaps (1,000 words = 32K probes per test), JSON is 11% smaller because it represents `0`-valued bitmap words more compactly (one digit vs 4 bytes).

### 3.3 Read Performance

| Scenario | Binary (µs) | JSON (µs) | Speedup |
|----------|-------------|-----------|---------|
| 100 tests, 32 words | 38 | 210 | **5.5×** |
| 500 tests, 32 words | 189 | 972 | **5.1×** |
| 1,000 tests, 32 words | 280 | 2,382 | **8.5×** |
| 100 tests, 1,000 words | 344 | 2,610 | **7.6×** |
| 1,000 tests, 1,000 words | 3,120 | 24,024 | **7.7×** |

**Read verdict**: Binary reads are **5–8.5× faster** across all scenarios. This is the key win — the daemon reads `.sagetc` on startup to restore coverage state. At 1,000 tests, binary read completes in ~0.3ms vs ~2.4ms for JSON.

### 3.4 Write Performance

| Scenario | Binary (µs) | JSON (µs) | Speedup |
|----------|-------------|-----------|---------|
| 100 tests, 32 words | 52 | 94 | **1.8×** |
| 500 tests, 32 words | 680 | 360 | 0.5× (2× slower) |
| 1,000 tests, 32 words | 1,450 | 838 | 0.6× (1.7× slower) |
| 100 tests, 1,000 words | 3,870 | 1,240 | 0.3× (3.1× slower) |
| 1,000 tests, 1,000 words | 38,200 | 12,450 | 0.3× (3.1× slower) |

**Write verdict**: Binary write is faster only at small scale (100 tests, 32 words). At larger scales, `MemoryStream` + `BinaryWriter` allocations dominate. The JSON serializer benefits from pooled buffers in `System.Text.Json`. Write performance can be improved in a future version by:
- Pre-allocating `MemoryStream` with `total_file_size` capacity
- Using `Span<byte>` / `IBufferWriter<byte>` instead of `BinaryWriter`
- ArrayPool for bitmap word serialization

### 3.5 Allocation

| Scenario | Binary (KB) | JSON (KB) | Binary/JSON |
|----------|-------------|-----------|-------------|
| 100 tests, 32 words | 48 | 62 | 0.77× |
| 500 tests, 32 words | 235 | 198 | 1.19× |
| 1,000 tests, 32 words | 468 | 396 | 1.18× |
| 100 tests, 1,000 words | 1,120 | 876 | 1.28× |
| 1,000 tests, 1,000 words | 11,200 | 8,760 | 1.28× |

**Allocation verdict**: Binary allocations are slightly higher at scale due to `MemoryStream` growth pattern. This is an implementation artifact, not a format limitation.

---

## 4. Summary

### 4.1 Key Findings

| Metric | `.sagefs` | `.sagetc` |
|--------|-----------|-----------|
| **Read speed** | 4.1× faster | 5–8.5× faster |
| **Write speed** | 57.7× faster | 0.3–1.8× (varies) |
| **File size** | 1.53× smaller | 0.9–1.1× (depends on bitmap density) |
| **Allocation** | 11.5× less | 0.8–1.3× (varies) |

### 4.2 Recommendation

Both binary formats are justified for production use:

1. **Read performance is the primary win** — both formats serve daemon cold start, where minimizing startup latency directly improves developer experience.
2. **`.sagefs` write performance** is excellent because session persistence writes happen infrequently (on session save) and the format avoids JSON string escaping overhead.
3. **`.sagetc` write performance** needs optimization for large test suites. The current MemoryStream-based writer can be improved with buffer pooling and span-based serialization.
4. **File size** is secondary to read performance for both use cases. Neither format produces unreasonably large files.

### 4.3 Benchmark Methodology Notes

- Benchmarks ran in the SageFs FSI REPL, not via BenchmarkDotNet
- Timing uses `Stopwatch.GetTimestamp()` (high-resolution)
- Allocation measurement uses `GC.GetAllocatedBytesForCurrentThread()` delta
- Each measurement is the median of 100 iterations after 10 warmup runs
- Results are from a single machine and should be validated on target hardware before production deployment
