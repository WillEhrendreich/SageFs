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

/// One declared boundary: the unit that can be restarted, the type whose
/// instances it holds, and — optionally — how to carry those instances when the
/// shape changes.
///
/// `Migrate` is the third reason this is not simply a dictionary of names. A
/// boundary is the one place that ALREADY holds the value, so it is the only
/// place a migration can be performed without the liveness registry holding a
/// strong reference to live state — which would make it report `HeldBy` for an
/// object nothing else uses, the same lie as a reflection probe.
///
/// It is a function field, and that is a cost worth naming: F# records with
/// function fields do not give structural equality. Nothing here compares
/// `Boundary` values — `Declare` compares `Id` and `Holds` field-wise, which is
/// exactly why a repeat of the same claim is recognised — so the cost is
/// theoretical. If a future caller needs `boundary1 = boundary2`, this becomes a
/// DU over (Id, Holds) + a separate hook table.
type Boundary =
  { Id: BoundaryId
    /// The type whose shape, when it changes, makes a restart scoped to THIS
    /// boundary. Naming a type nothing holds is a user error with a clear
    /// consequence: the restart stays app-wide, which is safe.
    Holds: string
    /// How to carry a live instance into the new shape. `None` is the honest
    /// default for every boundary today: the boundary does not know how, so a
    /// type change under it falls back to a rebuild. `None` is NOT "nothing is
    /// live" — that claim belongs to the liveness answer, and conflating them
    /// is how a cheap restart gets granted without evidence.
    Migrate: (obj -> System.Type -> MigrationWorth) option
    /// The LIVE value this boundary holds, and the type it should become.
    ///
    /// `Migrate` answers "if you have a value and a type, is it worth carrying".
    /// This answers the prior question — "do you HAVE them" — and it is separate
    /// because the two fail differently. A missing `Migrate` is the app not
    /// knowing how; a missing `Subject` is the app having nothing to migrate or
    /// not yet knowing what the new type is. Collapsing them would report a
    /// migration opportunity the app never offered, and a boundary that reports
    /// one it cannot honour is worse than one that reports nothing.
    ///
    /// It travels ON the boundary for the same reason `Migrate` does: the
    /// boundary is the only side that holds the app's own cell, so a
    /// worker-side lookup by type name could only ever be a guess about which
    /// loaded type is the live one.
    Subject: (unit -> (obj * System.Type) option) option }

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

