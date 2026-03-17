# As-You-Type Live Testing — Visual Studio Extension

This document describes the **current shipped Visual Studio behavior** for compiled-project
"test as you type" buffer syncing.

The earlier `evaluate-scope` design is no longer the contract Visual Studio implements. Visual
Studio now mirrors the same truthful compiled-buffer bridge already landed in VS Code and Neovim.

## Current Contract

Visual Studio sends debounced unsaved buffer content to the daemon through:

```text
POST /api/sessions/{sid}/buffer-changed
```

Request body:

```json
{
  "filePath": "C:\\Code\\Repos\\SageFs\\src\\Domain.fs",
  "content": "module Domain\n..."
}
```

The daemon routes that payload into `SageFsMsg.BufferContentChanged`, which preserves
keystroke-triggered live-testing semantics without pretending the file was saved.

## What Visual Studio Does

The Visual Studio client now has three pieces:

1. `SageFs.VisualStudio.Core\BufferChangeRequest.fs`
   - pure compiled-file filter (`.fs`, `.fsi`)
   - directory-boundary-aware session ownership resolution
   - request construction

2. `SageFs.VisualStudio.Core\SageFsClient.fs`
   - `PostBufferChangedAsync`

3. `SageFs.VisualStudio\Services\FSharpBufferChangedListener.cs`
   - `ITextViewChangedListener`
   - per-file debounce
   - live session lookup at send time
   - silent refusal on no-match / ambiguity

## Behavioral Rules

Visual Studio follows these guardrails:

- **Compiled source files only**
  - `.fs` -> included
  - `.fsi` -> included
  - `.fsx` -> excluded

- **File-backed documents only**
  - non-file buffers are ignored

- **300ms debounce**
  - debounce is tracked per file so edits in one document do not cancel another document's buffer sync

- **Live session lookup at send time**
  - after the debounce completes, the extension calls `/api/sessions`
  - ownership is resolved from the current live session list, not from a stale cached guess

- **Unique owner required**
  - if exactly one session working directory contains the file, the extension posts to that session
  - if zero sessions match, nothing is posted
  - if multiple sessions match, nothing is posted

- **No noisy hot-path UX**
  - ambiguity and no-match are intentionally silent for now
  - the extension does not claim the buffer was analyzed when routing is uncertain

## Why This Shape

This is intentionally conservative.

Visual Studio should not guess which session owns a buffer, and it should not silently route
unsaved compiled content to the wrong session. The smallest honest behavior is:

1. debounce
2. query live sessions
3. require a unique owner
4. post to `buffer-changed`

That keeps the client thin and keeps the truth about test execution inside the daemon's existing
session-scoped live-testing pipeline.

## Validation

The Visual Studio buffer bridge is validated at two layers:

- pure core contract:
  - `dotnet test .\sagefs-vs\SageFs.VisualStudio.Core.Tests\SageFs.VisualStudio.Core.Tests.fsproj --no-restore -nologo`

- host extension build:
  - `dotnet build .\sagefs-vs\SageFs.VisualStudio\SageFs.VisualStudio.csproj --no-restore -nologo`

Focused tests cover:

- unique ownership
- ambiguous ownership refusal
- `.fsi` inclusion
- `.fsx` exclusion
- prefix-neighbor false-positive rejection
- JSON payload shape
- session-scoped `buffer-changed` route construction

## Future Follow-Up

Potential future improvements remain deliberately separate from this first truthful slice:

- optional non-noisy diagnostics/debug surfacing for ambiguous ownership
- richer branch-coverage rendering parity in the Visual Studio client
- broader UX polish around live-testing status while unsaved buffers are in flight
