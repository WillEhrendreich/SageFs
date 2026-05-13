/-!
# Formal Specification: BinaryFormat.Crc32

This file contains:
1. A Lean 4 implementation model of the CRC-32 checksum function from
   `SageFs.Core/BinaryFormat.fs` (`SageFs.Crc32` module)
2. Key correctness properties and proved theorems

> 🔬 Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.
> Source: `SageFs.Core/BinaryFormat.fs`, module `SageFs.Crc32`
>
> **Model abstractions**:
> - Data is modelled as `List UInt8` rather than `byte[]` (no length/offset exception paths).
> - The lookup table is modelled as a pure function `UInt8 → UInt32` (computed inline)
>   rather than a mutable `uint32[]` array.
> - `crc32Compute offset length` is modelled via `List.drop`/`List.take` slicing.
> - Out-of-bounds access (`offset + length > data.length`) is NOT modelled — the
>   Lean functions are total but only match the F# semantics for valid inputs.
> - The F# implementation uses `uint32` arithmetic; Lean uses `UInt32` which wraps
>   identically at 2^32.
>
> **Informal spec correction**: the informal spec (`binaryformat_crc32_informal.md`)
> stated CRC-32 of `[0x00u]` = `0x2144DF1Cu`. The correct standard value (verified
> by this Lean model and by reference CRC-32 calculators) is `0xD202EF8Du`. The
> `0x2144DF1Cu` figure appears to have been a copy-paste error in the informal spec.
-/

-- ============================================================
-- CRC-32 polynomial constant
-- ============================================================

/-- The reflected CRC-32 polynomial (ISO 3309, Ethernet, ZIP). -/
private def poly : UInt32 := 0xEDB88320

-- ============================================================
-- Table computation
-- ============================================================

/-- Apply one bit of the CRC-32 polynomial to `crc`. -/
private def crcBit (crc : UInt32) : UInt32 :=
  if (crc &&& 1) == 1 then (crc >>> 1) ^^^ poly else crc >>> 1

/-- Compute the CRC-32 table entry for a single byte value `b`.
    This is equivalent to F#'s inner loop: 8 applications of `crcBit` starting
    from `b.toUInt32`. -/
def crcTableEntry (b : UInt8) : UInt32 :=
  crcBit (crcBit (crcBit (crcBit
    (crcBit (crcBit (crcBit (crcBit b.toUInt32)))))))

-- ============================================================
-- Core CRC-32 step
-- ============================================================

/-- Apply one byte `b` to the running CRC accumulator. -/
def crcStep (crc : UInt32) (b : UInt8) : UInt32 :=
  (crc >>> 8) ^^^ crcTableEntry ((crc &&& 0xFF).toUInt8 ^^^ b)

-- ============================================================
-- Main CRC-32 functions
-- ============================================================

/-- Compute CRC-32 of a byte list, accumulating from `init`. -/
private def crc32Fold (bs : List UInt8) (init : UInt32) : UInt32 :=
  bs.foldl crcStep init

/-- Compute CRC-32 of a byte list (ISO 3309: XOR-init 0xFFFFFFFF, XOR-final 0xFFFFFFFF). -/
def crc32Bytes (bs : List UInt8) : UInt32 :=
  crc32Fold bs 0xFFFFFFFF ^^^ 0xFFFFFFFF

/-- Model of `Crc32.computeAll`: CRC-32 of the entire byte list. -/
def crc32All (data : List UInt8) : UInt32 :=
  crc32Bytes data

/-- Model of `Crc32.compute data offset length`: CRC-32 of a sub-slice.
    Only semantically valid when `offset + length ≤ data.length`. -/
def crc32Compute (data : List UInt8) (offset length : Nat) : UInt32 :=
  crc32Bytes (data.drop offset |>.take length)

-- ============================================================
-- Reference test vectors (concrete values)
-- ============================================================

/-- "123456789" as ASCII bytes — the canonical CRC-32 test vector. -/
private def asciiTestVec : List UInt8 :=
  [49, 50, 51, 52, 53, 54, 55, 56, 57]

-- ============================================================
-- Theorems
-- ============================================================