/// What an app-side declaration can refuse with, when no worker has published a
/// registry for it to land in.
///
/// A DU rather than an error string, for the reason every refusal in this file
/// is a DU: "no registry" and "conflicted" are different facts with different
/// user actions, and a caller must be able to tell them apart rather than read a
/// message and guess.
type NoRegistry =
  /// No registry has been published, so a declaration here would be invisible to
  /// every restart. Running outside a SageFs worker.
  ///
  /// Named `Unpublished` rather than repeating the type's name, because F#
  /// cannot distinguish `NoRegistry.NoRegistryPublished` from using the TYPE
  /// (FS0800), and a case that cannot be named is a case nobody writes.
  | Unpublished of because: string

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

  /// The registry the APP declares into, published by the worker that reads it.
  ///
  /// The same shape as `HolderRegistry.Current`, and for the same reason. An
  /// app runs INSIDE the worker, so a boundary the app declares and a boundary
  /// the restart reads are the same question asked of two objects — and if they
  /// are two objects, every declaration is invisible and every type change falls
  /// back to a whole-app restart. Measured: `boundaryRegistry` was a local `let`
  /// in `WorkerMain.run`, reachable by nothing.
  ///
  /// `None` means no worker has published one, and that is a real state an app
  /// can detect: running outside SageFs, declaring is a no-op rather than a
  /// silent success, which is the same refusal `holdInCurrent` makes.
  static member val Current: Registry option = None with get, set

  /// Declare that `id` holds instances of `holds`. Idempotent for a
  /// REPEATED identical declaration, because startup code may legitimately
  /// run twice; a CONFLICTING one is refused.
  ///
  /// The 2-argument form declares a boundary with NO migration hook, which is
  /// the honest default: the boundary does not know how, so a type change under
  /// it falls back to a rebuild. It is kept rather than replaced so the common
  /// case stays a two-word call, and so a caller that has never heard of
  /// migration keeps compiling.
  member this.Declare (id: BoundaryId) (holds: string) : Declared =
    this.DeclareWithSubject id holds None None

  /// Declare with an explicit migration hook. See `Boundary.Migrate` for why the
  /// hook lives on the boundary rather than in the liveness registry.
  member this.DeclareWithMigration
      (id: BoundaryId)
      (holds: string)
      (migrate: (obj -> System.Type -> MigrationWorth) option)
      : Declared =
    this.DeclareWithSubject id holds migrate None

  /// Declare with a migration hook AND the subject it migrates.
  ///
  /// This is the form that makes a type change actually hot-reloadable: the
  /// `migrate` hook alone answers "is this worth carrying", and still leaves
  /// "over what value, and to what type" unanswered. Only the app holds its own
  /// cell and only it knows what the new type became, so both travel with the
  /// declaration. A boundary without a `subject` is not a broken declaration —
  /// it is an app that has not offered one, and it pays a rebuild.
  member _.DeclareWithSubject
      (id: BoundaryId)
      (holds: string)
      (migrate: (obj -> System.Type -> MigrationWorth) option)
      (subject: (unit -> (obj * System.Type) option) option)
      : Declared =
    match state.Boundaries |> Seq.tryFind (fun b -> b.Id = id) with
    | Some existing when existing.Holds = holds ->
      // A repeat of the same claim. Accept it rather than making every caller
      // guard against a double-initialise.
      //
      // The hook is NOT overwritten by a repeat that supplies none: a boundary
      // registered without a migration hook and then re-registered without one
      // keeps whatever it had. Silently clearing a hook would turn a repeat
      // into a downgrade, which is the opposite of what "accept it" means.
      //
      // The same rule applies to `Subject` for the same reason: a repeat that
      // cannot see a value is not a claim that there is no value.
      let merged =
        let withMigrate =
          match existing.Migrate, migrate with
          | Some hook, _ -> { existing with Migrate = Some hook }
          | None, hook -> { existing with Migrate = hook }
        match withMigrate.Subject, subject with
        | Some s, _ -> { withMigrate with Subject = Some s }
        | None, s -> { withMigrate with Subject = s }
      state.Boundaries[state.Boundaries.IndexOf existing] <- merged
      Declared.Accepted merged
    | Some existing ->
      Declared.Conflicted { Boundary = id; AlreadyHolds = existing.Holds; TriedToHold = holds }
    | None ->
      let boundary =
        { Id = id
          Holds = holds
          Migrate = migrate
          Subject = subject }
      state.Boundaries.Add boundary
      Declared.Accepted boundary

  /// What a boundary says a live value would be worth migrating as, or `None`
  /// when it declined to answer.
  ///
  /// `None` here is NOT "nothing is live" and NOT "cannot migrate" — it is "this
  /// boundary said nothing", and the caller must decide what an unanswered
  /// question costs. It costs a rebuild, because that is the direction we
  /// already take and a boundary's silence is not evidence.
  ///
  /// The boundary's OWN `Subject` is the authority on the pair it migrates, not
  /// the caller's arguments. That is deliberate: a caller can hand over a type it
  /// resolved by name, which is how a merely-loaded type gets mistaken for the
  /// live one, and it can hand over a value it read at a different moment than
  /// the boundary would. So the subject is read here, at decision time, and the
  /// arguments are used only where no subject was declared — a boundary that
  /// offers no subject has not claimed a value, and `None` is the honest answer
  /// rather than the caller's guess.
  member this.MigrationVerdictFor (typeName: string) (oldValue: obj) (newType: System.Type) =
    // Zero or several boundaries both mean "nobody to ask", and they are
    // different facts: an UNDECLARED type is a user action, an AMBIGUOUS one is
    // a choice. Neither is answered here — the caller reads the ambiguity from
    // `RestartScopeFor` — so both are simply "no hook to consult".
    match this.BoundariesHolding typeName with
    | [ boundary ] ->
      match boundary.Migrate, boundary.Subject with
      | None, _ -> None
      | Some migrate, Some subject ->
        // The boundary's own pair wins. A `None` from it means the app has not
        // produced a value or a type yet, and that is a refusal — NOT a reason
        // to fall back to the caller's arguments, which is the guess this whole
        // design refuses.
        subject ()
        |> Option.map (fun (subjectValue, subjectType) -> migrate subjectValue subjectType)
      | Some migrate, None -> Some(migrate oldValue newType)
    // Zero or several boundaries both mean "nobody to ask", and they are
    // different facts: an UNDECLARED type is a user action, an AMBIGUOUS one is
    // a choice. Neither is answered here — the caller reads the ambiguity from
    // `RestartScopeFor` — so both are simply "no hook to consult".
    | _ -> None

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

