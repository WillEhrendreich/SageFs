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

/// A record of every method hot reload has re-pointed, and where it points them. The guards read it, because patching a
/// method that hot reload has detoured replaces the detour with the original at once, and taking the patch off does not
/// put the detour back: the user's reloaded code would be gone. So a detoured method is never patched; the method its
/// entry jumps to is, when that is known, and the click says so when it is not.
///
/// Hot reload calls `markDetoured` BEFORE it writes a detour (so a click that is preparing at that moment is refused,
/// not raced) and `recordBody` once it knows what the detour points at.
module DetourLedger =

  [<RequireQualifiedAccess>]
  type private Entry =
    | Unknown
    | Body of MethodBase

  let private gate = obj ()
  let private entries = Dictionary<nativeint, Entry>()

  /// The method's identity: the same method reached through a base type or a derived one is one method.
  let private identity (m: MethodBase) : nativeint =
    try m.MethodHandle.Value
    with _ -> 0n

  /// Hot reload is about to re-point `older`. Whatever was known about where it pointed is no longer trusted.
  let markDetoured (older: MethodBase) : unit =
    match identity older with
    | 0n -> ()
    | key -> lock gate (fun () -> entries.[key] <- Entry.Unknown)

  /// `older`'s entry now jumps to `body`, and `body`'s IL is what runs.
  let recordBody (older: MethodBase) (body: MethodBase) : unit =
    match identity older with
    | 0n -> ()
    | key -> lock gate (fun () -> entries.[key] <- Entry.Body body)

  /// What was done to this method, one step.
  let resolve (m: MethodBase) : Detour =
    match identity m with
    | 0n -> Detour.NotDetoured
    | key ->
      lock gate (fun () ->
        match entries.TryGetValue key with
        | true, Entry.Body body -> Detour.DetouredTo body
        | true, Entry.Unknown -> Detour.DetouredElsewhere
        | false, _ -> Detour.NotDetoured)
