/-!
  # Formal Specification: ResultEx

  🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*
  Source: `SageFs.Core/ResultEx.fs`

  This file formalises the railway-oriented programming combinators in `ResultEx`.
  We work with the standard `Except ε α` type (Lean's built-in equivalent of
  `Result<'T, 'E>`), where `Ok v ↔ Except.ok v` and `Error e ↔ Except.error e`.

  Tasks in scope:
  - Phase 3: type definitions, signatures, key propositions (proofs are `sorry` or
    `decide` where trivial)
  - Phase 4/5: proofs of functor/monad laws and structural lemmas are attempted
    below; some require `sorry` pending deeper proof engineering.
-/

/-- Mirror of ResultEx.map -/
def resMap {α β ε : Type} (f : α → β) (r : Except ε α) : Except ε β :=
  match r with
  | .ok v => .ok (f v)
  | .error e => .error e

/-- Mirror of ResultEx.bind -/
def resBind {α β ε : Type} (r : Except ε α) (f : α → Except ε β) : Except ε β :=
  match r with
  | .ok v => f v
  | .error e => .error e

/-- Mirror of ResultEx.mapError -/
def resMapError {α ε δ : Type} (f : ε → δ) (r : Except ε α) : Except δ α :=
  match r with
  | .ok v => .ok v
  | .error e => .error (f e)

/-- Mirror of ResultEx.defaultWith -/
def resDefaultWith {α ε : Type} (f : ε → α) (r : Except ε α) : α :=
  match r with
  | .ok v => v
  | .error e => f e

/-- Mirror of ResultEx.ofOption -/
def resOfOption {α ε : Type} (err : ε) (o : Option α) : Except ε α :=
  match o with
  | .some v => .ok v
  | .none => .error err

/-- Mirror of ResultEx.toOption -/
def resToOption {α ε : Type} (r : Except ε α) : Option α :=
  match r with
  | .ok v => .some v
  | .error _ => .none

/-- Mirror of ResultEx.zip (left error wins) -/
def resZip {α β ε : Type} (r1 : Except ε α) (r2 : Except ε β) : Except ε (α × β) :=
  match r1, r2 with
  | .ok a, .ok b => .ok (a, b)
  | .error e, _ => .error e
  | _, .error e => .error e

/-- Mirror of ResultEx.apply -/
def resApply {α β ε : Type} (fResult : Except ε (α → β)) (xResult : Except ε α) : Except ε β :=
  match fResult, xResult with
  | .ok f, .ok x => .ok (f x)
  | .error e, _ => .error e
  | _, .error e => .error e

/-- Mirror of ResultEx.sequence (pure functional model; accumulates via List.reverse) -/
def resSequence {α ε : Type} (results : List (Except ε α)) : Except ε (List α) :=
  let rec go (acc : List α) : List (Except ε α) → Except ε (List α)
    | [] => .ok acc.reverse
    | .ok v :: rest => go (v :: acc) rest
    | .error e :: _ => .error e
  go [] results

/-- Mirror of ResultEx.partition -/
def resPartition {α ε : Type} (results : List (Except ε α)) : List α × List ε :=
  results.foldl (fun (acc : List α × List ε) r =>
    match r with
    | .ok v => (acc.1 ++ [v], acc.2)
    | .error e => (acc.1, acc.2 ++ [e])) ([], [])

-- ---------------------------------------------------------------------------
-- Functor laws
-- ---------------------------------------------------------------------------

/-- map id is the identity. -/
theorem resMap_id {α ε : Type} (r : Except ε α) : resMap id r = r := by
  cases r <;> rfl

/-- map composes. -/
theorem resMap_comp {α β γ ε : Type} (f : α → β) (g : β → γ) (r : Except ε α) :
    resMap g (resMap f r) = resMap (g ∘ f) r := by
  cases r <;> rfl

-- ---------------------------------------------------------------------------
-- Monad laws
-- ---------------------------------------------------------------------------

/-- Left identity: bind (ok v) f = f v -/
theorem resBind_left_id {α β ε : Type} (v : α) (f : α → Except ε β) :
    resBind (.ok v) f = f v := by
  rfl

