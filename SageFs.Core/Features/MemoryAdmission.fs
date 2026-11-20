/// The switch for the daemon's session-admission memory gate.
///
/// The supervisor has two jobs: shed load (reap dead sessions, stop idle ones) and refuse new
/// sessions when the machine is below its memory floor. In CI the second job is counterproductive.
/// A test daemon shares its machine with external work it cannot reclaim; refusing a session frees
/// none of that memory and only fails the test that asked. The gate's own tier admission
/// (`build/TierPlan.fs`, 8 GiB reserve + recorded peak) is the right place to decide the machine has
/// room, because it sees all tiers together. Shedding stays on in CI — it genuinely frees memory.
/// A user's daemon keeps the default (Protected): its refusal is a real service on a shared desktop.
module SageFs.MemoryAdmission

/// Whether session admission is protected by the memory floor.
[<RequireQualifiedAccess>]
type Mode =
  /// Refuse new sessions below the floor (the default, for a user's daemon).
  | Protected
  /// Never refuse admission; still shed idle/dead sessions (for a CI test daemon).
  | Unrestricted

/// The environment variable that sets it. The gate sets `off` for every tier, inherited by every
/// test daemon it spawns; a user's shell normally leaves it unset.
let environmentVariable = "SAGEFS_MEMORY_ADMISSION"

let defaultMode : Mode = Mode.Protected

/// The spellings for protected/unrestricted. Anything else, empty included, is the default.
let parse (value: string | null) : Mode =
  match value with
  | null -> defaultMode
  | text ->
    match text.Trim().ToLowerInvariant() with
    | "1"
    | "on"
    | "true" -> Mode.Protected
    | "0"
    | "off"
    | "false" -> Mode.Unrestricted
    | _ -> defaultMode

/// Read once, when the daemon starts, through the same snapshot every other env setting uses.
let fromEnvironment () : Mode =
  parse (SageFs.SettingsInForce.read environmentVariable)

/// Apply the mode to a supervisor decision. Protecting keeps it as decided. Going unrestricted
/// strips ONLY the refusal action — reap-dead and stop-idle stay, because those free memory and are
/// the useful half under pressure. Never rewrite Level or Reason: this is an action filter, not a
/// new decision, so a health payload still reports the pressure the machine actually has.
let apply (mode: Mode) (decision: SageFs.MemorySupervisor.Decision) : SageFs.MemorySupervisor.Decision =
  match mode with
  | Mode.Protected -> decision
  | Mode.Unrestricted ->
    { decision with
        Actions =
          decision.Actions
          |> List.filter (fun action ->
            match action with
            | SageFs.MemorySupervisor.ShedAction.RefuseNewSessions _ -> false
            | SageFs.MemorySupervisor.ShedAction.ReapDeadSessions _
            | SageFs.MemorySupervisor.ShedAction.StopIdleSessions _ -> true) }
