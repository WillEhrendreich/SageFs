# Performance Baselines

Benchmark baselines recorded with BenchmarkDotNet v0.15.8, InProcess emit toolchain.

**Machine**: 11th Gen Intel Core i7-11800H 2.30GHz, 8 physical / 16 logical cores  
**OS**: Windows 11 (10.0.22631.5472/23H2)  
**Runtime**: .NET 10.0.2 (10.0.225.61305), X64 RyuJIT x86-64-v4  
**Commit**: `8c4ab02` (v0.5.561)  
**Date**: 2026-03-05  

## CellGrid Allocation: Heap vs ArrayPool

| Method | Rows | Cols | Mean | Allocated | Ratio |
|--------|------|------|------|-----------|-------|
| HeapCreate | 60 | 200 | 101.9 μs | 192,211 B | 1.00 |
| **PoolRent** | 60 | 200 | **4.1 μs** | **32 B** | **0.04** |
| HeapCreate | 60 | 400 | 198.3 μs | 384,462 B | 1.00 |
| **PoolRent** | 60 | 400 | **8.7 μs** | **32 B** | **0.04** |
| HeapCreate | 120 | 200 | 193.8 μs | 384,462 B | 1.00 |
| **PoolRent** | 120 | 200 | **8.2 μs** | **32 B** | **0.04** |
| HeapCreate | 120 | 400 | 390.9 μs | 768,786 B | 1.00 |
| **PoolRent** | 120 | 400 | **17.3 μs** | **32 B** | **0.04** |

**Takeaway**: ArrayPool is **25x faster** and allocates **zero** on the managed heap (32B is the CellGrid record wrapper). Full-screen terminal (120×400) saves 769KB per frame.

## ElmLoop MsgLabel (Cached Reflection)

| Method | Mean | Allocated |
|--------|------|-----------|
| CachedLabel (3 DU cases) | 51.6 μs | 21.89 KB |

**Note**: This is 3 DU case lookups. Per-label cost ~17μs. The `msgLabel` cache avoids repeated `FSharpValue.GetUnionFields` reflection, but still allocates for formatting. Consider string interning if this appears in hot paths.

## Binary Manifest Persistence (10 sessions)

| Method | Mean | Allocated |
|--------|------|-----------|
| Write | 4.9 μs | 6.35 KB |
| Read | 5.0 μs | 5.56 KB |
| Roundtrip | 9.7 μs | 11.91 KB |

**Takeaway**: Full manifest roundtrip < 10μs for 10 sessions. Binary format is extremely efficient — read and write are symmetric at ~5μs each. Even with 100 sessions this would be well under 100μs.

## SageFsError.describe (4 DU cases)

| Method | Mean | Allocated |
|--------|------|-----------|
| DescribeAll | 466 ns | 1.08 KB |

**Takeaway**: ~117ns per error description. No concern — this is only called on error paths, not hot loops.

## SSE Event Formatting

| Method | Mean | Allocated |
|--------|------|-----------|
| FormatEvent | 127.7 ns | 376 B |
| FormatRetryHint | 63.7 ns | 160 B |

**Takeaway**: SSE formatting is sub-microsecond. Even at 60fps SSE push rate (16.7ms budget), formatting is <0.01% of frame time.

---

## How to Re-run

```bash
dotnet run -c Release --project SageFs.Tests -- --benchmark --filter "*"
```

Filter specific benchmarks:
```bash
dotnet run -c Release --project SageFs.Tests -- --benchmark --filter "*CellGrid*"
```

Results land in `BenchmarkDotNet.Artifacts/results/`.
