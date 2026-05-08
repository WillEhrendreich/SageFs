/-!
  SageFsError.lean
  🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*
  Source: `SageFs.Core/SageFsError.fs`

  Formalises the **error classification** logic of `SageFsError`:
  - `isClientError`, `isServerError`, `isGatewayError`, `isInfraError`
  - `toHttpStatus`
  - `toLogSeverity`

  ## Model

  `SageFsError` is modelled as a pure tag enum (`ErrorTag`) — the 30 constructor
  names with **all payload fields abstracted away**.  All category/severity
  functions depend only on the constructor tag, not on any payload value.
  This allows exhaustive case-splitting proofs and, in principle, `decide`.

  ## Key properties proved

  - **Mutual exclusion** (6 pairs): no error belongs to two categories.
  - **Partition completeness**: every error belongs to exactly one category.
  - **HTTP status consistency** (4 directions): category ↔ HTTP status range.
  - **Log severity**: infra errors are always `Critical`; gateway errors are
    never `Information`.

  ## Abstractions / omissions

  - All payload fields (strings, ints, floats, exn) are erased — the model
    checks structural/constructor-level properties only.
  - `LogLevel` is modelled as a 4-level enum (Critical ≥ Error_ ≥ Warning ≥ Info).
    .NET's `Microsoft.Extensions.Logging.LogLevel` has the same ordering.
  - The `describe`, `suggestedAction`, `toJson`, and `describeForAgent` functions
    are not modelled here; they have no invariants worth formalising at this level.

  No Mathlib. Pure Lean 4 stdlib only (network firewalled in CI).
  Source: SageFs.Core/SageFsError.fs
-/

namespace SageFsError

-- ── Types ─────────────────────────────────────────────────────────────────────

/-- Mirrors the 30-case `SageFsError` DU with all payload fields erased.
    Every constructor corresponds 1-to-1 with an F# `SageFsError` case.
    Payload erasure is safe because all classification functions depend only
    on the constructor tag. -/
inductive ErrorTag where
  -- Tool availability
  | ToolNotAvailable
  -- Session operations
  | SessionNotFound
  | NoActiveSessions
  | AmbiguousSessions
  | SessionCreationFailed
  | SessionStopFailed
  | SessionSwitchFailed
  -- Worker communication
  | WorkerCommunicationFailed
  | WorkerSpawnFailed
  | WorkerTimeout
  | WorkerHttpError
  | PipeClosed
  -- Eval/reset/check operations
  | EvalFailed
  | ResetFailed
  | HardResetFailed
  | ScriptLoadFailed
  | CheckFailed
  | CompletionFailed
  | CancelFailed
  -- Warm-up
  | WarmupOpenFailed
  | WarmupContextFailed
  -- Hot reload
  | HotReloadFailed
  | HotReloadStateError
  -- Restart policy
  | RestartLimitExceeded
  -- Infrastructure
  | DaemonStartFailed
  | DaemonNotRunning
  | PortInUse
  | SseConnectionError
  | JsonParseError
  | Unexpected
  deriving DecidableEq, Repr

/-- Abstract log severity: mirrors `.NET LogLevel` (Critical ≥ Error_ ≥ Warning ≥ Info). -/
inductive LogSev where
  | Critical
  | Error_
  | Warning
  | Info
  deriving DecidableEq, Repr

-- ── Classification functions ───────────────────────────────────────────────────

/-- Client errors (4xx): bad request from the caller. -/
def isClientError : ErrorTag → Bool
  | .ToolNotAvailable | .SessionNotFound | .NoActiveSessions
  | .AmbiguousSessions | .DaemonNotRunning | .JsonParseError => true
  | _ => false

/-- Server errors (500): internal SageFs system failures. -/
def isServerError : ErrorTag → Bool
  | .SessionCreationFailed | .SessionStopFailed | .SessionSwitchFailed
  | .EvalFailed | .ResetFailed | .HardResetFailed | .ScriptLoadFailed
  | .CheckFailed | .CompletionFailed | .CancelFailed | .WarmupOpenFailed
  | .WarmupContextFailed | .HotReloadFailed | .HotReloadStateError
  | .DaemonStartFailed | .Unexpected => true
  | _ => false

