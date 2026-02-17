# Vim-like Keymap Architecture for SageFs

## Problem Statement

Current keymap system (`KeyMap = Map<KeyCombo, UiAction>`) only supports single-chord bindings (e.g., `Ctrl+Q`). No support for:
- **Key sequences**: `g g`, `d d`, `<leader> f f`
- **Modes**: Normal/Insert/Visual/Command with different bindings per mode
- **Pane-specific overrides**: Sessions pane remaps arrows, but it's hardcoded
- **Leader key**: Common in modern Neovim configs (`<Space>` as leader)
- **Timeout disambiguation**: When `d` could be standalone or prefix of `d d`

## Design

### Core Types (in `SageFs.Core/KeyMap.fs` — new file)

```fsharp
/// A single key press (the atomic unit of input)
type KeyInput =
  | Key of ConsoleKey * ConsoleModifiers
  | Char of char
  | Leader

/// A binding trigger — one or more key inputs in sequence
type KeySequence = KeyInput list

/// What mode the editor is in
[<RequireQualifiedAccess>]
type InputMode =
  | Normal
  | Insert
  | Visual
  | Command

/// Context for keymap resolution (mode + focused pane)
type KeymapContext = {
  Mode: InputMode
  FocusedPane: PaneId
}

/// Result of feeding a key into the matcher
[<RequireQualifiedAccess>]
type KeyMatchResult =
  | Matched of UiAction
  | Pending           // prefix matches, waiting for more keys
  | NoMatch           // no binding found
  | Timeout of UiAction // timeout fired on ambiguous prefix that IS a binding

/// A single keybinding: mode + optional pane scope + sequence → action
type KeyBinding = {
  Mode: InputMode
  Pane: PaneId option   // None = global, Some = pane-specific
  Sequence: KeySequence
  Action: UiAction
}

/// Trie node for efficient prefix matching
type KeyTrieNode = {
  Action: UiAction option         // binding at this node (if any)
  Children: Map<KeyInput, KeyTrieNode>
}

/// The full keymap — one trie per (mode, pane option) pair
type KeyMap2 = {
  Bindings: Map<InputMode * PaneId option, KeyTrieNode>
  Leader: KeyInput
  TimeoutMs: int
  VimMode: bool  // false = all bindings in Insert mode, no modal switching
}
```

### Sequence Matcher (stateful, per-UI)

```fsharp
type SequenceMatcher = {
  mutable pending: KeyInput list  // keys pressed so far
  mutable lastKeyTime: int64     // timestamp of last key press
  keymap: KeyMap2
}

module SequenceMatcher =
  /// Feed a key input. Returns what to do.
  val feed : KeymapContext -> KeyInput -> KeyMatchResult
  
  /// Check for timeout (call each frame)
  val checkTimeout : KeymapContext -> KeyMatchResult option
  
  /// Reset pending state (e.g., on Escape)
  val reset : unit -> unit
```

**Disambiguation logic:**
1. Look up `pending @ [newKey]` in the trie for current (mode, pane)
2. If exact match with no children → `Matched action` (instant)
3. If exact match WITH children → start timeout timer, return `Pending`
4. If no exact match but has children → return `Pending` (wait for more)
5. If no exact match and no children → `NoMatch`
6. On timeout tick: if pending is a valid binding → `Timeout action`

### Mode System

**Opt-out via `VimMode = false`:**
- When `VimMode = false`, all keys are processed as if always in `Insert` mode
- No mode switching, no Normal mode, no leader sequences
- Existing behavior exactly preserved

**When `VimMode = true`:**
- Start in `Normal` mode
- `i` → Insert, `v` → Visual, `:` → Command, `Escape` → Normal
- Mode transitions are themselves keybindings (configurable)
- Status bar shows mode indicator: `-- NORMAL --`, `-- INSERT --`, `-- VISUAL --`

**Mode transitions as UiActions:**
```fsharp
// Add to UiAction DU
| SwitchInputMode of InputMode
| CopySelection    // Ctrl+C / y in Visual mode
```

### Pane-Specific Layers

Resolution order (first match wins):
1. `(currentMode, Some currentPane)` — pane-specific binding
2. `(currentMode, None)` — global binding for current mode
3. Fall through to char insertion (Insert mode only)

