# Vim-like Keymap Architecture for SageFs

## Problem Statement

Current keymap system (`KeyMap = Map<KeyCombo, UiAction>`) only supports single-chord bindings (e.g., `Ctrl+Q`). No support for:
- **Key sequences**: `g g`, `d d`, `<leader> f f`
- **Modes**: Normal/Insert/Visual/Command with different bindings per mode
- **Pane-specific overrides**: Sessions pane remaps arrows, but it's hardcoded
- **Context-aware bindings**: Different contexts (menus, dialogs, completion popups, plugins) need their own key overrides
- **Leader key**: Common in modern Neovim configs (`<Space>` as leader)
- **Sequence cancellation**: Escape clears pending sequence, Backspace removes last key from it

## Design

### Core Types (in `SageFs.Core/KeyMap.fs` — new file)

```fsharp
/// Every physical key — a closed DU so the DSL catches typos at compile time.
/// Modifiers (Shift, Ctrl, Alt) are applied separately, not baked into the key.
[<RequireQualifiedAccess>]
type Key =
  // Letters
  | A | B | C | D | E | F | G | H | I | J | K | L | M
  | N | O | P | Q | R | S | T | U | V | W | X | Y | Z
  // Digits
  | D0 | D1 | D2 | D3 | D4 | D5 | D6 | D7 | D8 | D9
  // Navigation
  | Up | Down | Left | Right
  | Home | End | PageUp | PageDown
  // Editing
  | Enter | Escape | Tab | Space | Backspace | Delete | Insert
  // Function keys
  | F1 | F2 | F3 | F4 | F5 | F6 | F7 | F8 | F9 | F10 | F11 | F12
  // Punctuation / symbols
  | Minus | Plus | Equals | LeftBracket | RightBracket
  | Semicolon | Quote | Comma | Period | Slash | Backslash | Backtick

// Key DU is deliberately closed — add cases as needed for numpad, media keys,
// platform-specific keys. Compile errors when a new key appears are preferable
// to silent fallthrough. (Carmack, Round 2)

/// Modifier flags — combinable (explicit enumeration — exhaustive pattern matching,
/// no invalid combinations. Composability tradeoff noted — fine at keymap scale
/// where bindings are defined once and matched many times. Wlaschin, Round 2)
[<RequireQualifiedAccess>]
type Modifier =
  | None
  | Shift
  | Ctrl
  | Alt
  | CtrlShift
  | CtrlAlt
  | AltShift
  | CtrlAltShift

/// A single key press: physical key + modifier. The atomic unit of input.
type KeyInput = {
  Key: Key
  Modifier: Modifier
}

/// A binding trigger — guaranteed non-empty key sequence
type KeySequence = { First: KeyInput; Rest: KeyInput list }

/// What mode the editor is in
[<RequireQualifiedAccess>]
type InputMode =
  | Normal
  | Insert
  | Visual
  | Command

module InputMode =
  let tryParse : string -> Result<InputMode, string>

/// Every physical key — parsing boundary for string-based config
module Key =
  let tryParse : string -> Result<Key, string>

/// The context in which keys are being processed.
/// More general than just "which pane" — covers any UI state
/// that needs its own key overrides.
[<RequireQualifiedAccess>]
type KeyContext =
  | Pane of PaneId              // focused pane (Editor, Output, Sessions, Diagnostics)
  | Menu of string              // command palette, file picker, context menu
  | Dialog of string            // confirmation prompt, input dialog
  | Overlay of string           // which-key hints, autocomplete dropdown
  | Search                      // find/replace bar active
  | OperatorPending             // waiting for motion after d/c/y
  | LangArg                     // waiting for single char after f/t/r
  | Completion                  // autocomplete popup visible
  | Repl                        // FSI input area
  | Plugin of string            // third-party registered context

module KeyContext =
  let tryParse : string -> Result<KeyContext, string>

/// Context for keymap resolution (mode + active context stack)
type KeymapContext = {
  Mode: InputMode
  Contexts: KeyContext list     // ordered most-specific first; lookup walks the list
}

/// Result of feeding a key into the matcher
[<RequireQualifiedAccess>]
type KeyMatchResult =
  | Matched of UiAction
  | Pending of KeyInput list  // prefix matches, waiting for more keys (carries accumulated keys for display)
  | NoMatch                   // no binding found
  | Cancelled                 // Escape pressed, pending sequence cleared
  | Backstepped of KeyInput list // Backspace pressed, last key removed from pending

/// Where a binding applies — eliminates Option-as-convention
[<RequireQualifiedAccess>]
type BindingScope =
  | Global
  | InContext of KeyContext

/// A single keybinding: mode + scope + sequence → action
type KeyBinding = {
  Mode: InputMode
  Scope: BindingScope
  Sequence: KeySequence
  Action: UiAction
}

/// Trie node for efficient prefix matching
type KeyTrieNode = {
  Action: UiAction option         // binding at this node (if any)
  Children: Map<KeyInput, KeyTrieNode>
}

/// Bindings for a single mode, with explicit context layering
type ScopedKeyMap = {
  Global: KeyTrieNode
  ContextOverrides: Map<KeyContext, KeyTrieNode>
}

/// The full keymap — one scoped map per mode
/// Leader key is resolved to its physical KeyInput at construction time
/// (e.g., `<leader> f` becomes `[Key(Space, 0); Char 'f']` in the trie)
type KeyMap2 = {
  Modes: Map<InputMode, ScopedKeyMap>
}

/// Errors that can occur during trie construction
type KeyMapBuildError =
  | AmbiguousPrefix of {| Prefix: KeySequence; ConflictsWith: KeySequence; Mode: InputMode; Scope: BindingScope |}
  | DuplicateBinding of {| Sequence: KeySequence; Mode: InputMode; Scope: BindingScope |}

module KeyMap2 =
  /// Build a KeyMap2 from bindings. Returns Error if any sequence is a strict
  /// prefix of another in the same (mode, scope) — no ambiguous prefixes allowed.
  val build : KeyBinding list -> Result<KeyMap2, KeyMapBuildError list>
```

