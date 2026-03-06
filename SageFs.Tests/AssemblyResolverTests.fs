module SageFs.Tests.AssemblyResolverTests

open System
open Expecto
open Expecto.Flip

// ══════════════════════════════════════════════════════════════════════
// Pure logic extracted from HotReloading.resolveAssembly for testability.
//
// The actual resolver lives in SageFs.Core/Middleware/HotReloading.fs.
// These functions mirror the decision logic so we can property-test it
// without touching real assemblies or the CLR binder.
// ══════════════════════════════════════════════════════════════════════

/// Outcome of the three-tier load fallback cascade.
type LoadOutcome =
  | LoadedFromDisk
  | LoadedBySimpleName
  | LoadedFromBytes
  | LoadFailed

/// Pure candidate selection: given an optional requested version and a
/// list of (path, version) candidates, return the best candidate
/// (highest version >= requested, or highest overall if no version
/// constraint).
let selectBestCandidate
  (requestedVersion: Version option)
  (candidates: (string * Version) list)
  : (string * Version) option =
  candidates
  |> List.filter (fun (_, v) ->
    match requestedVersion with
    | None -> true
    | Some req -> v >= req)
  |> List.sortByDescending snd
  |> List.tryHead

/// Models the three-tier fallback cascade from resolveAssembly.
/// Takes injectable load functions so we can test the control flow
/// without real assemblies.
let resolveWithFallback
  (loadFromPath: string -> Result<unit, exn>)
  (loadByName: string -> Result<unit, exn>)
  (loadFromBytes: string -> Result<unit, exn>)
  (path: string)
  : LoadOutcome =
  match loadFromPath path with
  | Ok () -> LoadedFromDisk
  | Error (:? IO.FileLoadException) ->
    match loadByName path with
    | Ok () -> LoadedBySimpleName
    | Error _ ->
      match loadFromBytes path with
      | Ok () -> LoadedFromBytes
      | Error _ -> LoadFailed
  | Error _ -> LoadFailed

// ── Test helpers ──

let ok () : Result<unit, exn> = Ok ()
let fileLoadErr () : Result<unit, exn> =
  Error (IO.FileLoadException("version mismatch") :> exn)
let otherErr () : Result<unit, exn> =
  Error (IO.FileNotFoundException("not found") :> exn)

/// Build a Version from arbitrary ints, clamped to valid ranges.
let mkVer (major: int) (minor: int) =
  System.Version(abs major % 21, abs minor % 51, 0, 0)

let mkCandidate major minor =
  (sprintf "lib%d.%d.dll" major minor, mkVer major minor)

// ══════════════════════════════════════════════════════════════════════
// Property Tests: Candidate Selection
// ══════════════════════════════════════════════════════════════════════

let candidateSelectionProperties = testList "Candidate Selection — Properties" [

  testProperty "highest version wins (no filter)" <|
    fun (pairs: (int * int) list) ->
      let cs = pairs |> List.map (fun (a, b) -> mkCandidate a b)
      match cs with
      | [] -> true
      | _ ->
        match selectBestCandidate None cs with
        | Some (_, sel) -> cs |> List.forall (fun (_, v) -> sel >= v)
        | None -> false

  testProperty "null requested version accepts any non-empty list" <|
    fun (pairs: (int * int) list) ->
      let cs = pairs |> List.map (fun (a, b) -> mkCandidate a b)
      match cs with
      | [] -> selectBestCandidate None cs |> Option.isNone
      | _ -> selectBestCandidate None cs |> Option.isSome

  testProperty "selected version >= requested" <|
    fun (reqMaj: int) (reqMin: int) (pairs: (int * int) list) ->
      let req = mkVer reqMaj reqMin
      let cs = pairs |> List.map (fun (a, b) -> mkCandidate a b)
      match selectBestCandidate (Some req) cs with
      | Some (_, v) -> v >= req
      | None -> true

  testProperty "None result means all versions < requested" <|
    fun (reqMaj: int) (reqMin: int) (pairs: (int * int) list) ->
      let req = mkVer reqMaj reqMin
      let cs = pairs |> List.map (fun (a, b) -> mkCandidate a b)
      match selectBestCandidate (Some req) cs with
      | Some _ -> true
      | None -> cs |> List.forall (fun (_, v) -> v < req)

  testProperty "result is always from the input list" <|
    fun (pairs: (int * int) list) ->
      let cs = pairs |> List.map (fun (a, b) -> mkCandidate a b)
      match selectBestCandidate None cs with
      | Some result -> cs |> List.contains result
      | None -> cs |> List.isEmpty

  testProperty "empty list always returns None" <|
    fun (reqMaj: int) (reqMin: int) ->
      let req = Some (mkVer reqMaj reqMin)
      selectBestCandidate req [] |> Option.isNone

  testProperty "single candidate: selected iff >= requested" <|
    fun (reqMaj: int) (reqMin: int) (cMaj: int) (cMin: int) ->
      let req = mkVer reqMaj reqMin
      let cv = mkVer cMaj cMin
      let cs = [ ("test.dll", cv) ]
      let result = selectBestCandidate (Some req) cs
      if cv >= req then result |> Option.isSome
      else result |> Option.isNone
]

