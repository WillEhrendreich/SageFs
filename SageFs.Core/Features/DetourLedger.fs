namespace SageFs.Features

// BCL only, on purpose: compiled into SageFs.Core and embedded into the isolated FSI host (see the DetourLedger entries
// in SageFs.Core.fsproj, FsiHostBuild.fs and FsiHost.fsproj), and it must come before the guard patcher and before hot
// reload, which writes to it.

open System
open System.Collections.Generic
open System.Reflection

/// What hot reload has done to a method.
[<RequireQualifiedAccess>]
type Detour =
  /// Nothing re-pointed it. Its own IL is what runs.
  | NotDetoured
  /// Its entry jumps to this method, and this method's IL is what runs.
  | DetouredTo of MethodBase
  /// Its entry was (or is about to be) re-pointed and where to is not known. Nothing may be patched onto it.
  | DetouredElsewhere

/// What the ledger knows of one re-pointed method.
[<RequireQualifiedAccess>]
type internal LedgerEntry =
  | Unknown
  | Body of MethodBase

/// A record of every method hot reload has re-pointed, and where it points them. The guards read it, because patching a
/// method that hot reload has detoured replaces the detour with the original at once, and taking the patch off does not
/// put the detour back: the user's reloaded code would be gone. So a detoured method is never patched; the method its
/// entry jumps to is, when that is known, and the click says so when it is not.
///
/// Hot reload calls `MarkDetoured` BEFORE it writes a detour (so a click that is preparing at that moment is refused,
/// not raced) and `RecordBody` once it knows what the detour points at. One ledger per process (`DetourLedger.shared`);
/// the guard simulation makes its own.
type Ledger() =
  let gate = obj ()
  let entries = Dictionary<nativeint, LedgerEntry>()

  /// The method's identity: the same method reached through a base type or a derived one is one method.
  let identity (m: MethodBase) : nativeint =
    try m.MethodHandle.Value
    with _ -> 0n

  /// Hot reload is about to re-point `older`. Whatever was known about where it pointed is no longer trusted.
  member _.MarkDetoured(older: MethodBase) : unit =
    match identity older with
    | 0n -> ()
    | key -> lock gate (fun () -> entries.[key] <- LedgerEntry.Unknown)

  /// `older`'s entry now jumps to `body`, and `body`'s IL is what runs.
  member _.RecordBody(older: MethodBase, body: MethodBase) : unit =
    match identity older with
    | 0n -> ()
    | key -> lock gate (fun () -> entries.[key] <- LedgerEntry.Body body)

  /// What was done to this method, one step.
  member _.Resolve(m: MethodBase) : Detour =
    match identity m with
    | 0n -> Detour.NotDetoured
    | key ->
      lock gate (fun () ->
        match entries.TryGetValue key with
        | true, LedgerEntry.Unknown -> Detour.DetouredElsewhere
        | true, LedgerEntry.Body body -> Detour.DetouredTo body
        | false, _ -> Detour.NotDetoured)

module DetourLedger =

  /// The process's ledger: hot reload writes it and the guards read it.
  let shared : Ledger = Ledger()

  let markDetoured (older: MethodBase) : unit = shared.MarkDetoured older
  let recordBody (older: MethodBase) (body: MethodBase) : unit = shared.RecordBody(older, body)
  let resolve (m: MethodBase) : Detour = shared.Resolve m
