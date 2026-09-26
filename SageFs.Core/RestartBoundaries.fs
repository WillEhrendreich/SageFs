namespace SageFs

// ── Declaring which unit is a restart boundary ──────────────────────────
//
// WHY this exists: `RestartScope.UnitScope` is computed, carried and logged,
// but had nothing to ACT on — because SageFs launches a user's app as a
// process and there is no in-process per-unit object to restart. This is the
// opt-in that gives it one, without asking anyone to change their source.
//
// CHOSEN SHAPE, and why it is the least surprising one available. The
// candidates were a DI registration, an `IOptionsMonitor`, and an agent
// mailbox. DI wins because a user already writes that line for reasons that
// have nothing to do with SageFs — declaring "this thing is a singleton" is
// ordinary application code, so adding "and it is a restart boundary" is one
// extra argument on a line they already have. `IOptionsMonitor` was rejected
// because a TYPE change is not a configuration change, and an agent mailbox
// because it requires the actor model, which makes "unit" ambiguous.
//
// THREE PROPERTIES THIS TYPE EXISTS TO KEEP, each of which a naive version
// gets wrong:
//
//  1. PER SESSION. Two apps run at once. A module-level registry would let a
//     boundary in one app silently narrow a restart in the other. The registry
//     carries its session's identity and boundaries never cross.
//  2. ZERO OR ONE, NEVER A GUESS. `RestartScopeFor` returns `None` both when
//     nothing is declared and when several things are, because picking one
//     arbitrarily is the same error class as picking a restart scope
//     arbitrarily — and both strands a live value laid out by the old type.
//  3. AN INERT OPT-IN MUST BE OBSERVABLE. Declaring a boundary changes nothing
//     until a type actually changes, so a user can ask what they declared and
//     what each one holds. An opt-in you cannot inspect is worse than none.
//
// Duplicated declarations of the SAME id are a mistake the user should see, so
// `Declare` refuses a conflicting second `Holds` for an id rather than
// silently letting two types claim one boundary.

open System
open System.Collections.Generic

/// A boundary's stable name — what the user calls it, and what appears in the
/// restart message.
type BoundaryId = string

/// One declared boundary: the unit that can be restarted, and the type whose
/// instances it holds.
type Boundary =
  { Id: BoundaryId
    /// The type whose shape, when it changes, makes a restart scoped to THIS
    /// boundary. Naming a type nothing holds is a user error with a clear
    /// consequence: the restart stays app-wide, which is safe.
    Holds: string }

/// A boundary declaration that conflicts with one already registered.
type DeclarationConflict =
  { Boundary: BoundaryId
    AlreadyHolds: string
    TriedToHold: string }

/// What declaring a boundary produced.
[<RequireQualifiedAccess>]
type Declared =
  | Accepted of boundary: Boundary
  /// The same id already claims a DIFFERENT type. Two types claiming one
  /// boundary would make the restart scope a coin flip.
  | Conflicted of conflict: DeclarationConflict

/// Why a type could not be scoped to a boundary. Separate cases because the
/// USER ACTION differs: one needs a declaration, the other needs a choice.
[<RequireQualifiedAccess>]
type Unscoped =
  /// No boundary declares this type, so a restart must cover the whole app.
  | Undeclared
  /// Several boundaries claim it, so SageFs will not choose between them.
  | Ambiguous of boundaries: BoundaryId list

type private RegistryState =
  { Session: WorkerProtocol.SessionId
    Boundaries: ResizeArray<Boundary> }

/// Per-session boundary registry. See the module comment for the three
/// properties this exists to keep.
type Registry private (state: RegistryState) =

  /// A registry for one session. Boundaries declared here are invisible to
  /// every other session's registry.
  static member For (session: WorkerProtocol.SessionId) =
    Registry({ Session = session; Boundaries = ResizeArray() })

  /// The session this registry belongs to, so a caller holding two of them
  /// can never mix their answers up.
  member _.Session = state.Session

  /// Declare that `id` holds instances of `holds`. Idempotent for a
  /// REPEATED identical declaration, because startup code may legitimately
  /// run twice; a CONFLICTING one is refused.
  member _.Declare (id: BoundaryId) (holds: string) : Declared =
    match state.Boundaries |> Seq.tryFind (fun b -> b.Id = id) with
    | Some existing when existing.Holds = holds ->
      // A repeat of the same claim. Accept it rather than making every caller
      // guard against a double-initialise.
      Declared.Accepted existing
    | Some existing ->
      Declared.Conflicted { Boundary = id; AlreadyHolds = existing.Holds; TriedToHold = holds }
    | None ->
      let boundary = { Id = id; Holds = holds }
      state.Boundaries.Add boundary
      Declared.Accepted boundary

  /// Everything declared, so a user can inspect what they opted into.
  member this.Declared = List.ofSeq state.Boundaries

  /// Every boundary claiming to hold `typeName`.
  member this.BoundariesHolding (typeName: string) =
    this.Declared |> List.filter (fun b -> b.Holds = typeName)

  /// The boundary to scope a restart to, or `None`.
  ///
  /// ZERO or SEVERAL are both `None`, and that is the safety property rather
  /// than a simplification: a wrong scope leaves a live value laid out by the
  /// old type in a half-restarted app, which is strictly worse than a full
  /// restart. `Undeclared` and `Ambiguous` are separated below so the refusal
  /// can SAY which happened.
  member this.RestartScopeFor (typeName: string) =
    match this.BoundariesHolding typeName with
    | [ b ] -> Ok b.Id
    | [] -> Error Unscoped.Undeclared
    | many -> Error(Unscoped.Ambiguous(many |> List.map (fun b -> b.Id)))

  /// `RestartScopeFor` as a plain option, for callers that only need the id.
  member this.TryRestartScopeFor (typeName: string) =
    match this.RestartScopeFor typeName with
    | Ok id -> Some id
    | Error _ -> None

  /// A one-line description per boundary, for a user asking "what did I
  /// declare, and what happens if I change it?".
  member this.Describe (boundary: Boundary) =
    sprintf "'%s' holds %s; changing %s restarts this boundary and leaves the rest running"
      boundary.Id boundary.Holds boundary.Holds
