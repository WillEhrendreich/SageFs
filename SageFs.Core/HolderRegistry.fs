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

/// Cells as they are created and swapped. Per-process by construction, because
/// liveness is a fact about ONE running process; two apps must never share
/// this, or one app's cell would make the other app's restart pay for a build.
type HolderRegistry private (cells: ResizeArray<LiveCell>, nextId: int64 ref) =

  static member New () = HolderRegistry(ResizeArray(), ref 1L)

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

  /// How many live (never superseded, never released) cells hold this type.
  member this.LiveHolding (typeName: string) =
    cells
    |> Seq.filter (fun c -> not c.Superseded && c.Holds = typeName)
    |> Seq.length