**Design decisions adopted from expert review:**
- **`BindingScope` instead of `KeyContext option`** (Seemann, Round 2): `KeyBinding.Scope: BindingScope` eliminates the `None = global` convention. `BindingScope.Global` and `BindingScope.InContext ctx` are explicit at every layer, not just in the compiled trie.
- **Prefix collisions rejected at construction** (Seemann, Round 2): `KeyMap2.build` returns `Result<KeyMap2, KeyMapBuildError list>`. If a sequence is a strict prefix of another in the same (mode, scope), construction fails with `AmbiguousPrefix` describing the conflict. No silent shadowing.
- **No `Leader` case on `KeyInput`** (Seemann): Leader is not a physical key — it's an indirection. The leader key (e.g., `Space`) is resolved to its physical `KeyInput` at trie-construction time when compiling `KeyBinding list → KeyTrieNode`. The matcher never sees `Leader`; it only sees physical keys.
- **Non-empty `KeySequence`** (Wlaschin): `{ First: KeyInput; Rest: KeyInput list }` makes empty sequences unrepresentable. No runtime guards needed.
- **No `VimMode: bool` flag** (Seemann): Modal vs non-modal is determined entirely by the binding data. Non-modal mode simply constructs a `KeyMap2` with all bindings in `Insert` mode and no `SwitchInputMode` actions. If the trie has no mode transitions, there's no mode switching.
- **General `KeyContext` instead of `PaneId`**: Panes are just one kind of context. Menus, dialogs, overlays, completion popups, operator-pending state, search, REPL, and plugins all get their own overrides through the same mechanism. `Contexts` is a list ordered most-specific first — lookup walks it until a match is found, then falls back to `Global`.
- **`Pending` carries accumulated keys** (Wlaschin): Status bar can display pending sequence directly from the match result without shadow state.
- **`InputMode.tryParse`**, **`Key.tryParse`**, **`KeyContext.tryParse`** (Wlaschin): Every boundary where strings enter the type system returns `Result`. Config parsing never throws.
- **DSL functions return `Result`** (Wlaschin, Round 2): `normal`, `insert`, `inContext` etc. return `Result<KeyBinding, string>`. Validation errors surface at the definition site with locality, not deferred to trie compilation. `KeyMapDsl.validateAll` collects all results.
- **Closed `Key` DU instead of `char`/`ConsoleKey`**: Every valid key is a DU case (`Key.A` through `Key.Z`, `Key.Enter`, `Key.Space`, etc.). Typos are compile errors, not runtime bugs. Uppercase is `shift Key.D`, consistent with `ctrl Key.Z` — all modifiers work the same way.
- **Platform key conversion must be a direct mapping** (Muratori, Round 2): `ConsoleKeyInfo → KeyInput` (TUI) and `KeyboardKey → KeyInput` (Raylib) must be a match expression or lookup table — never string parsing. Runs on every keystroke; easy to get right, annoying to debug if wrong.
- **Which-key overlay**: When a sequence is `Pending`, a which-key popup enumerates all available continuations from the current trie node. Critical for discoverability since there are no timeouts — the user needs to see what's available.
- **Keymap debug mode** (Fleury, Round 2): Toggle-able debug logging that shows the full `KeymapContext`, context resolution walk, and `KeyMatchResult` for each key press in the diagnostics pane. Zero cost when disabled — the pure `SequenceMatcher` doesn't log; the Elm `update` wrapper captures input/output around the call.

### Sequence Matcher (pure, composes into Elm update)

```fsharp
/// Immutable matcher state — lives in the Elm model
type MatcherState = {
  Pending: KeyInput list
}

module MatcherState =
  let empty = { Pending = [] }

module SequenceMatcher =
  /// Feed a key input. Returns match result and new state.
  /// Pure function — no mutation, no side effects.
  val feed : KeyMap2 -> KeymapContext -> MatcherState -> KeyInput
    -> KeyMatchResult * MatcherState
  
  /// Cancel pending sequence (Escape). Returns Cancelled and empty state.
  val cancel : MatcherState -> KeyMatchResult * MatcherState
  
  /// Remove last key from pending (Backspace). Returns Backstepped and updated state.
  val backstep : MatcherState -> KeyMatchResult * MatcherState
```

The matcher is a pure fold: `(state, key) → (result, state')`. It composes directly into the Elm `update` function — `MatcherState` is a field on the Elm model, updated alongside everything else. No mutable side-channel, fully testable by passing values in and asserting on values out.

