# SageFsError — Informal Specification

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*  
> Source: `SageFs.Core/SageFsError.fs`

---

## Purpose

`SageFsError` is the single unified error type for the entire SageFs system.  
Every `Result<_, SageFsError>` across all layers uses this one discriminated union.

The module provides:
1. **Classification predicates**: `isClientError`, `isServerError`, `isGatewayError`, `isInfraError`
2. **HTTP status mapping**: `toHttpStatus` — for serving errors over HTTP
3. **Log severity mapping**: `toLogLevel` — for structured logging

---

## Preconditions

- Input is any value of type `SageFsError`.
- No preconditions on the payload fields of each case.

---

## Postconditions

### `isClientError`
Returns `true` iff the error was caused by a bad request from the caller (4xx):
- `ToolNotAvailable`, `SessionNotFound`, `NoActiveSessions`, `AmbiguousSessions`,
  `DaemonNotRunning`, `JsonParseError`

### `isServerError`
Returns `true` iff the error represents an internal SageFs system failure (5xx):
- `SessionCreationFailed`, `SessionStopFailed`, `SessionSwitchFailed`,
  `EvalFailed`, `ResetFailed`, `HardResetFailed`, `ScriptLoadFailed`,
  `CheckFailed`, `CompletionFailed`, `CancelFailed`, `WarmupOpenFailed`,
  `WarmupContextFailed`, `HotReloadFailed`, `HotReloadStateError`,
  `DaemonStartFailed`, `Unexpected`

### `isGatewayError`
Returns `true` iff the error was caused by a failure in an upstream worker (502/504):
- `WorkerCommunicationFailed`, `WorkerSpawnFailed`, `WorkerTimeout`,
  `WorkerHttpError`, `PipeClosed`, `SseConnectionError`

### `isInfraError`
Returns `true` iff the error is a system-level infrastructure conflict (409):
- `PortInUse`, `RestartLimitExceeded`

### `toHttpStatus`
Maps every error to an HTTP status code:
- 404: `SessionNotFound`, `NoActiveSessions`, `DaemonNotRunning`
- 400: `AmbiguousSessions`, `JsonParseError`, `ToolNotAvailable`
- 409: `PortInUse`, `RestartLimitExceeded`
- 504: `WorkerTimeout`
- 502: `WorkerHttpError`, `WorkerCommunicationFailed`, `PipeClosed`, `WorkerSpawnFailed`, `SseConnectionError`
- 500: all remaining (server errors)

### `toLogLevel`
Maps every error to a log severity level:
- Critical: `DaemonStartFailed`, `PortInUse`, `RestartLimitExceeded`
- Error: `WorkerSpawnFailed`, `WorkerCommunicationFailed`, `WorkerTimeout`, `WorkerHttpError`, `PipeClosed`, `SessionCreationFailed`, `EvalFailed`, `ResetFailed`, `HardResetFailed`, `ScriptLoadFailed`, `HotReloadFailed`, `SseConnectionError`, `Unexpected`
- Warning: `SessionStopFailed`, `SessionSwitchFailed`, `CheckFailed`, `CompletionFailed`, `CancelFailed`, `WarmupOpenFailed`, `WarmupContextFailed`, `HotReloadStateError`, `JsonParseError`
- Information: `ToolNotAvailable`, `SessionNotFound`, `NoActiveSessions`, `AmbiguousSessions`, `DaemonNotRunning`

---

## Invariants

### Category Partition
Every `SageFsError` value belongs to **exactly one** of the four categories.
- Mutual exclusion: no two category predicates are simultaneously true.
- Completeness: at least one category predicate is always true.

### HTTP Status Consistency
- `isClientError e → toHttpStatus e ∈ {400, 404}`
- `isGatewayError e → toHttpStatus e ∈ {502, 504}`
- `isInfraError e → toHttpStatus e = 409`
- `isServerError e → toHttpStatus e = 500`

### Log Severity Consistency
- `isInfraError e → toLogLevel e = Critical`

---

## Edge Cases

- `Unexpected exn` is a server error (500) even when the underlying exception is a user error.
- `DaemonNotRunning` is a client error (404) — the caller should start the daemon.
- `RestartLimitExceeded` is an infra error (409) — the system is in a conflict state.

---

## Examples

| Error | isClient | isServer | isGateway | isInfra | HTTP |
|-------|----------|----------|-----------|---------|------|
| `SessionNotFound "abc"` | ✓ | — | — | — | 404 |
| `EvalFailed "type error"` | — | ✓ | — | — | 500 |
| `WorkerTimeout("s","op",30)` | — | — | ✓ | — | 504 |
| `PortInUse 7000` | — | — | — | ✓ | 409 |
| `PipeClosed` | — | — | ✓ | — | 502 |

---

## Open Questions

1. Should `DaemonNotRunning` be client (404) or infra (503)? Currently client.
2. Are there any plans to add new error variants? If so, will the category assignment be obvious?
3. `Unexpected exn` catches all exceptions — should it be split into separate error subtypes?
