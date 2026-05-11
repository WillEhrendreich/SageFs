# Informal Specification: `BinaryFormat.Crc32`

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*

**Source file**: `SageFs.Core/BinaryFormat.fs`
**Module**: `SageFs.Crc32`
**Run**: 25685202317 (2026-05-11)

---

## Purpose

`Crc32` computes a CRC-32 checksum over a byte array. It is used to validate the
integrity of `.sagefm` binary manifest files when they are loaded from disk. A CRC
mismatch causes the session to fall back to a cold start, so correctness is
safety-relevant: a bad CRC value could cause a valid manifest to be rejected (spurious
cold-start) or, worse, a corrupt manifest to be accepted.

The implementation uses the standard **ISO 3309 / Ethernet / ZIP** polynomial
`0xEDB88320` (reflected representation of `0x04C11DB7`). This is the same polynomial
used in Ethernet frames, ZIP files, and PNG chunks.

---

## Public API

```fsharp
module Crc32 =
  val compute    : data: byte[] -> offset: int -> length: int -> uint32
  val computeAll : data: byte[] -> uint32
```

`computeAll data` is a convenience alias: `compute data 0 data.Length`.

---

## Preconditions

### `compute data offset length`
- `data` is not null (assumed — .NET non-nullable array)
- `offset ≥ 0`
- `length ≥ 0`
- `offset + length ≤ data.Length` (otherwise `data.[i]` throws `IndexOutOfRangeException`)

### `computeAll data`
- `data` is not null

---

## Postconditions

### `compute data offset length` → `uint32`
Returns the CRC-32 checksum of `data[offset .. offset + length - 1]`.

Internally:
1. Initialise accumulator: `crc := 0xFFFFFFFFu`
2. For each byte `b` in the range `data[offset .. offset+length-1]`:
   - Look up `table[(crc XOR b) AND 0xFF]`
   - Update `crc := (crc >>> 8) XOR table[...]`
3. Return `crc XOR 0xFFFFFFFFu` (final complement)

The initial and final XOR with `0xFFFFFFFFu` is a standard CRC-32 convention.

### `computeAll data` → `uint32`
Equivalent to `compute data 0 data.Length`. Returns CRC-32 of the entire array.

---

## Key Properties

### P1: Consistency (most critical)
```
∀ data, computeAll data = compute data 0 data.Length
```
This is a direct consequence of the implementation — `computeAll` is a one-line alias.
In Lean this will reduce to `rfl` or `simp [computeAll, compute]`.

### P2: Empty input
```
compute [||] 0 0 = 0x00000000u
```
The standard CRC-32 of an empty byte sequence:
- Start with `0xFFFFFFFFu`
- Loop body executes zero times
- Final XOR: `0xFFFFFFFFu XOR 0xFFFFFFFFu = 0x00000000u`

Note: this differs from the "CRC of zero bytes = 0" claim some sources make; the correct
value with the ISO 3309 initialisation/finalisation convention is `0x00000000u`.

### P3: CRC-32 of a known single byte
CRC-32 of `[0x00u]` = `0x2144DF1Cu` (standard reference value for this polynomial).
This serves as a sanity-check / regression test — verifiable by `decide` on the Lean model.

### P4: Non-zero output for known non-empty input
```
∀ data ≠ [], computeAll data ≠ 0xFFFFFFFFu
```
Since the initial state `0xFFFFFFFFu` is XOR'd out at the end, the output `0xFFFFFFFFu`
would require the loop to leave the accumulator equal to `0x00000000u`, which cannot
happen with the first byte of any non-empty input given standard CRC-32 table properties.
(This property is harder to prove in full generality; can be verified for short concrete
sequences by `decide`.)

### P5: Determinism
```
∀ data offset length, compute data offset length = compute data offset length
```
Trivially true (pure function), but worth stating: the same input always produces the
same checksum.

### P6: Offset/length slicing consistency
```
∀ data prefix suffix,
  compute (prefix ++ data ++ suffix) prefix.Length data.Length
  = computeAll data
```
Computing the CRC of a slice `data[offset..offset+length-1]` is equivalent to computing
the CRC of that slice in isolation. This is because the algorithm only accesses bytes in
the specified range.