**Example:** Sessions pane overrides:
```fsharp
{ Mode = Normal; Pane = Some PaneId.Sessions; Sequence = [Key(J, 0)]; Action = Editor SessionNavDown }
{ Mode = Normal; Pane = Some PaneId.Sessions; Sequence = [Key(K, 0)]; Action = Editor SessionNavUp }
{ Mode = Normal; Pane = Some PaneId.Sessions; Sequence = [Key(Enter, 0)]; Action = Editor SessionSelect }
{ Mode = Normal; Pane = Some PaneId.Sessions; Sequence = [Key(D, 0); Key(D, 0)]; Action = Editor SessionDelete }
```

### Default Bindings

**Normal mode (vim-like):**
```
h/j/k/l        → cursor movement
i               → Insert mode
v               → Visual mode
:               → Command mode
g g             → go to top
G               → go to bottom
d d             → delete line
<Space> f       → find/trigger completion
<Space> e       → eval/submit
<Space> r       → reset session
<Space> R       → hard reset session
<Space> s       → toggle session panel
<Space> n       → new session
<Space> c       → clear output
Ctrl+C          → copy selection (always, regardless of mode)
```

**Insert mode:**
```
Escape          → Normal mode
Enter           → new line
Ctrl+Enter      → submit
Backspace       → delete backward
(all printable) → insert char
Ctrl+Z          → undo
Ctrl+Shift+Z    → redo
```

**Visual mode:**
```
Escape          → Normal mode
h/j/k/l         → extend selection
y               → yank (copy) + Normal mode
d               → delete selection + Normal mode
```

**Non-vim mode (VimMode = false):**
All existing bindings preserved exactly, just moved into the new type system. Everything runs in Insert mode permanently.

### Config File Format

```fsx
// ~/.sagefs/keymap.fsx
let vimMode = true
let leader = "Space"
let timeoutMs = 300

let keybindings = [
  // mode, pane, sequence, action
  "Normal", "*", "g g", "MoveToTop"
  "Normal", "*", "d d", "DeleteLine"  
  "Normal", "*", "<leader> f", "TriggerCompletion"
  "Normal", "Sessions", "j", "SessionNavDown"
  "Insert", "*", "Ctrl+Enter", "Submit"
]
```

### Status Bar Integration

Mode indicator rendered by shared `Screen` module (works in TUI + Raylib):
```
┌─ Editor ─────────────────────────── -- NORMAL -- ─┐
```

When `VimMode = false`, no mode indicator shown.