**No timeouts — sequences persist until explicitly resolved.** Like Neovim's `which-key` style: pending keys stay until one of:
- Next key completes a match → `Matched action`
- Next key has no match and no further children → `NoMatch` (pending cleared)
- `Escape` → `Cancelled` (pending cleared)
- `Backspace` → `Backstepped` (last key removed from pending; if pending becomes empty, return to clean state)

**Disambiguation logic:**
1. Look up `pending @ [newKey]` in the trie for current (mode, context stack)
2. If exact match with no children → `Matched action` (instant, pending cleared)
3. If exact match WITH children → return `Pending` (wait for more keys, no timeout)
4. If no exact match but has children → return `Pending` (wait for more)
5. If no exact match and no children → `NoMatch` (pending cleared)
6. If `Escape` while pending → `Cancelled` (pending cleared)
7. If `Backspace` while pending → remove last key, return `Backstepped` with remaining pending

**No ambiguous prefixes allowed.** If `d d` is a binding, then `d` alone is *not* a binding — it is purely a prefix. This eliminates timeout complexity entirely. The user always knows where they are by looking at the pending sequence indicator in the status bar, and can cancel or backstep at will.

### Error Recovery

When a key sequence reaches a dead end (`NoMatch`), the pending buffer is discarded — **no replay**. This matches Neovim's behavior with `which-key` style sequences: if you press `d x` and `d x` isn't bound, both keys are dropped and you're back to a clean state. The rationale:

1. **Replay is dangerous in Normal mode.** If `d x` replayed `x` as a standalone binding, the user gets an unexpected action from a sequence they were trying to build. Discarding is safer — the user sees the pending indicator clear and knows the sequence failed.
2. **In Insert mode, there are no multi-key sequences** (all printable chars insert immediately). So the replay question only applies to Normal/Visual/Command modes, where discarding is the right behavior.
3. **Backspace gives the user control.** If they fat-finger the second key, they can Backspace to remove it and try again — they don't need automatic replay to "recover" the first key.

**Concrete behavior on `NoMatch`:**
- Pending buffer cleared entirely
- No action dispatched
- Status bar pending indicator clears
- User is back to clean state, ready for next input

