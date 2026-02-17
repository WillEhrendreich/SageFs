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