/-- Gateway errors (502/504): upstream worker unreachable or timed out. -/
def isGatewayError : ErrorTag → Bool
  | .WorkerCommunicationFailed | .WorkerSpawnFailed | .WorkerTimeout
  | .WorkerHttpError | .PipeClosed | .SseConnectionError => true
  | _ => false

/-- Infrastructure errors (409): system-level conflicts. -/
def isInfraError : ErrorTag → Bool
  | .PortInUse | .RestartLimitExceeded => true
  | _ => false

/-- HTTP status code for each error tag.
    Mirrors `SageFsError.toHttpStatus` in `SageFsError.fs`. -/
def toHttpStatus : ErrorTag → Nat
  | .SessionNotFound | .NoActiveSessions | .DaemonNotRunning => 404
  | .AmbiguousSessions | .JsonParseError | .ToolNotAvailable => 400
  | .PortInUse | .RestartLimitExceeded => 409
  | .WorkerTimeout => 504
  | .WorkerHttpError | .WorkerCommunicationFailed | .PipeClosed
  | .WorkerSpawnFailed | .SseConnectionError => 502
  | _ => 500   -- all server errors

/-- Log severity for each error tag.
    Mirrors `SageFsError.toLogLevel` in `SageFsError.fs`. -/
def toLogSev : ErrorTag → LogSev
  | .DaemonStartFailed | .PortInUse | .RestartLimitExceeded => .Critical
  | .WorkerSpawnFailed | .WorkerCommunicationFailed | .WorkerTimeout
  | .WorkerHttpError | .PipeClosed | .SessionCreationFailed | .EvalFailed
  | .ResetFailed | .HardResetFailed | .ScriptLoadFailed | .HotReloadFailed
  | .SseConnectionError | .Unexpected => .Error_
  | .SessionStopFailed | .SessionSwitchFailed | .CheckFailed | .CompletionFailed
  | .CancelFailed | .WarmupOpenFailed | .WarmupContextFailed
  | .HotReloadStateError | .JsonParseError => .Warning
  | _ => .Info   -- ToolNotAvailable, SessionNotFound, NoActiveSessions,
                 -- AmbiguousSessions, DaemonNotRunning

-- ── #check sanity ─────────────────────────────────────────────────────────────

#check @isClientError
#check @toHttpStatus

-- ── Mutual exclusion theorems ─────────────────────────────────────────────────

/-- Client errors and server errors are disjoint. -/
theorem client_not_server (e : ErrorTag) :
    ¬(isClientError e = true ∧ isServerError e = true) := by
  cases e <;> simp [isClientError, isServerError]

/-- Client errors and gateway errors are disjoint. -/
theorem client_not_gateway (e : ErrorTag) :
    ¬(isClientError e = true ∧ isGatewayError e = true) := by
  cases e <;> simp [isClientError, isGatewayError]

/-- Client errors and infra errors are disjoint. -/
theorem client_not_infra (e : ErrorTag) :
    ¬(isClientError e = true ∧ isInfraError e = true) := by
  cases e <;> simp [isClientError, isInfraError]

/-- Server errors and gateway errors are disjoint. -/
theorem server_not_gateway (e : ErrorTag) :
    ¬(isServerError e = true ∧ isGatewayError e = true) := by
  cases e <;> simp [isServerError, isGatewayError]

/-- Server errors and infra errors are disjoint. -/
theorem server_not_infra (e : ErrorTag) :
    ¬(isServerError e = true ∧ isInfraError e = true) := by
  cases e <;> simp [isServerError, isInfraError]

/-- Gateway errors and infra errors are disjoint. -/
theorem gateway_not_infra (e : ErrorTag) :
    ¬(isGatewayError e = true ∧ isInfraError e = true) := by
  cases e <;> simp [isGatewayError, isInfraError]

-- ── Partition completeness ─────────────────────────────────────────────────────

/-- **Partition theorem**: every error belongs to exactly one category.
    This ensures the four predicates cover the entire constructor space
    without overlap — they form a true partition of `ErrorTag`. -/
theorem error_category_partition (e : ErrorTag) :
    (isClientError e = true ∧ isServerError e = false ∧
     isGatewayError e = false ∧ isInfraError e = false) ∨
    (isClientError e = false ∧ isServerError e = true ∧
     isGatewayError e = false ∧ isInfraError e = false) ∨
    (isClientError e = false ∧ isServerError e = false ∧
     isGatewayError e = true ∧ isInfraError e = false) ∨
    (isClientError e = false ∧ isServerError e = false ∧
     isGatewayError e = false ∧ isInfraError e = true) := by
  cases e <;> simp [isClientError, isServerError, isGatewayError, isInfraError]