**Edge case — `NoMatch` with no pending keys** (single key that isn't bound at all):
- In Normal/Visual/Command mode: ignored silently (no action, no error)
- In Insert mode: impossible for printable chars (they always insert). Non-printable unbound keys are ignored silently.

### Mode System

**No `VimMode` flag — determined by binding data:**
- Non-modal mode: construct `KeyMap2` with all bindings in `Insert` mode and no `SwitchInputMode` actions
- Modal mode: construct `KeyMap2` with Normal/Insert/Visual/Command bindings including `SwitchInputMode` transitions
- The keymap *data* determines modal behavior — no boolean flag needed

**User-facing terminology: "modal mode", not "vim mode."** The moment you say "vim," every user expects full vim compatibility (`ciw`, motions, registers, macros). This is a *vim-inspired* modal keymap — call it "modal mode" or "keyboard mode" in the UI, status bar, settings, and documentation. Set expectations honestly.

**When modal bindings are present:**
- Start in `Normal` mode
- `i` → Insert, `v` → Visual, `:` → Command, `Escape` → Normal
- Mode transitions are themselves keybindings (configurable)
- Status bar shows mode indicator: `-- NORMAL --`, `-- INSERT --`, `-- VISUAL --`

**Mode transitions as UiActions:**
```fsharp
// Add to UiAction DU
| SwitchInputMode of InputMode
| CopySelection    // Ctrl+C / y in Visual mode

// Every UiAction needs a display name for the which-key overlay
module UiAction =
  let displayName : UiAction -> string
```

### Context Layers

Resolution walks the `KeymapContext.Contexts` list (most-specific first) via `ScopedKeyMap`:
1. For each `KeyContext` in the context stack, check `Modes[currentMode].ContextOverrides[context]`
2. If no context-specific match found, fall back to `Modes[currentMode].Global`
3. Fall through to char insertion (Insert mode only)

**Why a context stack, not a single context?** Multiple contexts can be active simultaneously. For example: the `Completion` overlay is visible while the `Pane Editor` is focused while in `Insert` mode. The stack `[Completion; Pane Editor]` means completion bindings take priority over editor bindings, which take priority over global.

**Context stacks should be kept shallow** (typically 2-3 deep). Each key press walks the stack doing a trie lookup per context. At N=3 this is negligible; at N=10+ it's still microseconds but indicates something is wrong with the UI layering. Code should assert or warn if the stack exceeds a reasonable depth (e.g., 8).

**Context examples:**

Sessions pane overrides:
```fsharp
{ Mode = Normal; Scope = InContext (Pane Sessions); Sequence = { First = { Key = Key.J; Modifier = None }; Rest = [] }; Action = Editor SessionNavDown }
{ Mode = Normal; Scope = InContext (Pane Sessions); Sequence = { First = { Key = Key.K; Modifier = None }; Rest = [] }; Action = Editor SessionNavUp }
{ Mode = Normal; Scope = InContext (Pane Sessions); Sequence = { First = { Key = Key.Enter; Modifier = None }; Rest = [] }; Action = Editor SessionSelect }
{ Mode = Normal; Scope = InContext (Pane Sessions); Sequence = { First = { Key = Key.D; Modifier = None }; Rest = [{ Key = Key.D; Modifier = None }] }; Action = Editor SessionDelete }
```

Completion popup overrides:
```fsharp
{ Mode = Insert; Scope = InContext Completion; Sequence = { First = { Key = Key.Tab; Modifier = None }; Rest = [] }; Action = Editor AcceptCompletion }
{ Mode = Insert; Scope = InContext Completion; Sequence = { First = { Key = Key.Down; Modifier = None }; Rest = [] }; Action = Editor NextCompletion }
{ Mode = Insert; Scope = InContext Completion; Sequence = { First = { Key = Key.Up; Modifier = None }; Rest = [] }; Action = Editor PrevCompletion }
{ Mode = Insert; Scope = InContext Completion; Sequence = { First = { Key = Key.Escape; Modifier = None }; Rest = [] }; Action = Editor DismissCompletion }
```

Command palette overrides:
```fsharp
{ Mode = Insert; Scope = InContext (Menu "command_palette"); Sequence = { First = { Key = Key.Enter; Modifier = None }; Rest = [] }; Action = Editor ExecuteCommand }
{ Mode = Insert; Scope = InContext (Menu "command_palette"); Sequence = { First = { Key = Key.Escape; Modifier = None }; Rest = [] }; Action = Editor DismissMenu }
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
Ctrl+C          → copy selection (bound in all modes via keymap, not hardcoded)
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

**Non-modal mode (no `SwitchInputMode` bindings present):**
All existing bindings preserved exactly, just moved into the new type system. Everything runs in Insert mode permanently. Determined by binding data, not a flag.

### Config File Format

```fsx
// ~/.sagefs/keymap.fsx
let leader = "Space"

// String-based config (parsed with InputMode.tryParse, validated at load time)
let keybindings = [
  "Normal", "*", "g g", "MoveToTop"
  "Normal", "*", "d d", "DeleteLine"  
  "Normal", "*", "<leader> f", "TriggerCompletion"
  "Normal", "Pane:Sessions", "j", "SessionNavDown"
  "Insert", "Completion", "Tab", "AcceptCompletion"
  "Insert", "*", "Ctrl+Enter", "Submit"
]
```

**Preferred: Type-safe DSL (compile-time checked, validated at definition):**
```fsharp
open SageFs.Core.KeyMapDsl

// Key.d, Key.g, Key.Enter etc. — DU cases, not strings or chars.
// Modifiers applied via shift/ctrl/alt combinators.
// Uppercase G = shift Key.G is explicit. Can't accidentally mix up 'g' and 'G'.
// Each DSL function returns Result<KeyBinding, string> — errors surface at the
// definition site, not when the trie is compiled.

let bindings : Result<KeyBinding, string> list = [
  normal [Key.G; Key.G] MoveToTop              // g g → go to top
  normal [shift Key.G] GoToBottom               // G (shift+g) → go to bottom
  normal [Key.D; Key.D] DeleteLine              // d d → delete line
  normal [leader; Key.F] TriggerCompletion      // <leader> f
  normal [leader; Key.E] Submit                 // <leader> e → eval/submit
  normal [leader; shift Key.R] HardResetSession // <leader> R (shift+r)
  inContext (Pane Sessions) normal [Key.J] SessionNavDown
  inContext (Pane Sessions) normal [Key.K] SessionNavUp
  inContext Completion insert [Key.Tab] AcceptCompletion
  inContext Completion insert [Key.Escape] DismissCompletion
  insert [ctrl Key.Enter] Submit
  insert [ctrl Key.Z] Undo
  insert [ctrlShift Key.Z] Redo
]

// Collect all Results — fail fast with all errors, not just the first
let validatedBindings : Result<KeyBinding list, string list> =
  KeyMapDsl.validateAll bindings
```

The DSL functions (`normal`, `insert`, `ctrl`, `shift`, `leader`, `inContext`) return `Result<KeyBinding, string>`. Validation happens at the definition site — if a binding is malformed (e.g., empty key list passed to `normal`), the error message points to the DSL line, not to the trie builder. `KeyMapDsl.validateAll` collects all `Result` values, returning either all valid bindings or all errors. `Key.D` is always lowercase `d`; uppercase `D` is `shift Key.D`. All modifiers (shift, ctrl, alt) work the same way, and the `Key` DU has no case/char ambiguity.

### Status Bar Integration

Mode indicator rendered by shared `Screen` module (works in TUI + Raylib):
```
┌─ Editor ─────────────────────────── -- NORMAL -- ─┐
```

When no `SwitchInputMode` bindings are present (non-modal mode), no mode indicator shown.

Pending sequence shown (like Neovim's `showcmd` / `which-key` — persists until resolved, cancelled, or backstepped):
```
┌─ Editor ──────────────────── d_ ── -- NORMAL -- ─┐
```

### Which-Key Display

When the user enters a pending prefix, a which-key overlay appears showing all available continuations from the current position in the trie. This is critical for discoverability — without timeouts, the user needs to *see* what's available.

**Behavior:**
- Appears after the first key of a multi-key sequence lands in `Pending`
- Shows all children of the current trie node, grouped by category
- Updates live as more keys are pressed (narrows the visible options)
- Disappears on `Matched`, `NoMatch`, `Cancelled`, or `Backstepped` to empty

**Example:** User presses `<Space>` (leader) in Normal mode:
```
┌─ Which Key ─────────────────────────────────────────┐
│ <Space> …                                           │
│                                                     │
│  f → find/completion    e → eval/submit             │
│  r → reset session      R → hard reset session      │
│  s → toggle sessions    n → new session             │
│  c → clear output                                   │
└─────────────────────────────────────────────────────┘
```

**Example:** User presses `d` in Normal mode:
```
┌─ Which Key ──────────────┐
│ d …                      │
│                          │
│  d → delete line         │
└──────────────────────────┘
```

**Implementation:**
- The which-key display is a `KeyContext.Overlay "which_key"` — it gets its own context override bindings (e.g., Escape to dismiss)
- Content is derived from the trie: given the current `MatcherState.Pending`, walk the trie to the current node and enumerate `Children`
- Each child is rendered as `key → action description` using a display name on `UiAction`
- Rendered by the shared `Screen` module (works in TUI + Raylib)
- Position: bottom of the editor pane, overlaying content (like Neovim's which-key popup)
- Collapsible: if there are too many options, show categories first (like which-key.nvim groups)

### Keymap Debug Mode

When a binding doesn't fire and you're wondering why, you need to answer: "what context stack was active when I pressed that key?" A debug mode logs `KeymapContext` and `KeyMatchResult` for each key press, visible in the diagnostics pane.

**What gets logged (per key press):**
- The `KeyInput` that was pressed (key + modifier)
- The full `KeymapContext` at the time: `Mode` + `Contexts` stack (ordered)
- Which context trie was checked, in order (showing the resolution walk)
- The `KeyMatchResult` returned: `Matched action`, `Pending [keys]`, `NoMatch`, etc.
- If `Matched`: which scope it matched in (`Global` vs `InContext ctx`)

**Example diagnostics output:**
```
[keymap] Key.D None | Mode=Normal Contexts=[Pane Sessions] | checked: Pane Sessions → miss, Global → Pending [d]
[keymap] Key.D None | Mode=Normal Contexts=[Pane Sessions] | checked: Pane Sessions → Matched DeleteLine (InContext Pane Sessions)
```

**Implementation:**
- Controlled by a flag in the Elm model (toggled via a keybinding or command)
- Log entries are structured data (`KeyDebugEntry` record), not ad-hoc strings — the diagnostics pane formats them
- Zero cost when disabled — the `SequenceMatcher.feed` function is pure and doesn't log; the debug wrapper in the Elm `update` function captures input/output around the call
- Visible in the existing diagnostics pane, same rendering in TUI + Raylib

### Migration Path

1. New `KeyMap2` type lives alongside old `KeyMap` temporarily
2. `KeyMap.defaults` converted to `KeyMap2` equivalent
3. Both TUI and Raylib switch to `SequenceMatcher`
4. Old `KeyMap` type deprecated, removed after migration
5. Existing `config.fsx` keybinding format extended for sequences

## Implementation Plan

### Phase 1: Proved Refactor (no behavior change)
Port existing `KeyMap.defaults` into the new `KeyMap2` type system. Everything stays in Insert mode. No sequences, no modes, no matcher. Prove the new types work by showing the old and new systems produce identical results.

- [ ] Create `SageFs.Core/KeyMap.fs` with core types (`Key`, `Modifier`, `KeyInput`, `KeySequence`, `InputMode`, `KeyContext`, `BindingScope`, `KeyBinding`, `KeyTrieNode`, `ScopedKeyMap`, `KeyMap2`, `KeyMapBuildError`)
- [ ] Port all `KeyMap.defaults` single-chord bindings to `KeyBinding list` (all in `Insert` mode, `Scope = Global`, no mode transitions)
- [ ] Implement `KeyMap2.build : KeyBinding list -> Result<KeyMap2, KeyMapBuildError list>` with prefix-collision validation
- [ ] Wire into both TUI and Raylib: replace `mapKeyWith` with new type lookup (single-key only, no sequences yet)
- [ ] Remove old `KeyMap` type once both backends use `KeyMap2`
- [ ] **Property tests — proved equivalence:**
  - For every `ConsoleKeyInfo` input, old `KeyMap.defaults` lookup and new `KeyMap2` lookup produce the same `UiAction option`
  - Round-trip: `KeyBinding list → KeyTrieNode → lookup` matches flat `Map` lookup for all single-key bindings
  - `KeySequence` is always non-empty (cannot construct empty)
  - `KeyTrieNode` built from N bindings contains exactly N leaf actions
  - Context overrides resolve before global for every (context, key) pair
  - No binding is silently dropped during trie construction (count in = count out)
  - If a sequence is a strict prefix of another in the same (mode, scope), `KeyMap2.build` returns `Error` with `AmbiguousPrefix`
  - If two bindings have identical (mode, scope, sequence), `KeyMap2.build` returns `Error` with `DuplicateBinding`

### Phase 2: Sequence Matcher
Add the pure `SequenceMatcher` and multi-key sequences. Still no vim modes — sequences work in Insert mode (e.g., Ctrl-chord sequences that happen to be multi-key).

- [ ] Implement `SequenceMatcher` as pure functions (feed/cancel/backstep returning `KeyMatchResult * MatcherState`)
- [ ] Add `MatcherState` to Elm model
- [ ] Wire `SequenceMatcher.feed` into both TUI and Raylib frame loops
- [ ] **Property tests — matcher correctness:**
  - `feed` with exact match (no children) always returns `Matched` and clears pending
  - `feed` with prefix match always returns `Pending` with accumulated keys
  - `feed` with dead end always returns `NoMatch` and clears pending
  - `cancel` always returns `Cancelled` and empty state regardless of pending
  - `backstep` removes exactly one key; backstep on empty pending returns empty `Backstepped`
  - `feed` is a pure fold: same `(state, key)` always produces same `(result, state')`
  - No key sequence of length N requires more than N calls to `feed` to resolve
  - Context stack resolution: most-specific context wins, falls back to global

### Phase 3: Vim Modes + Bindings
Add `InputMode` switching, Normal/Insert/Visual/Command mode bindings, leader key.

- [ ] Add `SwitchInputMode`, `CopySelection` to UiAction
- [ ] Add `UiAction.displayName` for which-key
- [ ] Add Normal/Insert/Visual mode default bindings
- [ ] Add leader-key bindings (resolved to physical key at construction)
- [ ] Add context-specific bindings (pane overrides, completion, menus)
- [ ] **Property tests — mode system:**
  - Mode transitions are themselves keybindings (no hardcoded transitions)
  - Every mode has at least one binding that transitions out of it (no mode traps)
  - Insert mode falls through to char insertion for unbound printable keys
  - Normal/Visual/Command modes ignore unbound keys silently
  - Leader sequences resolve identically regardless of which physical key is configured as leader

### Phase 4: Which-Key + UI Polish
Add which-key overlay, status bar mode indicator, pending sequence display.

- [ ] Which-key overlay: trie node enumeration → `key → action` display
- [ ] Which-key: render in shared `Screen` module (TUI + Raylib)
- [ ] Which-key: show on `Pending`, hide on `Matched`/`NoMatch`/`Cancelled`
- [ ] Which-key: live narrowing as more keys are pressed
- [ ] Status bar: mode indicator + persistent pending sequence display
- [ ] Pending sequence cancel (Escape) and backstep (Backspace) visual feedback
- [ ] Keymap debug mode: `KeyDebugEntry` record type, toggle keybinding, diagnostics pane rendering
- [ ] Keymap debug mode: log `KeymapContext` + context resolution walk + `KeyMatchResult` per key press
- [ ] **Snapshot tests:** which-key renders expected content for known trie states

### Phase 5: Config + DSL
- [ ] Type-safe DSL for keybinding definitions (`normal`, `insert`, `inContext`, `shift`, `ctrl`, etc.) — each returning `Result<KeyBinding, string>`
- [ ] `KeyMapDsl.validateAll : Result<KeyBinding, string> list -> Result<KeyBinding list, string list>` for collecting all errors
- [ ] `Key.tryParse`, `KeyContext.tryParse` for string config boundaries (alongside existing `InputMode.tryParse`)
- [ ] Extend config file parser for sequence format (all parsing via `tryParse` functions, `Result` throughout)
- [ ] Non-modal preset: all bindings in Insert mode, no `SwitchInputMode` actions
- [ ] Leader key configuration
- [ ] **Property tests — config round-trip:**
  - DSL-constructed bindings and string-parsed bindings produce identical `KeyMap2` for the same logical config
  - Invalid config strings produce `Result.Error` with descriptive message, never crash
  - DSL functions return `Error` for malformed inputs (empty key list, invalid combinations)
  - `Key.tryParse` round-trips: `Key.tryParse (sprintf "%A" key) = Ok key` for all `Key` cases
  - `KeyContext.tryParse` round-trips: `KeyContext.tryParse (sprintf "%A" ctx) = Ok ctx` for all `KeyContext` cases

## Notes
- `KeyMap2` name is temporary — will become `KeyMap` when old one is removed
- Leader key is `Space` by default, configurable
- Timeout is 300ms by default, configurable  → **REMOVED**: No timeouts. Sequences persist until completed, cancelled (Escape), or backstepped (Backspace). No ambiguous prefixes allowed.
- Pane-specific bindings are one kind of context override — menus, dialogs, completion, plugins, etc. all use the same `KeyContext` mechanism
- Ctrl+C copies selection — bound in all modes via the keymap system, not hardcoded. No special-case bindings outside the keymap.

## Expert Review Panel (Round 2 — Fresh Review)

### Mark Seemann (ploeh.dk) — Functional Architecture & Composability

**Overall:** This is a well-designed functional architecture. The pure `SequenceMatcher` composing into an Elm update loop is exactly right — state flows through the system as data, not as side effects. A few observations.

**The `MatcherState` is minimal and correct.** Just `{ Pending: KeyInput list }` — no timestamps, no flags, no mutable fields. This is what a fold accumulator should look like. The `feed` signature `KeyMap2 -> KeymapContext -> MatcherState -> KeyInput -> KeyMatchResult * MatcherState` is a textbook pure state transition. I'd write property tests for this all day.

**`KeyContext` as a DU with a context stack is elegant.** The `Contexts: KeyContext list` ordered most-specific-first means resolution is a simple fold over the list — try each context's override trie, fall back to global. This is the [Composite pattern](https://blog.ploeh.dk/2024/06/24/a-restaurant-example-of-the-composite-pattern/) applied to keybindings. The fact that `Completion` stacks on top of `Pane Editor` is exactly how real UIs work, and the type makes it explicit.

**One concern: `KeyBinding` still has `Context: KeyContext option`.** The `Option` here is doing double duty — `None` means "global" and `Some ctx` means "override." You've addressed this structurally in `ScopedKeyMap` (where `Global` and `ContextOverrides` are separate fields), but `KeyBinding` — the *input* to the trie builder — still uses `Option`. This means the trie builder has to know the `None = global` convention. Consider a small DU:

```fsharp
type BindingScope =
  | Global
  | InContext of KeyContext
```

Then `KeyBinding.Scope: BindingScope` makes the intent unambiguous at every layer, not just in the compiled trie.

**The "no ambiguous prefixes" rule is a strong invariant.** It should be enforced at trie construction time — if someone defines both `d` as a standalone binding and `d d` as a sequence in the same mode, the builder should return a `Result.Error`, not silently shadow one with the other. This is a property test: "for any `KeyBinding list`, if a sequence is a strict prefix of another sequence in the same scope, construction fails with a descriptive error."

**Grade: A-** — Pure, composable, testable. The `BindingScope` refinement and prefix-collision validation at construction are the remaining gaps.

---

### Scott Wlaschin (FSharpForFunAndProfit.com) — Domain Modeling & Type-Driven Design

**Overall:** Excellent domain modeling. The types are doing serious work here — illegal states are well-guarded, and the type-safe DSL is exactly the right approach for an F# tool.

**The `Key` DU is a joy.** A closed set of physical keys where typos are compile errors. `shift Key.D` for uppercase, consistent with `ctrl Key.Z` — the modifier system is orthogonal and composable. This is "making illegal states unrepresentable" applied to keyboard input. No more `Key('d')` vs `Key('D')` confusion.

**`KeySequence = { First: KeyInput; Rest: KeyInput list }` — non-empty by construction.** Perfect. Every function that touches a sequence knows it has at least one key. No `List.head` exceptions hiding in the matcher.

**The `Modifier` DU enumerates all combinations explicitly.** This is fine for 3 modifiers (8 combinations), but it's worth noting this is a design choice — an alternative is `Modifier Set` (a set of flags). The explicit DU has the advantage that pattern matching is exhaustive and there's no "invalid combination" state. The disadvantage is it doesn't compose — you can't write `addModifier Ctrl existing`. At your scale (keymaps are defined once, matched many times), the explicit DU is the right call.

**`KeyMatchResult` carries data where it matters.** `Pending of KeyInput list` means the which-key overlay gets its data directly from the match result — no shadow state, no separate "what's pending?" query. `Backstepped of KeyInput list` tells the caller what remains after removal. The `Cancelled` case carries nothing because there's nothing left. Each case carries exactly what its consumer needs.

**The DSL design is strong but I'd refine one thing.** The DSL functions should return `Result<KeyBinding, string>` rather than `KeyBinding` directly. Why? Because the DSL is where you validate invariants: "this sequence doesn't conflict with an existing prefix." If you defer validation to the trie builder, the error messages lose locality — they can't tell you *which DSL line* caused the conflict. Fail at the point of definition, not the point of compilation.

**`InputMode.tryParse` returning `Result` is correct.** I'd extend the same pattern to `KeyContext.tryParse` and `Key.tryParse` for the string-based config format. Every boundary where strings enter the type system should be a `Result`-returning function.

**Grade: A** — The type design is mature and well-considered. Minor refinements around DSL validation and parse functions for all types at the string boundary.

---

### Casey Muratori (Computer Enhance) — Performance & Latency

**Overall:** The timeout removal fixes the critical latency issue. The remaining design is appropriate for the scale. A few practical points.

**No timeouts, no ambiguous prefixes — this is the right call.** Every key press either matches instantly (leaf node), enters pending (has children), or misses. Zero added latency on any input path. The user's perception is always "I pressed a key and something happened immediately or I'm clearly in a sequence." Combined with the which-key overlay, there's no guessing.

**The trie earns its place because of which-key.** I previously said a flat list would be simpler. With the which-key overlay, the trie's structure is load-bearing — "enumerate children of current node" is O(1) on a trie and O(n) with filtering on a flat list. The trie is the right data structure for this feature set.

**`Map<KeyInput, KeyTrieNode>` for children is still heavier than it needs to be.** At 5-10 children per node (typical for a leader key's children), a sorted array would give you better cache locality. But at this scale, you'll never measure the difference — I mention it only so you don't reach for `Map` reflexively in hot paths elsewhere in the codebase. For the keymap, it's fine.

**The context stack walk concerns me slightly.** `Contexts: KeyContext list` means for every key press, you potentially walk N context tries before falling back to global. With N=2-3 (typical: `[Completion; Pane Editor]`), this is nothing. But if someone stacks 10 contexts via plugins, you're doing 10 trie lookups per key press. Probably still microseconds, but worth a comment in the code: "context stack should be kept shallow."

**The `Key` DU conversion from platform input needs to be fast.** Every key press from the OS (ConsoleKeyInfo on TUI, KeyboardKey on Raylib) gets converted to `KeyInput` before matching. This conversion function runs on every keystroke. Make sure it's a direct mapping (match expression or lookup table), not going through string parsing. At human typing speed (worst case 10 keys/second), it genuinely doesn't matter — but it's the kind of thing that's easy to get right and annoying to debug if wrong.

**Grade: A-** — Clean design, no latency issues, appropriate data structures. Keep context stacks shallow.

---

### Ryan Fleury (RAD Debugger) — Systems Architecture & Practical Engineering

**Overall:** This is a solid, well-layered system. The context generalization is particularly good — I've seen too many codebases with ad-hoc "but this pane is special" branches scattered everywhere.

**The context stack is the right abstraction.** `[Completion; Pane Editor]` — most-specific first, walk until match, fall back to global. This is exactly how focus/input routing works in serious UI frameworks. When you add a new UI element (search bar, context menu), you push a context onto the stack. When it dismisses, you pop it. The keymap system doesn't need to know what these UI elements *are* — it just resolves bindings from the stack. Clean separation.

**Mode ownership is now implicit but correct.** `InputMode` lives in the Elm model, flows into `KeymapContext.Mode` for lookup, and gets updated via `SwitchInputMode` actions through `update`. The Elm architecture makes ownership unambiguous — the model owns it, `update` changes it, `view` reads it. This is better than explicit documentation about "who owns mode" because the architecture itself enforces it.

**The proved refactor (Phase 1) is the most important phase.** Property tests proving old↔new equivalence mean you can swap the type system with confidence. "For every `ConsoleKeyInfo`, both systems return the same `UiAction option`" — that's the test that lets you sleep at night. Everything after Phase 1 is additive.

**Feature parity across TUI and Raylib in the same phase is correct for this codebase.** The shared layer (`KeyMap2`, `SequenceMatcher`, `Screen`) does the heavy lifting. Backend-specific code is just `PlatformKeyInfo → KeyInput` conversion — a small mapping function. Staggering would mean maintaining two input pipelines, and that's strictly more complexity than one migration.

**One thing I'd add: debug/inspect tooling.** When a binding doesn't fire and you're wondering why, you need to answer "what context stack was active when I pressed that key?" Add a debug mode that logs `KeymapContext` and `KeyMatchResult` for each key press — visible in the diagnostics pane. This is cheap to implement and invaluable for debugging context resolution order.

**Grade: A** — Clean architecture, appropriate layering, good migration strategy. Add debug logging for context resolution.

---

### John Carmack — Simplicity, Pragmatism & Shipping

**Overall:** This is a significantly more mature design than most keymap systems I've seen in editor projects. The phased implementation with property tests at each gate is disciplined engineering.

**The five-phase plan is well-structured.** Each phase is independently shippable, each has its own test suite, and each builds on proven foundation. Phase 1 (proved refactor) is particularly good — you're de-risking the type migration before adding any new features. Too many projects skip this step and end up debugging type system issues and feature logic simultaneously.

**"Vim-inspired, not vim" needs to be visible in the UI.** When the user enables modal mode, don't call it "vim mode" — call it "modal mode" or "keyboard mode." The moment you say "vim," every user will file a bug that `ciw` doesn't work. Set expectations in the UI, not just in developer docs.

**The error recovery is well-specified.** `NoMatch` discards the pending buffer, Backspace gives explicit correction, Escape cancels. Combined with the which-key overlay showing available continuations, the user always knows where they are and how to get out. This is better UX than vim's error recovery, which is learned through muscle memory and suffering.

**The `.fsx` config is appropriate for this audience.** F# developers running an F# REPL tool — executable config is a feature. The type-safe DSL on top makes it feel native rather than scripty.

**The which-key overlay turns a potential UX problem into a feature.** No timeouts means sequences could feel "stuck" — but the which-key overlay makes the pending state visible and navigable. It transforms "I'm waiting and nothing is happening" into "I can see exactly what my options are." This is the right solution.

**One thought on the `Key` DU: you'll need to handle platform-specific keys eventually.** Some keyboards have media keys, numpad keys, or platform-specific keys (Windows key, Command key). The closed DU means you'll need to update it when these come up. Consider whether a catch-all case (`| Other of string`) is worth the tradeoff — it breaks exhaustive matching but prevents the DU from blocking platform support. My instinct says keep it closed and add cases as needed — you want compile errors when a new key appears, not silent fallthrough.

**Grade: A-** — Well-engineered, pragmatic, shippable. Name the mode carefully in the UI. Consider platform key extensibility.

---

### Consensus Summary

| Concern | Raised By | Severity |
|---|---|---|
| `KeyBinding.Context` uses `Option` — consider `BindingScope` DU | Seemann | ~~Medium~~ **ADOPTED** |
| No-ambiguous-prefix rule should be enforced at trie construction with `Result.Error` | Seemann | ~~Medium~~ **ADOPTED** |
| DSL functions should return `Result` to fail at definition point, not compilation | Wlaschin | ~~Medium~~ **ADOPTED** |
| Extend `tryParse` pattern to `KeyContext` and `Key` for string config boundaries | Wlaschin | ~~Low~~ **ADOPTED** |
| Keep context stacks shallow — comment the expectation in code | Muratori | ~~Low~~ **ADOPTED** |
| Platform key conversion must be a direct mapping, not string parsing | Muratori | ~~Low~~ **ADOPTED** |
| Add debug/inspect logging for context resolution (diagnostics pane) | Fleury | ~~Medium~~ **ADOPTED** |
| Don't call it "vim mode" in the UI — use "modal mode" or "keyboard mode" | Carmack | ~~Medium~~ **ADOPTED** |
| Consider platform key extensibility (media keys, numpad) — closed DU may need cases | Carmack | ~~Low~~ **ADOPTED** |
| `Modifier` DU is explicit enumeration vs `Set` — fine at this scale, note the tradeoff | Wlaschin | ~~Informational~~ **NOTED** |