/// The outcome of an app-side `Declare.inCurrent`.
///
/// ONE DU, and the shape is forced. A `(Declared * NoRegistry option)` pair
/// needs a `Declared` even when nothing was declared — and there is no honest
/// one to give, because a fabricated `Accepted` reads to a caller that checks
/// only the first slot as a live guarantee that does not exist. So the refusal
/// is a case of the result, not a second value beside it.
///
/// The cases are named distinctly from the internal `Declared` DU on purpose:
/// reusing `Accepted` here made `Declared.Accepted` ambiguous at every call site
/// (the compiler caught it), and an ambiguous case is a case nobody can read.
///
/// The two refusals stay separate because their USER ACTIONS differ: nothing was
/// published means the app is not running under SageFs, while a conflict means
/// it is, and it declared the same id twice for different types.
[<RequireQualifiedAccess>]
type Declaration =
  /// Declared, and the restart will scope to this boundary.
  | DeclaredBoundary of boundary: Boundary
  /// Nothing was published, so a declaration here would be invisible.
  | Unpublished of because: string
  /// The same id already claims a DIFFERENT type, so the scope would be a coin
  /// flip. The existing claim stands.
  | ConflictedWith of conflict: DeclarationConflict

/// The app-facing boundary API. A module because a `namespace` cannot hold
/// values, and an app needs to CALL this.
[<RequireQualifiedAccess>]
module Declare =

  /// Declare a boundary the restart will actually see.
  ///
  /// The registry the worker READS and the registry an app DECLARES INTO were
  /// two objects, so every declaration an app made was invisible and every type
  /// change fell back to the module-inferred scope. This goes through the
  /// published one.
  ///
  /// The refusal is deliberate. Silently accepting a declaration no restart can
  /// see would hand a user a scoped-restart guarantee that does not exist —
  /// worse than refusing, because they would then trust a restart that stays
  /// whole-app. It is the same refusal `RegisteredHolder.holdInCurrent` makes,
  /// for the same reason.
  let inCurrent
      (id: BoundaryId)
      (holds: string)
      (migrate: (obj -> System.Type -> MigrationWorth) option)
      : Declaration =
    match Registry.Current with
    | None ->
      Declaration.Unpublished
        "no boundary registry is published in this process, so a declared boundary would be invisible to every restart"
    | Some registry ->
      match registry.DeclareWithMigration id holds migrate with
      | Declared.Accepted b -> Declaration.DeclaredBoundary b
      | Declared.Conflicted c -> Declaration.ConflictedWith c

/// The shape a boundary's type declared, as captured at parse time.
///
/// `None` means the declaration is not a record, or nothing captured it — a
/// different claim from "a record with no fields", and the same distinction
/// `DeclaredShape.DeclaresFields` makes.
type DeclaredShapeResult =
  /// The type is a record and its fields were captured.
  | ShapeKnown of fields: string list
  /// Not captured, and not to be assumed empty.
  | ShapeUnknown of because: string
