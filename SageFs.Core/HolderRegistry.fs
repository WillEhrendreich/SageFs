namespace SageFs

// ── Which holder cells are alive, and what they hold ────────────────────
//
// WHY this exists. `RestartCost` decides whether a type change can skip its
// `dotnet build`, and it needs LIVENESS: does anything alive still hold an
// instance laid out by the old definition? The doc records that reflection
// cannot answer this — .NET exposes no per-type instance count, and a type is
// "present" with zero instances, so any reflection probe reports liveness for a
// type nobody holds.
//
// INSTRUMENTATION can answer it, and this module is that instrumentation. A
// holder cell knows its own type and exists at a known moment, so a cell can
// be REGISTERED WHEN IT IS CREATED. Liveness then becomes a fact SageFs
// holds, rather than a question it has to guess at restart time.
//
// THAT is the design point: a probe at restart time is a question, and the
// answer has to be inferred from a runtime that will not tell you. A registry
// written at construction time is a record, and the answer is a lookup.
//
// WHY THE SUPERSEDED FLAG IS NOT OPTIONAL. A cell whose contents were replaced
// no longer holds the old type, so it must stop being evidence for it. Without
// that, every type a process ever held would look live forever and every
// scoped restart would pay a build — a failure in the expensive-looking
// direction that would look like a working probe answering nothing.
//
// The count is a COUNT, and a missing cell is a distinct value rather than a
// zero, for the same reason `ProbeOutcome` separates `Counted 0` from
// `CouldNotCount`: an answer of zero and a failure to answer must never
// collapse into each other.

/// A registered cell's stable identity.
type CellId = int64

/// One registered cell.
type LiveCell =
  { Id: CellId
    /// The type this cell holds, by the name the boundary registry uses, so
    /// the two join without a translation table.
    Holds: string
    /// Whether this cell's contents were replaced. A superseded cell is not
    /// evidence for its old type.
    Superseded: bool }

/// WHAT A LIVENESS SOURCE FOUND, carrying the intent of the answer.
///
/// The design rule this type exists to honour: "the intent is intelligence,
/// context determines content". A count is only meaningful with its provenance,
/// so provenance is not a wrapper around it — it IS the answer. Every case is
/// something a source can genuinely mean, and a caller cannot manufacture one
/// it did not observe:
///
///   - `HeldBy`      — these boundaries hold a value of the old shape. EVIDENCE.
///   - `HeldByNothing` — a source looked and found none. ALSO EVIDENCE, and a
///     different claim from "nobody looked". Conflating the two is what would
///     let a failed probe skip a build.
///   - `Unconsulted`  — there is no source here. Not evidence; the caller must
///                     be able to tell this from a real zero.
///   - `SourceFailed` — a source exists and refused or broke. Distinct from
///     `Unconsulted` because the USER ACTION differs: one needs a source
///     installed, the other needs a bug fixed.
///
/// `Boundaries` rather than an `int`, so a message can name WHICH boundary
/// holds the old value — which is the thing a user can act on.
[<RequireQualifiedAccess>]
type LiveCount =
  /// These boundaries hold values of the old shape.
  | HeldBy of boundaries: string list
  /// A source was consulted and it holds nothing.
  | HeldByNothing
  /// No liveness source exists here, so nothing was established.
  | Unconsulted of because: string
  /// A source exists and could not answer.
  | SourceFailed of because: string

/// Cells as they are created and swapped. Per-process by construction, because
/// liveness is a fact about ONE running process; two apps must never share
/// this, or one app's cell would make the other app's restart pay for a build.
type HolderRegistry private (cells: ResizeArray<LiveCell>, nextId: int64 ref) =

  static member New () = HolderRegistry(ResizeArray(), ref 1L)

  /// The registry the RESTART reads, if one has been published.
  ///
  /// WHY THIS EXISTS, and it is a bug fix rather than a convenience. The app and
  /// the worker run in the SAME process (SageFs.Host/AppRunner.fs loads the
  /// assembly and invokes its entry point in-process), and each constructed its
  /// OWN registry. They are therefore two unrelated objects, so the worker's
  /// `LiveCountOf` read a registry the app never wrote to and answered `0` for
  /// every type — producing `HeldByNothing`, and with it a cheap `RespawnOnly`
  /// on the strength of nothing. Every test passed, because every test
  /// registers into and reads back from the SAME instance.
  ///
  /// `Current` is the handle that closes that gap. The worker publishes the
  /// registry it will read from; an app that holds values registers into it.
  ///
  /// IT IS NOT A REPLACEMENT FOR THE INSTANCE. Isolation is unchanged and still
  /// the point: this is a single ambient handle per PROCESS, and the existing
  /// "two registries never see each other" property is untouched for anyone who
  /// passes an explicit registry. What changes is that the worker's registry is
  /// now reachable from app code at all.
  static member val Current: HolderRegistry option = None with get, set

  /// Register a cell that now holds `holds`. This is the moment liveness
  /// becomes knowable, so it is the only moment a cell has to be recorded.
  member this.Register (holds: string) : CellId =
    let cell = { Id = nextId.Value; Holds = holds; Superseded = false }
    nextId.Value <- nextId.Value + 1L
    cells.Add cell
    cell.Id

  /// Record that a cell's contents were replaced. The cell keeps existing but
  /// is no longer evidence for its old type — and the OLD type is named, so a
  /// caller can be told what stopped holding it.
  member this.SwapIn (id: CellId) (nowHolds: string) : unit =
    match cells |> Seq.tryFind (fun c -> c.Id = id) with
    | Some cell ->
      cells.Remove cell |> ignore
      cells.Add { cell with Holds = nowHolds; Superseded = false }
    | None ->
      // A cell this registry never registered cannot be liveness evidence, and
      // silently accepting the swap would let an unknown cell look known.
      cells.Add { Id = id; Holds = nowHolds; Superseded = false }

  /// Mark a cell as no longer holding anything — a boundary disposed, a scope
  /// exited. It stays visible in `Cells` so the history is inspectable, and
  /// stops counting as live.
  member this.Release (id: CellId) : unit =
    match cells |> Seq.tryFind (fun c -> c.Id = id) with
    | Some cell ->
      cells.Remove cell |> ignore
      cells.Add { cell with Superseded = true }
    | None -> ()

  /// Everything registered, for a user asking "what does SageFs think is
  /// alive?".
  member this.Cells = List.ofSeq cells

  /// WHICH live boundaries hold this type, by name. A LIST rather than a
  /// count, because a message naming the boundary is something a user can act
  /// on, and a bare `2` is not.
  member this.BoundariesHolding (typeName: string) : string list =
    cells
    |> Seq.filter (fun c -> not c.Superseded && c.Holds = typeName)
    |> Seq.map (fun c -> c.Holds)
    |> Seq.distinct
    |> List.ofSeq

  /// The answer, carrying its provenance. A registry that HOLDS something says
  /// which boundary; one that holds nothing says so as a CLAIM, which is what
  /// lets the cost decision skip a build on evidence rather than on absence.
  member this.LiveCountOf (typeName: string) : LiveCount =
    match this.BoundariesHolding typeName with
    | [] -> LiveCount.HeldByNothing
    | held -> LiveCount.HeldBy held

  /// How many live (never superseded, never released) cells hold this type.
  member this.LiveHolding (typeName: string) =
    cells
    |> Seq.filter (fun c -> not c.Superseded && c.Holds = typeName)
    |> Seq.length