/-- Right identity: bind r ok = r -/
theorem resBind_right_id {α ε : Type} (r : Except ε α) :
    resBind r Except.ok = r := by
  cases r <;> rfl

/-- Associativity: bind (bind r f) g = bind r (fun v => bind (f v) g) -/
theorem resBind_assoc {α β γ ε : Type} (r : Except ε α) (f : α → Except ε β) (g : β → Except ε γ) :
    resBind (resBind r f) g = resBind r (fun v => resBind (f v) g) := by
  cases r <;> rfl

-- ---------------------------------------------------------------------------
-- map / bind coherence
-- ---------------------------------------------------------------------------

/-- map f r = bind r (fun v => ok (f v)) -/
theorem resMap_eq_bind {α β ε : Type} (f : α → β) (r : Except ε α) :
    resMap f r = resBind r (fun v => .ok (f v)) := by
  cases r <;> rfl

-- ---------------------------------------------------------------------------
-- round-trip properties (ofOption / toOption)
-- ---------------------------------------------------------------------------

/-- toOption ∘ ofOption is the identity on Option. -/
theorem toOption_ofOption_id {α ε : Type} (e : ε) (o : Option α) :
    resToOption (resOfOption e o) = o := by
  cases o <;> rfl

/-- ofOption then toOption is the identity on Ok results. -/
theorem ofOption_toOption_ok {α ε : Type} (e : ε) (v : α) :
    resOfOption e (resToOption (.ok v : Except ε α)) = .ok v := by
  rfl

-- ---------------------------------------------------------------------------
-- sequence: structural lemmas
-- ---------------------------------------------------------------------------

/-- sequence [] = Ok [] -/
theorem resSequence_nil {α ε : Type} : resSequence ([] : List (Except ε α)) = .ok [] := by
  rfl

/-- sequence with a single Ok. -/
theorem resSequence_single_ok {α ε : Type} (v : α) :
    resSequence [Except.ok (ε := ε) v] = .ok [v] := by
  rfl

/-- sequence with a single Error. -/
theorem resSequence_single_error {α ε : Type} (e : ε) :
    resSequence [Except.error (α := α) e] = .error e := by
  rfl

/-- If sequence returns Ok, the result has the same length as the input. -/
theorem resSequence_length {α ε : Type} (xs : List (Except ε α)) (vs : List α)
    (h : resSequence xs = .ok vs) : vs.length = xs.length := by
  sorry -- requires induction on the accumulator form of resSequence.go

-- ---------------------------------------------------------------------------
-- partition: completeness
-- ---------------------------------------------------------------------------

/-- The total elements in partition equals the input length. -/
theorem resPartition_length {α ε : Type} (xs : List (Except ε α)) :
    let (oks, errs) := resPartition xs
    oks.length + errs.length = xs.length := by
  sorry -- foldl accumulator induction

-- ---------------------------------------------------------------------------
-- isOk / isError: boolean predicates
-- ---------------------------------------------------------------------------

/-- isOk characterisation. -/
theorem isOk_iff {α ε : Type} (r : Except ε α) :
    (∃ v, r = .ok v) ↔ (match r with | .ok _ => True | .error _ => False) := by
  cases r <;> simp

/-- isOk and isError are complementary. -/
theorem isOk_isError_complement {α ε : Type} (r : Except ε α) :
    (match r with | .ok _ => true | .error _ => false) =
    !(match r with | .ok _ => false | .error _ => true) := by
  cases r <;> rfl

-- ---------------------------------------------------------------------------
-- zip properties
-- ---------------------------------------------------------------------------

/-- zip of two Oks. -/
theorem resZip_ok_ok {α β ε : Type} (a : α) (b : β) :
    resZip (.ok a : Except ε α) (.ok b) = .ok (a, b) := by rfl

/-- zip propagates left error. -/
theorem resZip_error_left {α β ε : Type} (e : ε) (r : Except ε β) :
    resZip (.error e : Except ε α) r = .error e := by rfl

#check @resMap
#check @resBind
#check @resSequence
#check @resPartition