Pending sequence shown briefly (like Neovim's `showcmd`):
```
┌─ Editor ──────────────────── d_ ── -- NORMAL -- ─┐
```

### Migration Path

1. New `KeyMap2` type lives alongside old `KeyMap` temporarily
2. `KeyMap.defaults` converted to `KeyMap2` equivalent
3. Both TUI and Raylib switch to `SequenceMatcher`
4. Old `KeyMap` type deprecated, removed after migration
5. Existing `config.fsx` keybinding format extended for sequences

## Implementation Plan

### Phase 1: Core Types + Matcher
- [ ] Create `SageFs.Core/KeyMap.fs` with types
- [ ] Implement `KeyTrieNode` builder from `KeyBinding list`
- [ ] Implement `SequenceMatcher` with feed/timeout/reset
- [ ] Add `InputMode`, `SwitchInputMode`, `CopySelection` to UiAction
- [ ] Tests: sequence matching, timeout, disambiguation, pane priority

### Phase 2: Default Bindings + Mode System  
- [ ] Port all `KeyMap.defaults` bindings to `KeyBinding list`
- [ ] Add Normal/Insert/Visual mode bindings
- [ ] Add leader-key bindings
- [ ] Add pane-specific session bindings
- [ ] Tests: mode transitions, leader sequences

### Phase 3: UI Integration
- [ ] TUI: Replace `mapKeyWith` with `SequenceMatcher.feed`
- [ ] Raylib: Replace `mapKeyWith` with `SequenceMatcher.feed`
- [ ] Status bar: mode indicator + pending sequence display
- [ ] Handle timeout tick in frame loops

### Phase 4: Config + Polish
- [ ] Extend config file parser for sequence format
- [ ] `VimMode = false` flag with existing-behavior preservation
- [ ] Leader key configuration
- [ ] Remove old `KeyMap` type

## Notes
- `KeyMap2` name is temporary — will become `KeyMap` when old one is removed
- Leader key is `Space` by default, configurable
- Timeout is 300ms by default, configurable  
- Pane-specific bindings take priority over global
- Ctrl+C always copies selection regardless of mode (hardwired in GUI, not in keymap)

## Expert Review Panel

### Mark Seemann (ploeh.dk) — Functional Architecture & Composability

**Overall:** The domain modeling is reasonable, but mutability is smuggled into what should be a pure functional design, and some type design choices conflate concerns.

**The `SequenceMatcher` is a red flag.** `mutable pending` and `mutable lastKeyTime` sit in the middle of an otherwise-immutable type system. This is the classic "I need state so I'll just make it mutable" escape hatch. In an Elm-architecture application — which SageFs already is — the pending key buffer and last-key timestamp belong in the *model*, not in a side-channel mutable object. The matcher should be a pure function:

```fsharp
type MatcherState = { Pending: KeyInput list; LastKeyTime: int64 }

module SequenceMatcher =
  let feed (keymap: KeyMap2) (context: KeymapContext) (state: MatcherState) (key: KeyInput) (now: int64)
    : KeyMatchResult * MatcherState
```

This is a classic fold. Each key press produces a new state and a result. The Elm `update` function already handles this pattern — `SequenceMatcher` should compose into it, not bypass it. The mutable version will also be harder to test: setup/teardown instead of passing values in and asserting on values out.

**`KeyMatchResult` conflates "what happened" with "what to do about time."** `Timeout of UiAction` is not a match result — it's a *scheduling concern*. The matcher should return `Matched | Pending | NoMatch`. Timeout resolution is the frame loop's responsibility. When the frame loop sees `Pending` and the clock exceeds the threshold, *it* decides to resolve to the pending action. Mixing temporal concerns into the matcher's return type couples two things that change for different reasons.

**The `VimMode: bool` field is a feature envy smell.** A behavioral switch encoded into a data type. Instead, `VimMode = false` should simply mean constructing a `KeyMap2` with all bindings in `Insert` mode and no mode-transition bindings. The keymap *data* determines the behavior — no boolean flag needed. If the trie has no `SwitchInputMode` actions, there's no mode switching. The flag is redundant with the binding data it controls.

**`Leader` as a DU case on `KeyInput` is questionable.** Leader is not a physical key — it's an indirection. The leader key *is* `Key(Space, NoModifiers)` or whatever the user configured. Expanding `Leader` to its physical key should happen at trie-construction time (when compiling `KeyBinding list → KeyTrieNode`), not at match time. Having `Leader` as a `KeyInput` case means the matcher needs to know what leader maps to — unnecessary runtime indirection.

**Grade: B-** — Good type modeling instincts, but mutable state and temporal coupling undermine the functional architecture already in place.

---

### Scott Wlaschin (FSharpForFunAndProfit.com) — Domain Modeling & Type-Driven Design

**Overall:** Good direction — replacing stringly-typed ad-hoc key handling with a proper domain model. But the types can be made to *make illegal states unrepresentable* more thoroughly.

**`KeySequence = KeyInput list` allows empty sequences.** An empty list is a valid `KeyInput list` but a meaningless keybinding. Use a non-empty list type:

```fsharp
type KeySequence = { First: KeyInput; Rest: KeyInput list }
```

This makes "a binding with no keys" unrepresentable. Every function that receives a `KeySequence` can safely destructure without guarding against empty.

**`KeyBinding` has an implicit constraint that isn't encoded.** Pane-specific bindings take priority — but nothing in the type prevents the *same* sequence appearing in both `(Normal, Some Sessions)` and `(Normal, None)`. That's fine semantically (it's an override), but the priority rule is documented in prose, not in types. Consider making the resolution explicit:

```fsharp
type ScopedKeyMap = {
  PaneBindings: Map<PaneId, KeyTrieNode>
  GlobalBindings: KeyTrieNode
}
```

Now the lookup function *structurally* tries pane first, then global. The prose documentation becomes executable code.

**The `InputMode` DU is closed, but the config format is open.** The config file uses strings like `"Normal"`, `"Insert"`. If someone types `"Replace"` in their config, what happens? A parsing layer with proper `Result` error handling is needed — and it's not shown anywhere in the design:

```fsharp
module InputMode =
  let tryParse : string -> Result<InputMode, string>
```

**`KeyMatchResult.Pending` carries no information.** When pending, the user wants to see `d_` in the status bar — but `Pending` doesn't tell you *what* keys are pending. It should carry the accumulated sequence:

```fsharp
| Pending of KeyInput list  // the keys so far, for display
```

This follows the principle of making types carry all the information their consumers need. Without this, the caller maintains a parallel shadow copy of the pending state — exactly the duplication that causes bugs.

**The config format mixes tuples with a list of strings.** `"Normal", "*", "g g", "MoveToTop"` is a 4-tuple of strings. Not self-describing, not extensible, no validation at the type level. For an F# config, lean into a builder pattern or proper DSL:

```fsharp
normal [key 'g'; key 'g'] MoveToTop
normal [leader; key 'f'] TriggerCompletion
inPane Sessions normal [key 'j'] SessionNavDown
```

Compile-time checking (can't misspell an action name), discoverability, and no string parsing.

**Grade: B+** — Domain decomposition is sound and DUs are well-chosen. Tighten the types to eliminate more illegal states.

---

### Casey Muratori (Computer Enhance) — Performance & Latency

**Overall:** For a text editor keymap, throughput is fine — there will never be enough bindings for the trie to matter. But concerns about *latency* introduced by the timeout mechanism, and about unnecessary indirection.

**The 300ms timeout is a UX landmine.** Every single-key binding that *could* be a prefix of a multi-key binding now has 300ms of added latency. User presses `d` in Normal mode — can't execute until confirming it's not `d d`. So wait 300ms. That's *perceptible*. That's the difference between "feels responsive" and "feels sluggish." Vim solves this by having `d` alone do *nothing* — it's purely a prefix. Trying to have `d` be both a standalone action and a prefix creates inherent tension.

**Recommendation:** Don't allow ambiguous prefixes. If `d d` is a binding, then `d` alone is *not* a binding — it's always a prefix. This eliminates the timeout entirely for the common case. If ambiguity must be supported, make the timeout much shorter (50-80ms) and user-tunable per-binding, not globally.

**The trie is over-engineered for this scale.** Maybe 100-200 bindings. Maximum sequence length of 3-4 keys. A linear scan of a flat binding list, filtered by mode and prefix-matched against pending keys, would be just as fast and far simpler to debug. The trie's theoretical O(k) advantage over O(n) only matters when n is large. With n=200 and k=3, optimizing the wrong thing. A flat list is cache-friendly, trivially serializable, and debuggable with `printfn "%A"`. A trie is none of those things.

**`Map<KeyInput, KeyTrieNode>` has allocation overhead per lookup.** F#'s `Map` is an AVL tree — each lookup is O(log n) with comparisons and pointer chasing. For a node with 5-10 children, a sorted array with binary search or linear scan would be faster due to cache locality. At this scale it doesn't matter — mentioned only because if using a trie, at least use a flat sorted array for children, not a tree-of-trees.

**The `checkTimeout` per-frame poll is the right approach** — don't use actual timers or async callbacks. A simple `if now - lastKeyTime > timeoutMs` check each frame is the lowest-overhead way. Just guard against unnecessary trie lookups when nothing is pending:

```fsharp
let checkTimeout ctx now =
  if pending.IsEmpty then None
  elif now - lastKeyTime < timeoutMs then None
  else resolveCurrentPending ctx
```

**Grade: B** — Correct in principle, but the timeout mechanism needs sharper thinking about UX latency. Simplify the data structure.

---

### Ryan Fleury (RAD Debugger) — Systems Architecture & Practical Engineering

**Overall:** Layering is mostly right, but some abstraction choices will make debugging and iteration painful.

**`InputMode` is part of the keymap but it's really editor state.** In RAD's input handling, mode is a property of the *view* — not the input system. `KeymapContext` bundles `Mode` and `FocusedPane` together, which makes sense for lookup. But who *owns* the mode? It should live in the Elm model, update via `SwitchInputMode` actions flowing through `update`, and get read out for display and keymap lookup. The design doc doesn't make this ownership explicit — two developers might independently put it in two different places.

**The pane-specific override system will become a maintenance headache.** `Map<InputMode * PaneId option, KeyTrieNode>` means every (mode, pane) pair gets its own *complete trie*. For 4 modes × 5 pane types, that's 20 possible tries, most empty or near-empty. Worse: adding a new global binding means adding to the `(mode, None)` trie. Someone reading the code has to understand that `None` means "global fallback" and `Some pane` means "override." Implicit priority ordering via `Option` is clever but opaque.

**Make the layering explicit in the data structure:**

```fsharp
type LayeredKeyMap = {
  Global: Map<InputMode, KeyTrieNode>
  PaneOverrides: Map<InputMode * PaneId, KeyTrieNode>
}
```

Now the lookup function is obvious: check `PaneOverrides` first, fall back to `Global`. No `Option` gymnastics. Debugging "why did `j` do the wrong thing in Sessions pane" means inspecting `PaneOverrides[(Normal, Sessions)]` directly.

**The migration path (Phase 3) is the riskiest part.** Replacing `mapKeyWith` in *both* TUI and Raylib simultaneously — two UI backends with subtly different input handling (ConsoleKeyInfo vs Raylib KeyboardKey) migrated at the same time. Do one at a time. Get TUI working with the new system, ship it, let it bake, *then* port Raylib. The doc puts both in the same phase — asking for "works in TUI but Raylib has a subtle modifier key bug" problems.

**Hardcoding `Ctrl+C` as "always copies regardless of mode."** Pragmatic, but hardcoded behaviors outside the keymap system become invisible tech debt. Put it in the keymap as a binding that exists in *all* modes. If someone wants to remap Ctrl+C, they should be able to. The keymap system exists precisely to avoid special cases outside it.

**Grade: B+** — Solid practical design. Sharpen the ownership model, make layering explicit, and stagger the migration.

---

### John Carmack — Simplicity, Pragmatism & Shipping

**Overall:** Good design doc. Real problem identified, proportionate solution, phased implementation shows discipline. A few pushbacks.

**Building a vim emulator inside a REPL tool.** A feature with very high surface area. Every vim user expects slightly different things, and if `d d` doesn't behave exactly like vim's, they'll notice and be annoyed. Signing up for a long tail of "but in vim, `d` takes a motion" conversations. Be very clear about scope: this is a *vim-inspired* modal keymap, not vim keybindings. Name it accordingly in user-facing docs so expectations are set correctly.

**The config file as `.fsx` is powerful but dangerous.** Running arbitrary F# code from the user's home directory at startup is a security surface. For a keymap config, Turing completeness isn't needed — a data format is. A simple TOML or JSON file with a fixed schema is safer and faster to parse. If programmability is wanted, make it opt-in with a clear warning.

**Ship `VimMode = false` first, as a refactor.** Phase 1 and 2 build the entire vim mode system before it's usable. Restructure: Phase 1 is *only* porting existing `KeyMap.defaults` to `KeyMap2` with `VimMode = false`. No modes, no sequences, no timeouts. Just prove the new type system works without changing behavior. Phase 2 adds single-key Normal mode bindings. Phase 3 adds sequences and timeouts. Phase 4 adds leader key. Each phase is independently shippable and testable.

**The trie is fine.** At this scale, a trie, flat list, and hash map all return in microseconds. Pick whichever is clearest to read and debug. The trie makes the prefix relationship explicit in the structure, which is nice for understanding. Don't optimize further unless profiling shows it matters (it won't).

**Error recovery is undefined.** What happens when the user presses an invalid sequence? `NoMatch` is returned — then what? Does the pending buffer clear? Do already-pressed keys get replayed as individual lookups? Vim replays them (press `d x` where `d x` isn't bound: `d` gets discarded, `x` gets executed as its own binding). This must be specified explicitly, because it affects how the matcher interacts with char insertion in Insert mode.

**Grade: B+** — Ship the non-vim refactor first. Explicitly define error recovery. Be honest about scope.

---

### Consensus Summary

| Concern | Raised By | Severity |
|---|---|---|
| Mutable `SequenceMatcher` should be pure state in Elm model | Seemann | High |
| 300ms timeout adds perceptible latency — avoid ambiguous prefixes | Muratori | High |
| Error recovery (invalid sequence replay) undefined | Carmack | High |
| Ship `VimMode = false` refactor first as standalone phase | Carmack | High |
| Migrate TUI and Raylib *separately*, not in same phase | Fleury | High |
| `KeySequence` allows empty lists — use non-empty type | Wlaschin | Medium |
| `Pending` should carry accumulated keys for display | Wlaschin | Medium |
| `VimMode: bool` is redundant with binding data | Seemann | Medium |
| `Leader` as `KeyInput` case — resolve at construction time | Seemann | Medium |
| Pane layering should be structurally explicit, not via `Option` | Fleury | Medium |
| `Timeout` in `KeyMatchResult` conflates matching with scheduling | Seemann | Medium |
| `.fsx` config is a security surface — consider data format | Carmack | Medium |
| `Ctrl+C` hardcoded outside keymap — put it in the system | Fleury | Low |
| Trie vs flat list: trie is fine but don't over-optimize children | Muratori | Low |