*Open question*: Is this property even true? CRC-32 with the standard XOR-init/final
convention is NOT additively compositional (you cannot combine CRCs of sub-arrays into
the CRC of the whole). However, **slicing is compositional**: the CRC of a subarray
computed via `compute data offset length` should equal the CRC of that subarray
extracted into its own array. This should hold because the loop initialisation is always
`crc := 0xFFFFFFFFu` regardless of offset.

### P7: Table correctness (structural)
The `table` has exactly 256 entries (one per byte value). Each entry is a `UInt32`.
This is trivially true from the array comprehension `[| for i in 0u..255u do ... |]`.

---

## Invariants

**I1 — Accumulator invariant**: During `compute`, the loop variable `crc` is always a
valid `UInt32` (no arithmetic overflow — all operations are XOR and right-shift, which
preserve the UInt32 range).

**I2 — Table entries are CRC values**: Each `table[i]` equals the CRC-32 of the single
byte `i` under the ISO 3309 polynomial, with XOR initialisation but *without* the final
XOR complement. This is a loop invariant on the table construction.

---

## Edge Cases

| Input | Expected behaviour |
|-------|-------------------|
| Empty array `[||]` | Returns `0x00000000u` |
| `offset = 0, length = 0` | Returns `0x00000000u` (empty sub-range) |
| Single-byte `[0x00u]` | Returns `0x2144DF1Cu` (reference value) |
| Single-byte `[0xFFu]` | Returns `0xFF000000u` (reference value, verify by `decide`) |
| Large array | Accumulates correctly; no overflow |
| `length > data.Length - offset` | Throws `IndexOutOfRangeException` (out-of-spec input; not modelled in Lean) |

---

## Examples

```fsharp
// Empty
Crc32.computeAll [||]                                 // 0x00000000u

// Single byte 0x00
Crc32.computeAll [| 0x00uy |]                         // 0x2144DF1Cu

// Standard test vector: "123456789" in ASCII
Crc32.computeAll "123456789"B                         // 0xCBF43926u

// Consistency: computeAll = compute data 0 len
let data = [| 0x01uy; 0x02uy; 0x03uy |]
Crc32.computeAll data = Crc32.compute data 0 3        // true

// Slice is independent of surrounding data
let full = [| 0x00uy; 0x01uy; 0x02uy; 0x03uy; 0x04uy |]
Crc32.compute full 1 3 = Crc32.computeAll [| 0x01uy; 0x02uy; 0x03uy |]  // true
```

---

## Inferred Intent

The `Crc32` module exists to give binary manifest files a cheap integrity check. The
design intent is clearly to mirror standard CRC-32: the polynomial, the XOR init, and
the final complement all match ISO 3309. This means:

1. The implementation should produce results compatible with other CRC-32
   implementations for the same input
2. The "standard test vector" property (CRC-32 of `"123456789"B = 0xCBF43926u`) can
   serve as a golden reference in the Lean spec

---

## Open Questions

**Q1**: Should the Lean spec model the 256-entry table as a `Fin 256 → UInt32` function
or as a concrete `List UInt32` / `Array UInt32`? Using a function is cleaner for proofs;
using a list is closer to the F# implementation.

**Q2**: The "standard test vector" `0xCBF43926u` for `"123456789"` is a well-known
reference. Should the Lean spec include this as a `#eval`-verified sanity check, or is it
sufficient to verify only the algebraic properties (consistency, empty input)?

**Q3**: The `.sagefm` format uses CRC-32 for integrity checking. Is there a higher-level
property worth verifying — e.g., "if `computeAll payload ≠ expected`, the manifest is
rejected"? This would require modelling the manifest reader, which is outside scope for
now.

---

## Recommended Lean Proof Strategy

1. **Model**: Use `List UInt8` (not `Array`) for the data argument; model the table as a
   pure function `Fin 256 → UInt32` computed by the same algorithm.
2. **Properties P1, P2**: Close immediately by `simp [computeAll, compute]` or `rfl`.
3. **Property P3** (single-byte known value): Use `native_decide` to evaluate the
   concrete value.
4. **Property P6** (slicing): Prove by induction on the prefix length, showing the loop
   only uses bytes in `[offset, offset+length)`.
5. **Table invariant I2**: Prove by `decide` for a small subset; the full 256-entry
   claim may require `native_decide`.

**Tactic budget estimate**: ~60–80 lines of Lean for P1–P6 with model definitions.
