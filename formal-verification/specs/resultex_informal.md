# Informal Specification: ResultEx

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*
> Source: `SageFs.Core/ResultEx.fs`

## Last Updated
- **Date**: 2026-04-25
- **Commit**: (current branch)

---

## Purpose

`ResultEx` is a railway-oriented programming (ROP) module that provides combinators
for composing `Result<'T, 'E>` computations. It extends the standard F# `Result`
module with operations that make error handling explicit and composable.

The module's operations fall into four groups:
1. **Transformers** — map success/error values without changing the structure
2. **Sequencers** — chain computations that may fail
3. **Combinators** — combine multiple results
4. **Collectors** — process lists of results

---

## Type context

All functions are generic over `'T`, `'U`, `'E`. The SageFs-specific `describe`
function specialises the error to `SageFsError`, but the rest are fully generic.

---

## Functions and their specifications

### `map f r` — Transform success value

**Precondition**: `f : 'T → 'U` is total  
**Postcondition**:
- If `r = Ok v` → `map f r = Ok (f v)`
- If `r = Error e` → `map f r = Error e`

**Invariants**: error type is preserved; `map id r = r` (identity law);
`map (g ∘ f) r = map g (map f r)` (composition law / functor law)

---

### `bind f r` — Monadic sequencing

**Precondition**: `f : 'T → Result<'U, 'E>` is total  
**Postcondition**:
- If `r = Ok v` → `bind f r = f v`
- If `r = Error e` → `bind f r = Error e`

**Invariants**:
- Left identity: `bind f (Ok v) = f v`
- Right identity: `bind Ok r = r`
- Associativity: `bind g (bind f r) = bind (fun v → bind g (f v)) r`

These three laws make `Result<_, 'E>` a monad over the `Ok` case.

---

### `mapError f r` — Transform error value

**Postcondition**:
- If `r = Ok v` → `mapError f r = Ok v`
- If `r = Error e` → `mapError f r = Error (f e)`

---

### `defaultWith f r` — Recover from error

**Postcondition**:
- If `r = Ok v` → `defaultWith f r = v`
- If `r = Error e` → `defaultWith f r = f e`

---

### `defaultValue d r` — Recover with constant

**Postcondition**:
- If `r = Ok v` → `defaultValue d r = v`
- If `r = Error _ → `defaultValue d r = d`

---

### `ofOption err o` — Lift Option to Result

**Postcondition**:
- If `o = Some v` → `ofOption err o = Ok v`
- If `o = None`   → `ofOption err o = Error err`

---

### `toOption r` — Demote Result to Option

**Postcondition**:
- If `r = Ok v`    → `toOption r = Some v`
- If `r = Error _` → `toOption r = None`

**Round-trip**: `toOption (ofOption e o) = o` for all `o : Option 'T`  
**Partial round-trip**: `ofOption e (toOption r)` = `r` only when `r = Ok _`

---

### `zip r1 r2` — Combine two results (first-error wins)

**Postcondition**:
- If `r1 = Ok a, r2 = Ok b` → `zip r1 r2 = Ok (a, b)`
- If `r1 = Error e, _`       → `zip r1 r2 = Error e`
- If `r1 = Ok _, r2 = Error e` → `zip r1 r2 = Error e`

---

### `apply fResult xResult` — Applicative apply

**Postcondition**:
- If `fResult = Ok f, xResult = Ok x` → `apply fResult xResult = Ok (f x)`
- If `fResult = Error e, _`             → `apply fResult xResult = Error e`
- If `fResult = Ok _, xResult = Error e` → `apply fResult xResult = Error e`

---

### `tap f r` — Peek at success value (side-effect, pass-through)

**Postcondition**: result is unchanged; `tap f r = r` (ignoring side effects)

---

### `tapError f r` — Peek at error value (side-effect, pass-through)

**Postcondition**: result is unchanged; `tapError f r = r` (ignoring side effects)

---

### `sequence results` — Collect list of results (all-or-first-error)

**Postcondition**:
- If all elements are `Ok vi` → `sequence results = Ok [v0; v1; …; vn-1]` (order preserved)
- If any element is `Error e` → `sequence results = Error e` (first error wins)

**Edge cases**:
- `sequence [] = Ok []`
- `sequence [Ok v] = Ok [v]`
- `sequence [Error e; Ok v] = Error e` (error before success → error)
- `sequence [Ok v; Error e] = Error e` (error after success → error)

**Length invariant**: if `sequence xs = Ok vs` then `vs.length = xs.length`

---

### `traverse f items` — Map-then-collect

**Postcondition**: `traverse f items = sequence (List.map f items)`

---

### `partition results` — Split into oks and errors

**Postcondition**: 
- Returns `(oks, errs)` where `oks` = values from `Ok` elements (in order)
  and `errs` = values from `Error` elements (in order)
- `oks.length + errs.length = results.length`

---

### `isOk r` / `isError r` — Boolean predicates

**Postcondition**:
- `isOk r = true  ↔ ∃ v, r = Ok v`
- `isError r = true ↔ ∃ e, r = Error e`
- `isOk r = !isError r` (exhaustive)

---

## Key invariants

1. **map/bind coherence**: `map f r = bind (fun v → Ok (f v)) r`
2. **monad laws**: left identity, right identity, associativity for `bind`
3. **functor laws**: `map id = id`, `map (g ∘ f) = map g ∘ map f`
4. **sequence length**: if `sequence xs = Ok vs` then `vs.length = xs.length`
5. **partition completeness**: `oks.length + errs.length = results.length`
6. **round-trip**: `toOption ∘ ofOption e = id` on `Option 'T`

---

## Edge cases

- All functions are total (no exceptions)
- `sequence []` = `Ok []`
- `partition []` = `([], [])`
- `traverse f []` = `Ok []`

---

## Open questions

1. **Error priority in `zip`**: when both arguments are `Error`, the left error wins.
   Is this intentional? The F# code makes this explicit but a comment would help.
2. **`tap`/`tapError` semantics**: the spec treats these as identity functions
   (ignoring side effects). In a pure Lean model, we verify the return value
   only, not the side effects.
3. **`describe` function**: specialised to `SageFsError` — should it be
   verified against the actual `SageFsError.describe` implementation?