-- ── HTTP status consistency ────────────────────────────────────────────────────

/-- Client errors map to 4xx status codes (400 or 404). -/
theorem client_http_status (e : ErrorTag) (h : isClientError e = true) :
    toHttpStatus e = 400 ∨ toHttpStatus e = 404 := by
  cases e <;> simp_all [isClientError, toHttpStatus]

/-- Gateway errors map to 502 or 504. -/
theorem gateway_http_status (e : ErrorTag) (h : isGatewayError e = true) :
    toHttpStatus e = 502 ∨ toHttpStatus e = 504 := by
  cases e <;> simp_all [isGatewayError, toHttpStatus]

/-- Infra errors always map to 409 Conflict. -/
theorem infra_http_status (e : ErrorTag) (h : isInfraError e = true) :
    toHttpStatus e = 409 := by
  cases e <;> simp_all [isInfraError, toHttpStatus]

/-- Server errors always map to 500 Internal Server Error. -/
theorem server_http_status (e : ErrorTag) (h : isServerError e = true) :
    toHttpStatus e = 500 := by
  cases e <;> simp_all [isServerError, toHttpStatus]

/-- HTTP status is always a valid HTTP error code (4xx or 5xx). -/
theorem http_status_range (e : ErrorTag) :
    toHttpStatus e = 400 ∨ toHttpStatus e = 404 ∨ toHttpStatus e = 409 ∨
    toHttpStatus e = 500 ∨ toHttpStatus e = 502 ∨ toHttpStatus e = 504 := by
  cases e <;> simp [toHttpStatus]

-- ── Reverse direction: HTTP status → category ─────────────────────────────────

/-- 409 status means infra error (converse of `infra_http_status`). -/
theorem http_409_iff_infra (e : ErrorTag) :
    toHttpStatus e = 409 ↔ isInfraError e = true := by
  cases e <;> simp [toHttpStatus, isInfraError]

/-- 504 status means gateway error (WorkerTimeout is the only 504 case). -/
theorem http_504_iff_gateway_timeout (e : ErrorTag) :
    toHttpStatus e = 504 ↔ e = .WorkerTimeout := by
  cases e <;> simp [toHttpStatus]

-- ── Log severity theorems ──────────────────────────────────────────────────────

/-- Infra errors always get the highest log severity. -/
theorem infra_log_critical (e : ErrorTag) (h : isInfraError e = true) :
    toLogSev e = .Critical := by
  cases e <;> simp_all [isInfraError, toLogSev]

/-- Gateway errors are never logged at the lowest (Info) severity. -/
theorem gateway_not_info (e : ErrorTag) (h : isGatewayError e = true) :
    toLogSev e ≠ .Info := by
  cases e <;> simp_all [isGatewayError, toLogSev]

/-- Client errors are never logged at the Critical severity. -/
theorem client_not_critical (e : ErrorTag) (h : isClientError e = true) :
    toLogSev e ≠ .Critical := by
  cases e <;> simp_all [isClientError, toLogSev]

-- ── Cross-cutting: all HTTP status codes used are actually assigned ────────────

/-- 400 is assigned (to `ToolNotAvailable`). -/
theorem status_400_exists : toHttpStatus .ToolNotAvailable = 400 := by
  simp [toHttpStatus]

/-- 404 is assigned (to `SessionNotFound`). -/
theorem status_404_exists : toHttpStatus .SessionNotFound = 404 := by
  simp [toHttpStatus]

/-- 409 is assigned (to `PortInUse`). -/
theorem status_409_exists : toHttpStatus .PortInUse = 409 := by
  simp [toHttpStatus]

/-- 500 is assigned (to `EvalFailed`). -/
theorem status_500_exists : toHttpStatus .EvalFailed = 500 := by
  simp [toHttpStatus]

/-- 502 is assigned (to `PipeClosed`). -/
theorem status_502_exists : toHttpStatus .PipeClosed = 502 := by
  simp [toHttpStatus]

/-- 504 is assigned (to `WorkerTimeout`). -/
theorem status_504_exists : toHttpStatus .WorkerTimeout = 504 := by
  simp [toHttpStatus]

end SageFsError