/-- P1: `computeAll` is equivalent to `compute data 0 data.length`. -/
theorem crc32All_eq_compute (data : List UInt8) :
    crc32All data = crc32Compute data 0 data.length := by
  simp [crc32All, crc32Compute, List.drop_zero, List.take_length]

/-- P2: CRC-32 of the empty list is 0 (XOR-init XOR XOR-final = 0). -/
theorem crc32All_empty : crc32All [] = 0 := by
  native_decide

/-- P2b: `compute [] 0 0` is also 0. -/
theorem crc32Compute_empty : crc32Compute [] 0 0 = 0 := by
  native_decide

/-- CRC-32 of the single byte `0x00` is `0xD202EF8D`.
    Note: the informal spec incorrectly stated `0x2144DF1C`; the correct
    standard value (verified by reference CRC-32 calculators) is `0xD202EF8D`. -/
theorem crc32_single_zero : crc32All [0] = 0xD202EF8D := by
  native_decide

/-- CRC-32 of the single byte `0xFF` is `0xFF000000`.
    Since idx = (0xFF XOR 0xFF) = 0 and table[0] = 0, the accumulator after
    the loop is `0x00FFFFFF`, and the final XOR gives `0xFF000000`. -/
theorem crc32_single_ff : crc32All [0xFF] = 0xFF000000 := by
  native_decide

/-- P: Standard CRC-32 test vector.
    The CRC-32 of the ASCII string "123456789" is universally agreed to be
    `0xCBF43926` for the ISO 3309 polynomial with standard init/final. -/
theorem crc32_test_vector : crc32All asciiTestVec = 0xCBF43926 := by
  native_decide

/-- P5: Determinism — a pure function always returns the same value. -/
theorem crc32_deterministic (data : List UInt8) :
    crc32All data = crc32All data := by
  rfl

/-- Table entry for byte 0 is 0 (the polynomial applied to the zero seed). -/
theorem crcTableEntry_zero : crcTableEntry 0 = 0 := by
  native_decide

/-- Table entry for byte 1 is the well-known value `0x77073096`. -/
theorem crcTableEntry_one : crcTableEntry 1 = 0x77073096 := by
  native_decide

/-- Table entry for byte 0xFF is `0x2D02EF8D`. -/
theorem crcTableEntry_ff : crcTableEntry 0xFF = 0x2D02EF8D := by
  native_decide

/-- P6: Slicing consistency.
    Computing the CRC-32 of a slice `data.drop offset |>.take length` via
    `crc32Compute` is equivalent to `crc32All` on the extracted sub-list.
    The slice result is independent of bytes outside the [offset, offset+length) range
    because `crc32Compute` drops the prefix and takes exactly `length` bytes. -/
theorem crc32_slicing_consistency (pfx data sfx : List UInt8) :
    crc32Compute (pfx ++ data ++ sfx) pfx.length data.length =
    crc32All data := by
  simp only [crc32Compute, crc32All, List.append_assoc]
  rw [List.drop_append_of_le_length (Nat.le_refl _)]
  simp [List.drop_length, List.take_append_of_le_length (Nat.le_refl _), List.take_length]

/-- `crc32Compute` with zero length always returns 0, regardless of offset. -/
theorem crc32Compute_zero_length (data : List UInt8) (offset : Nat) :
    crc32Compute data offset 0 = 0 := by
  simp [crc32Compute, crc32Bytes, crc32Fold, List.take_zero]

/-- The init XOR convention: final result is the folded accumulator XOR 0xFFFFFFFF. -/
theorem crc32Bytes_def (bs : List UInt8) :
    crc32Bytes bs = crc32Fold bs 0xFFFFFFFF ^^^ 0xFFFFFFFF := by
  rfl

/-- The accumulator after folding the empty list is the initial value. -/
theorem crc32Fold_empty (init : UInt32) :
    crc32Fold [] init = init := by
  rfl

/-- CRC-32 accumulator fold over a single byte. -/
theorem crc32Fold_single (b : UInt8) (init : UInt32) :
    crc32Fold [b] init = crcStep init b := by
  rfl

/-- Table entries are always valid UInt32 values — any `UInt32` is within its type range. -/
theorem crcTableEntry_toNat_lt (b : UInt8) :
    (crcTableEntry b).toNat < 2 ^ 32 := by
  exact (crcTableEntry b).toNat_lt