// ══════════════════════════════════════════════════════════════════════
// Fallback Cascade Tests
//
// Chesterton's Fence:
// This three-tier cascade exists because when SageFs (host) and a user
// project reference the same NuGet package at different versions, the
// native CLR binder rejects Assembly.LoadFrom with FileLoadException
// (0x80131040). The binder has the assembly registered from SageFs's
// deps.json at a different version, and LoadFrom's identity check fails.
//
// Without this cascade, any user project referencing a different version
// of packages like OpenTelemetry, Serilog, etc. would crash at JIT time
// inside Harmony's CompileMethodHook.
//
// Tier 1: Assembly.LoadFrom(path)      — works when no version conflict
// Tier 2: Assembly.Load(simpleName)    — CLR unifies to its own version;
//         reentrancy-safe (CLR won't re-enter AssemblyResolve for same
//         assembly)
// Tier 3: Assembly.Load(File.ReadAllBytes(path)) — nuclear option that
//         bypasses all identity checks. Loses PDB association.
// ══════════════════════════════════════════════════════════════════════

let fallbackCascadeTests = testList "Fallback Cascade" [

  testCase "LoadFrom succeeds → LoadedFromDisk" <| fun () ->
    resolveWithFallback
      (fun _ -> ok ()) (fun _ -> failwith "no") (fun _ -> failwith "no")
      "t.dll"
    |> Expect.equal "should load from disk" LoadedFromDisk

  testCase "FileLoadException → Load(name) succeeds → LoadedBySimpleName" <| fun () ->
    resolveWithFallback
      (fun _ -> fileLoadErr ()) (fun _ -> ok ()) (fun _ -> failwith "no")
      "t.dll"
    |> Expect.equal "should fall back to simple name" LoadedBySimpleName

  testCase "FileLoadException → both fail → Load(bytes) succeeds → LoadedFromBytes" <| fun () ->
    resolveWithFallback
      (fun _ -> fileLoadErr ()) (fun _ -> otherErr ()) (fun _ -> ok ())
      "t.dll"
    |> Expect.equal "should fall back to byte loading" LoadedFromBytes

  testCase "FileLoadException → all fail → LoadFailed" <| fun () ->
    resolveWithFallback
      (fun _ -> fileLoadErr ()) (fun _ -> otherErr ()) (fun _ -> otherErr ())
      "t.dll"
    |> Expect.equal "should report failure" LoadFailed

  testCase "non-FileLoadException does NOT trigger cascade" <| fun () ->
    let mutable tier2Called = false
    resolveWithFallback
      (fun _ -> otherErr ())
      (fun _ -> tier2Called <- true; ok ())
      (fun _ -> failwith "no")
      "t.dll"
    |> Expect.equal "should fail immediately" LoadFailed
    tier2Called |> Expect.isFalse "tier 2 should not be called"

  testCase "path is threaded through all tiers" <| fun () ->
    let mutable paths = []
    resolveWithFallback
      (fun p -> paths <- p :: paths; fileLoadErr ())
      (fun p -> paths <- p :: paths; otherErr ())
      (fun p -> paths <- p :: paths; otherErr ())
      "my/assembly.dll"
    |> ignore
    paths
    |> List.length
    |> Expect.equal "all 3 tiers called" 3
    paths
    |> List.distinct
    |> List.length
    |> Expect.equal "all tiers received same path" 1
]

[<Tests>]
let tests = testList "Assembly Resolver" [
  candidateSelectionProperties
  fallbackCascadeTests
]
