/// Starting an FSI session in an isolated host process, for the worker: build (or reuse) the host with the
/// project's own SDK, decide which runtime the host should run on, start it, and wrap it as an IFsiSession.
///
/// The worker itself stays on its own runtime; only the host — the process the user's code actually runs in —
/// is launched on the runtime the project needs, and it shares no assembly with SageFs.
module SageFs.IsolatedFsiSession

open System
open System.IO
open System.Threading.Tasks
open SageFs.FsiHostBuild
open SageFs.FsiHostClient
open SageFs.FsiSession
open SageFs.RemoteFsiSession
open SageFs.Utils

/// Why an isolated session could not be started.
type IsolatedStartError =
  | SdkUnresolved of HostBuildError
  | HostBuildFailed of HostBuildError
  | RuntimeNotInstalled of instructions: string
  | HostStartFailed of StartError
  | AgentAttachFailed of AttachError

/// Every case says what happened and, where the user can act, what to do.
let describeStartError (error: IsolatedStartError) : string =
  match error with
  | SdkUnresolved reason -> describeBuildError reason
  | HostBuildFailed reason -> describeBuildError reason
  | RuntimeNotInstalled instructions -> instructions
  | HostStartFailed reason -> SageFs.FsiHostClient.describeStartError reason
  | AgentAttachFailed reason -> describeAttachError reason

/// The dotnet muxer: DOTNET_HOST_PATH, else the one next to the running runtime.
let dotnetPath () : string =
  match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
  | null
  | "" ->
    Args.muxerFromRuntimeDir
      (System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory())
      (OperatingSystem.IsWindows())
  | path -> path

/// Overrides where built hosts are cached. A host is content-addressed (SDK version + exact sources + its Harmony), so
/// sharing one cache between daemons is safe; a test harness whose daemons each get a fresh data dir uses this to build the
/// host once instead of once per daemon.
[<Literal>]
let HostCacheEnvironmentVariable = "SAGEFS_HOST_CACHE_DIR"

/// Where built hosts are cached: the override, else under the SageFs data dir.
let hostCacheRootWith (getEnv: string -> string | null) (sageFsDir: string) : string =
  match getEnv HostCacheEnvironmentVariable with
  | null -> Path.Combine(sageFsDir, "hosts")
  | value when String.IsNullOrWhiteSpace value -> Path.Combine(sageFsDir, "hosts")
  | value -> value

let hostCacheRoot () : string = hostCacheRootWith Environment.GetEnvironmentVariable DaemonState.SageFsDir

/// The env var that carries the primary project's own build output directory into the isolated host process
/// (issue #142's own suggested name). `AppContext.BaseDirectory` and `Assembly.Location` inside a session
/// point at the HOST's own directory — or, for a referenced project's assembly, wherever FSI's runtime
/// loaded it from, which is not necessarily the project's build output either — never at the project's own
/// bin/<config>/<tfm>/, so any code that resolves config, fixtures or native libs relative to its own
/// assembly breaks. The host sets `AppContext.BaseDirectory` from this at startup (see FsiHost/Program.fs);
/// code that reads the env var directly gets the same answer without the AppContext indirection.
[<Literal>]
let ProjectOutputEnvironmentVariable = "SAGEFS_PROJECT_OUTPUT"

/// Pure over injected filesystem primitives: the primary project's (the first of `projects` — the one
/// explicitly requested, not a transitive reference) own build output directory — the directory holding
/// the NEWEST (by write time) `<ProjectName>.dll` found anywhere under its `bin/`, mirroring
/// `RuntimeSelection.projectRuntimeRequirement`'s identical "newest match under bin/, by write time" rule
/// so the two can never disagree about which build is "the" one.
///
/// Deliberately NOT derived from the `-r:` references `solutionToFsiArgs` builds: the worker shadow-copies
/// the whole solution to a `/tmp/sagefs-shadow-*` directory before FSI ever sees it, rewriting every `-r:`
/// path to point there — so reading a `-r:` entry would report the SAME shadow-copy temp directory #142
/// already complained about for `Assembly.Location`, not the project's real build output. Reading straight
/// from disk, independent of what FSI was told to load, is what makes this answer actually different from
/// the bug it's fixing.
let primaryProjectOutputDirWith
    (directoryExists: string -> bool)
    (matchingFiles: string -> string -> string list)
    (writeTimeUtc: string -> DateTime)
    (projects: string list)
    : string option =
  match projects with
  | [] -> None
  | primary :: _ ->
    let projectDir = Path.GetDirectoryName(primary: string)
    let binDir = Path.Combine(projectDir, "bin")
    let expectedName = Path.GetFileNameWithoutExtension(primary: string) + ".dll"
    match directoryExists binDir with
    | false -> None
    | true ->
      matchingFiles binDir expectedName
      |> List.sortByDescending writeTimeUtc
      |> List.tryHead
      |> Option.bind (Path.GetDirectoryName >> Option.ofObj)

let primaryProjectOutputDir (projects: string list) : string option =
  primaryProjectOutputDirWith
    Directory.Exists
    (fun dir pattern -> Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories) |> Seq.toList)
    File.GetLastWriteTimeUtc
    projects

/// The isolated host's own FSharp.Core.dll (`typeof<unit>` lives there) — a sibling of the host's own entry
/// assembly, since `ensureBuiltWith` copies the SDK toolset's FSharp.Core into the same build output
/// directory as `FsiHost.dll`.
let private hostFSharpCoreDll (hostDll: string) : string = Path.Combine(Path.GetDirectoryName(hostDll: string), "FSharp.Core.dll")

/// The project's own resolved FSharp.Core.dll, if `-r:` references one — the same lookup as
/// `primaryProjectOutputDir`, by file name rather than by owning project.
let private projectFSharpCoreDll (fsiArgs: string list) : string option =
  fsiArgs
  |> List.tryPick (fun (arg: string) ->
    match arg.StartsWith("-r:", StringComparison.Ordinal) with
    | false -> None
    | true ->
      let path = arg.Substring 3
      match String.Equals(Path.GetFileName path, "FSharp.Core.dll", StringComparison.OrdinalIgnoreCase) with
      | true -> Some path
      | false -> None)

/// A project's FSharp.Core is a different BUILD from the host's own — see #141. Both file sizes are carried
/// because the file VERSION and even the .NET AssemblyVersion are commonly identical between the SDK's own
/// toolset copy and a project's NuGet-restored copy despite being different builds with different members
/// (a `Using` overload present in one, missing in the other) — version strings cannot detect this. File size
/// is the cheap, reliable signal the issue's own diagnosis used (SHA-256 would be stronger but costs a full
/// read of a multi-MB file on every warmup for a mismatch that, once known, never needs re-proving down to
/// the byte).
type FSharpCoreMismatch =
  { HostCopy: string
    HostSizeBytes: int64
    ProjectCopy: string
    ProjectSizeBytes: int64 }

let describeFSharpCoreMismatch (m: FSharpCoreMismatch) : string =
  sprintf
    "This session's code runs against the host's own FSharp.Core (%s, %d bytes), not the project's resolved copy (%s, %d bytes). FSI hosts everything in one process, and the first FSharp.Core loaded wins — even when a project references a different build under the same version number. Code compiled into the project that hits a member only the project's copy has (a known case: `use` inside `task { }`, see SageFs issue #141 — a DEBUG build calls `TaskBuilderBase.Using` as a real virtual method, which a mismatched FSharp.Core may not have; a RELEASE build inlines it away entirely, so building the project with `dotnet build -c Release` sidesteps this specific failure today) will fail with MissingMethodException; that failure is not a bug in your code."
    m.HostCopy
    m.HostSizeBytes
    m.ProjectCopy
    m.ProjectSizeBytes

/// Pure over file lengths, so it's testable without real FSharp.Core.dll files on disk. `None` when either
/// length can't be read (fine: it means "not proven mismatched", never a false positive) or the lengths
/// happen to agree.
let detectFSharpCoreMismatchWith
    (fileLength: string -> int64 option)
    (hostFSharpCore: string)
    (projectFSharpCore: string)
    : FSharpCoreMismatch option =
  match fileLength hostFSharpCore, fileLength projectFSharpCore with
  | Some hostLen, Some projLen when hostLen <> projLen ->
    Some
      { HostCopy = hostFSharpCore
        HostSizeBytes = hostLen
        ProjectCopy = projectFSharpCore
        ProjectSizeBytes = projLen }
  | _ -> None

let private fileLengthOrNone (path: string) : int64 option =
  try Some(FileInfo(path).Length)
  with _ -> None

/// #141 resolution: an IL-level identity rewrite so the PROJECT's own resolved FSharp.Core is what its
/// compiled code actually runs against — the same technique `FsiHostBuild.renameAssembly` already uses to
/// keep the agent's own Harmony from colliding with a user's Lib.Harmony, applied the other direction: not
/// "rename OUR dependency before WE compile against it" (Harmony's case — the consumer is compiled fresh,
/// so it never has a wrong-identity reference to begin with) but "rename an ALREADY-COMPILED third party's
/// dependency after the fact" — the project's DLL exists before SageFs ever sees it, so there is no
/// "compile fresh against the renamed name" option here.
///
/// Deliberately NOT a wholesale swap of the host's own FSharp.Core.dll: per #141's own diagnosis the two
/// builds are not superset/subset (the host's copy has FEWER `Using` overloads than the project's), so
/// swapping the file the host's OWN FsiHost.dll/FSharp.Compiler.Service depend on could just as easily
/// break the host's eval machinery as fix the project's code. This rewrite instead gives the project's own
/// FSharp.Core a NEW identity nothing else in the process uses, so it coexists with the host's untouched
/// original copy in the same process: two different builds of "the types named Microsoft.FSharp.Core.*",
/// living under two different assembly names, at once. The host's own code never references the new name,
/// so nothing about how it runs changes.
///
/// Renaming ONLY the shared `AssemblyNameReference` entry (not each TypeRef/MemberRef individually) is what
/// makes this a one-line edit per assembly: Cecil resolves every TypeRef whose scope is "FSharp.Core"
/// through that ONE reference object, so mutating its `.Name` retargets all of them transparently — the
/// same reason `renameAssembly` only ever had to touch `AssemblyDefinition.Name`, never walk the type table.
///
/// WIRED IN, via `ActorCreation.createActorImmediate` calling `fixShadowCopiedFSharpCoreReferences` (below)
/// immediately after `ShadowCopy.shadowCopySolution` — before namespace scanning, coverage instrumentation,
/// or FSI's own `-r:` loading ever touch the shadow-copied file, so there is exactly one physical file for
/// each project assembly and it is already correct before anything reads it. Two real findings from getting
/// here, both live-verified:
///
/// 1. Exposing the renamed FSharp.Core as an explicit `-r:` reference corrupts session-wide type-checking
///    (bare `Some 42;;` crashed FCS internally with `convMethodRef: could not bind to method`, and the
///    project's own function calls failed FCS's unification of two different-identity `unit`s). `--lib:`
///    (a search directory, not a compiler reference) avoids this — the CLR resolves the renamed assembly
///    lazily, on demand, invisible to FCS's own type-checking until something actually needs it.
///
/// 2. A rewrite applied late, as a patch on the flattened `fsiArgs: string list` inside
///    `IsolatedFsiSession.start`, is provably bypassed: `ShadowCopy.shadowCopySolution` re-derives every
///    project's own assembly from its TRUE original `bin/` output for IL coverage instrumentation,
///    operating on the rich `Solution` record — a step `start` cannot see because it only ever receives
///    the already-flattened strings. Fixed by moving the rewrite to run ON the shadow copy itself, in
///    `ActorCreation.fs`, before anything downstream can re-derive from the original.
///
/// A third, narrower finding surfaced once the rewrite actually took hold: F#'s cross-module inlining.
/// Every F# assembly embeds `FSharpOptimizationCompressedData(B)` — the data FCS reads to inline small,
/// simple functions from a referenced assembly directly into the CALLING code. For a trivial one-liner
/// (`let f () = typeof<int option>.Assembly.Location`), FCS inlines the body into the submission and
/// RECOMPILES it there — against FSI's own ambient FSharp.Core, not the rewritten identity — even though
/// the SAME function, invoked via plain reflection (`MethodInfo.Invoke`, bypassing FCS's compile-time
/// inlining) or called from inside a larger, non-inlinable function (proven: `useInTask`'s whole
/// resumable-code state machine is far too large to inline), correctly uses the rewritten copy. Live,
/// side-by-side, in the SAME submission: a direct call to `Repro.fsharpCoreLocation()` returned the host's
/// path; `MethodInfo.Invoke` on the exact same `MethodInfo` returned the project's own rewritten path.
/// `rewriteReferenceWith` strips `FSharpOptimizationCompressedData*` (inlining hints only) while leaving
/// `FSharpSignatureCompressedData*` untouched (still needed for FCS to type-check calls into the assembly
/// at all) — this forces every call into a rewritten assembly to be a real method call, never an inline.
module ProjectFSharpCoreIdentity =

  /// The identity the project's own FSharp.Core is renamed to. Distinct from `FsiHostBuild.HostHarmonyName`
  /// in kind (that one is compiled fresh against; this one is rewritten after the fact) but the same idea:
  /// a name nothing else in the process could ever collide with.
  [<Literal>]
  let RewrittenName = "SageFs.ProjectFSharpCore"

  /// F#-compiler-embedded manifest resources that let FCS INLINE a referenced assembly's simple functions
  /// into the calling code, recompiled fresh against whatever FSharp.Core the CALLER (not the callee) is
  /// ambient in — see the module doc comment for the live, side-by-side proof this causes for a rewritten
  /// assembly. `FSharpSignatureCompressedData*` (the F# signature data FCS needs to type-check calls into
  /// the assembly at all) is a DIFFERENT resource and is never touched.
  let private optimizationResourcePrefixes = [ "FSharpOptimizationCompressedData."; "FSharpOptimizationCompressedDataB." ]

  /// Renders a `TypeReference` the same way regardless of WHERE it was read from — the one thing
  /// `.FullName` does NOT do for a generic type/method parameter. Live-verified divergence: reading
  /// `TaskBuilderBase.Using<TResource,TOverall,T>`'s own `MethodDefinition` straight out of FSharp.Core.dll
  /// renders its method generic parameters by their DECLARED NAME (`TResource`); reading the exact same
  /// method through a `MethodReference` imported into a CALLING assembly (which is how every real call
  /// site is found below) renders them positionally (`!!0`) — Cecil does not carry a MethodReference's
  /// target method's own parameter names across the assembly boundary. Comparing `.FullName` strings from
  /// the two sources therefore never matches for any generic method, silently turning every candidate call
  /// site invisible to `missingFromHost` — that regressed #141 itself (a `task { use r = ... }` call to
  /// this exact method) the first time this module used raw `.FullName` for both sides. Normalizing every
  /// generic parameter to its POSITION (stable across both representations) instead of its name fixes it.
  let rec private normalizeTypeName (tr: Mono.Cecil.TypeReference) : string =
    match tr with
    | :? Mono.Cecil.GenericParameter as gp ->
      match gp.Type with
      | Mono.Cecil.GenericParameterType.Method -> sprintf "!!%d" gp.Position
      | _ -> sprintf "!%d" gp.Position
    | :? Mono.Cecil.GenericInstanceType as git ->
      sprintf "%s<%s>" (normalizeTypeName git.ElementType) (git.GenericArguments |> Seq.map normalizeTypeName |> String.concat ",")
    | tr -> tr.FullName

  /// A signature key identifying one method, independent of WHICH assembly compiled it: declaring type's
  /// full name, method name, and each parameter's NORMALIZED type name (see `normalizeTypeName`) — the
  /// same shape for `TResource` (read from the method's own definition) and `!!0` (read from a call site
  /// in a different assembly), so this reliably answers "does an overload with this exact shape exist
  /// here" without caring which literal assembly compiled it, or from which side the signature was read.
  let private methodKey (declaringTypeFullName: string) (name: string) (parameterTypeNames: string list) : string =
    sprintf "%s::%s(%s)" declaringTypeFullName name (String.concat "," parameterTypeNames)

  let private publicMethodKeysOf (modul: Mono.Cecil.ModuleDefinition) : Set<string> =
    modul.Types
    |> Seq.collect (fun t -> t.Methods)
    |> Seq.filter (fun m -> m.IsPublic || m.IsFamily)
    |> Seq.map (fun m ->
      methodKey
        m.DeclaringType.FullName
        m.Name
        (m.Parameters |> Seq.map (fun p -> normalizeTypeName p.ParameterType) |> List.ofSeq))
    |> Set.ofSeq

  /// The FSharp.Core members the project's build has that the host's build lacks — the ONLY members #141
  /// needs redirected to the project's own copy; on this box, 16 (ValueTask/ReadOnlySpan additions plus
  /// #141's own `TaskBuilderBase.Using` overload). Never throws: an unreadable file (bad image, IO error)
  /// answers "nothing provably missing" (empty set) — the same fail-closed-to-no-op posture as
  /// `detectFSharpCoreMismatchWith`.
  let missingMethodKeys (hostFSharpCoreDll: string) (projectFSharpCoreDll: string) : Set<string> =
    try
      use hostAsm = Mono.Cecil.AssemblyDefinition.ReadAssembly(hostFSharpCoreDll)
      use projectAsm = Mono.Cecil.AssemblyDefinition.ReadAssembly(projectFSharpCoreDll)
      Set.difference (publicMethodKeysOf projectAsm.MainModule) (publicMethodKeysOf hostAsm.MainModule)
    with _ -> Set.empty

  /// Pure over injected bytes and the exact set of FSharp.Core members to redirect: rewrite ONLY the call
  /// sites (`call`/`callvirt`/`newobj`/generic-instantiated-call instructions) whose target method both
  /// (a) is declared on a type scoped to "FSharp.Core" and (b) matches one of `missingFromHost`'s
  /// signatures — to `RewrittenName` instead, stripping cross-module inlining data for every touched
  /// assembly (see the module doc comment for why). `None` when the assembly has no FSharp.Core reference,
  /// or has one but no call site actually needs redirecting — most referenced assemblies in a typical -r:
  /// list are in one of those two buckets, and are left completely untouched.
  ///
  /// Everything else — every OTHER FSharp.Core call (including ones a THIRD PARTY like Expecto also makes
  /// against its own FSharp.Core-typed parameters, and every closure/type the project's own code derives
  /// from FSharp.Core types) — is left byte-for-byte untouched, so it keeps resolving to whatever single
  /// ambient FSharp.Core the process already has loaded, exactly as it always has.
  ///
  /// A blanket rewrite (retarget the WHOLE "FSharp.Core" `AssemblyNameReference`, unconditionally,
  /// regardless of which members are actually missing) was tried first and reverted: Cecil dedups
  /// `TypeReference`s by (namespace, name, scope), so ONE shared TypeRef row for e.g.
  /// `FSharpFunc\`2<unit,unit>` is used BOTH as the base type of a project-compiled closure AND as a
  /// parameter type in the signature Cecil emits for a CALL INTO A THIRD PARTY (e.g. Expecto's
  /// `TestCaseBuilder.Run(FSharpFunc<unit,unit>)`) that also happens to take that type. Renaming the shared
  /// reference retargets BOTH uses at once — fine for the project's own calls into FSharp.Core's own API
  /// (the whole point), but it also silently retargets the signature of every call into a completely
  /// unrelated third party, which still expects the unrenamed identity. Live-verified: rewriting
  /// everything turned `test "..." { ... }` calls in an Expecto-based project's compiled code into
  /// `MissingMethodException: TestCaseBuilder.Run(FSharpFunc<Unit,Unit>)` — the SAME failure class #141
  /// itself reports, just moved to a different boundary — and live-testing discovery, which must actually
  /// INVOKE a project's `[<Tests>]` value to enumerate it, reported the assembly as having zero tests
  /// instead of surfacing that exception. Scoping the rewrite to only the specific missing members makes
  /// every call into anything else, including any third party, provably untouched.
  let rewriteReferenceWith
      (readAssembly: byte[] -> Mono.Cecil.AssemblyDefinition)
      (missingFromHost: Set<string>)
      (dllBytes: byte[])
      : byte[] option =
    use assembly = readAssembly dllBytes
    let modul = assembly.MainModule
    let original =
      modul.AssemblyReferences
      |> Seq.tryFind (fun r -> String.Equals(r.Name, "FSharp.Core", StringComparison.Ordinal))
    match original with
    | None -> None
    | Some _ when Set.isEmpty missingFromHost ->
      None // nothing the project has that the host lacks — no call site could possibly need redirecting
    | Some original ->
      let renamed = Mono.Cecil.AssemblyNameReference(RewrittenName, original.Version)
      renamed.PublicKeyToken <- original.PublicKeyToken
      renamed.Culture <- original.Culture

      let rec retarget (tr: Mono.Cecil.TypeReference) : Mono.Cecil.TypeReference =
        match tr with
        | null -> null
        | :? Mono.Cecil.GenericInstanceType as git ->
          let git2 = Mono.Cecil.GenericInstanceType(retarget git.ElementType)
          for a in git.GenericArguments do
            git2.GenericArguments.Add(retarget a)
          git2 :> Mono.Cecil.TypeReference
        | tr when obj.ReferenceEquals(tr.Scope, original :> Mono.Cecil.IMetadataScope) ->
          Mono.Cecil.TypeReference(tr.Namespace, tr.Name, modul, renamed :> Mono.Cecil.IMetadataScope, tr.IsValueType)
        | tr -> tr

      let scopesToOriginal (tr: Mono.Cecil.TypeReference) =
        match tr with
        | null -> false
        | :? Mono.Cecil.GenericInstanceType as git ->
          obj.ReferenceEquals(git.ElementType.Scope, original :> Mono.Cecil.IMetadataScope)
        | tr -> obj.ReferenceEquals(tr.Scope, original :> Mono.Cecil.IMetadataScope)

      let keyOf (mr: Mono.Cecil.MethodReference) =
        methodKey
          mr.DeclaringType.FullName
          mr.Name
          (mr.Parameters |> Seq.map (fun p -> normalizeTypeName p.ParameterType) |> List.ofSeq)

      // Retargets an ordinary (non-generic-instantiated) MethodReference's declaring type + signature, IF
      // it both scopes to FSharp.Core and names one of the missing members. Returns whether it touched
      // anything so the GenericInstanceMethod case below can share the one rule — Cecil throws
      // InvalidOperationException if DeclaringType/ReturnType/Parameters are set directly on a
      // MethodSpecification, so a generic call's ElementMethod (an ordinary MethodReference) is what
      // actually needs retargeting.
      let retargetIfMissing (mr: Mono.Cecil.MethodReference) : bool =
        match scopesToOriginal mr.DeclaringType && Set.contains (keyOf mr) missingFromHost with
        | true ->
          mr.DeclaringType <- retarget mr.DeclaringType
          mr.ReturnType <- retarget mr.ReturnType
          for p in mr.Parameters do
            p.ParameterType <- retarget p.ParameterType
          true
        | false -> false

      // `modul.Types` lists TOP-LEVEL types only — Cecil does not recurse into `NestedTypes` on its own.
      // Live-verified this is where the actual bug was: `task { use r = ... }`'s `TaskBuilderBase.Using`
      // call does not live on `Repro`'s own top-level methods at all — it lives on the `Invoke` method of
      // a compiler-generated closure class NESTED inside `Repro` (F#'s resumable-code desugaring puts each
      // `Delay`/`Using`/continuation step in its own nested "Pipe #N input at line L" class). A walk over
      // `modul.Types` alone finds zero FSharp.Core call sites for this exact shape and silently returns
      // `None` — reproducing #141's own bug, which is exactly what the narrower rewrite regressed to
      // before this fix.
      let rec allTypes (t: Mono.Cecil.TypeDefinition) : Mono.Cecil.TypeDefinition seq =
        seq {
          yield t
          for nested in t.NestedTypes do
            yield! allTypes nested
        }

      let mutable touchedAny = false
      for t in modul.Types |> Seq.collect allTypes do
        for m in t.Methods do
          match m.HasBody with
          | false -> ()
          | true ->
            for instr in m.Body.Instructions do
              match instr.Operand with
              | :? Mono.Cecil.GenericInstanceMethod as gim ->
                match retargetIfMissing gim.ElementMethod with
                | true -> touchedAny <- true
                | false -> ()
              | :? Mono.Cecil.MethodReference as mr when not (mr :? Mono.Cecil.MethodDefinition) ->
                match retargetIfMissing mr with
                | true -> touchedAny <- true
                | false -> ()
              | _ -> ()

      match touchedAny with
      | false -> None // every candidate call site already matches what the host has — nothing to rewrite
      | true ->
        modul.AssemblyReferences.Add(renamed)
        modul.Resources
        |> Seq.filter (fun res -> optimizationResourcePrefixes |> List.exists (fun prefix -> res.Name.StartsWith(prefix, StringComparison.Ordinal)))
        |> Seq.toList // materialize before mutating the collection we're iterating
        |> List.iter (fun res -> modul.Resources.Remove res |> ignore)
        use output = new MemoryStream()
        assembly.Write output
        Some(output.ToArray())

  let private readAssemblyFromBytes (bytes: byte[]) : Mono.Cecil.AssemblyDefinition =
    Mono.Cecil.AssemblyDefinition.ReadAssembly(new MemoryStream(bytes))

  /// Rewrite one on-disk assembly's FSharp.Core reference, IO and all. `None` (untouched, no file written)
  /// when the assembly has no FSharp.Core reference, has one but nothing in `missingFromHost` is actually
  /// called, or couldn't be read as a .NET assembly at all (never a session-killing exception; worst case,
  /// that one assembly keeps resolving against whatever it did before, exactly today's behavior).
  let rewriteFileReference (missingFromHost: Set<string>) (dllPath: string) : byte[] option =
    try rewriteReferenceWith readAssemblyFromBytes missingFromHost (File.ReadAllBytes dllPath)
    with _ -> None

/// #141, the REAL fix: rewrite a shadow-copied project assembly's FSharp.Core reference IN PLACE — the
/// SAME file every downstream step (namespace scanning, IL coverage instrumentation, FSI's own `-r:`
/// loading) already reads from, not a new copy at a new path. `ActorCreation.createActorImmediate` calls
/// this immediately after `ShadowCopy.shadowCopySolution`, before anything else touches
/// `sln.Projects[i].TargetPath` — earlier than `IsolatedFsiSession.start` even runs, let alone finishes,
/// which is exactly why the `fsiArgs`-string-level rewrite this module used to attempt there never took:
/// whatever reads `sln.Projects` first (proven live to be namespace scanning or coverage instrumentation,
/// not FSI's own loader) always wins the race and re-derives the loaded bytes from the project's original,
/// un-rewritten build output. There is only ever ONE physical file for each project assembly; making it
/// correct before anything reads it removes the race instead of trying to out-run it.
///
/// Resolves and builds the isolated host itself (same `resolveSdk`/`ensureBuiltWith` calls
/// `IsolatedFsiSession.start` makes later) — an extra call, but a cheap one: the host is content-addressed
/// and cached, so every session after the first for a given SDK finds it already built. Returns the shadow
/// directory a `--lib:` entry must be added for (only when a rewrite actually happened — the renamed
/// FSharp.Core needs to be on FSI's search path for the CLR to resolve it at runtime), so the caller can
/// fold it into `sln.LibPaths` before `solutionToFsiArgs` ever runs.
let fixShadowCopiedFSharpCoreReferences
    (logger: ILogger)
    (workingDir: string)
    (sln: SageFs.ProjectLoading.Solution)
    : string option =
  let projectFSharpCoreOf (po: Ionide.ProjInfo.Types.ProjectOptions) : string option =
    po.PackageReferences
    |> List.tryFind (fun pr -> String.Equals(Path.GetFileNameWithoutExtension(pr.FullPath: string), "FSharp.Core", StringComparison.OrdinalIgnoreCase))
    |> Option.map (fun pr -> pr.FullPath)
  match sln.Projects with
  | [] -> None
  | projects ->
    let dotnet = dotnetPath ()
    match resolveSdk dotnet workingDir with
    | Error _ -> None // can't resolve the host from here; the later detect-and-warn path in `start` still covers it
    | Ok sdk ->
      match ensureBuiltWith dotnet sdk (hostCacheRoot ()) with
      | Error _ -> None
      | Ok build ->
        let hostDll = match build with Built d -> d | Reused d -> d
        let hostFSharpCore = hostFSharpCoreDll hostDll
        let mutable fixedShadowDir = None
        for po in projects do
          match projectFSharpCoreOf po with
          | None -> ()
          | Some projectFSharpCore ->
            match detectFSharpCoreMismatchWith fileLengthOrNone hostFSharpCore projectFSharpCore with
            | None -> ()
            | Some mismatch ->
              try
                // A file-size difference is only PROOF that the two builds differ, not proof any of that
                // difference is a member the PROJECT'S code actually calls — most differences between two
                // "same version, different build" FSharp.Core copies are irrelevant. Only the members the
                // project's build has that the host's LACKS are candidates for redirecting; see
                // `ProjectFSharpCoreIdentity.rewriteReferenceWith`'s own doc comment for why redirecting
                // anything wider than that breaks calls into third parties (Expecto and friends).
                let missing = ProjectFSharpCoreIdentity.missingMethodKeys mismatch.HostCopy mismatch.ProjectCopy
                match ProjectFSharpCoreIdentity.rewriteFileReference missing po.TargetPath with
                | None -> ()
                | Some rewrittenBytes ->
                  File.WriteAllBytes(po.TargetPath, rewrittenBytes)
                  let shadowDir = Path.GetDirectoryName(po.TargetPath: string)
                  let renamedPath = Path.Combine(shadowDir, ProjectFSharpCoreIdentity.RewrittenName + ".dll")
                  match File.Exists renamedPath with
                  | true -> ()
                  | false ->
                    let renamedBytes = renameAssembly ProjectFSharpCoreIdentity.RewrittenName (File.ReadAllBytes mismatch.ProjectCopy)
                    File.WriteAllBytes(renamedPath, renamedBytes)
                  fixedShadowDir <- Some shadowDir
                  logger.LogWarning(
                    sprintf
                      "  Rewrote %s's FSharp.Core reference to its own identity in place for %d member(s) the host's build lacks (host: %s, %d bytes; project: %s, %d bytes)"
                      (Path.GetFileName(po.TargetPath: string))
                      (Set.count missing)
                      mismatch.HostCopy
                      mismatch.HostSizeBytes
                      mismatch.ProjectCopy
                      mismatch.ProjectSizeBytes
                  )
              with ex ->
                logger.LogWarning(
                  sprintf
                    "  Could not rewrite %s's FSharp.Core reference (%s) — continuing with the host's copy, which is the pre-existing (potentially mismatched) behavior"
                    (Path.GetFileName(po.TargetPath: string))
                    ex.Message
                )
        fixedShadowDir

/// Start an isolated FSI session for `projects`, run from `workingDir`. `recorder` receives everything the user's
/// code and FSI write to stdout (so per-eval output capture works exactly as it does in-process).
let start
  (logger: ILogger)
  (recorder: TextWriter)
  (fsiArgs: string list)
  (workingDir: string)
  (projects: string list)
  (agent: HostAgent.AgentInit)
  : Async<Result<IFsiSession, IsolatedStartError>> =
  async {
    let dotnet = dotnetPath ()
    match resolveSdk dotnet workingDir with
    | Error reason -> return Error(SdkUnresolved reason)
    | Ok sdk ->
      // Building is a blocking process run; keep it off the caller's thread.
      let! built = Async.AwaitTask(Task.Run(fun () -> ensureBuiltWith dotnet sdk (hostCacheRoot ())))
      match built with
      | Error reason -> return Error(HostBuildFailed reason)
      | Ok build ->
        let dll =
          match build with
          | Built dll -> dll
          | Reused dll -> dll
        // The host is built for its SDK's target framework, so that major is the runtime it runs on by default.
        let hostMajor = int (sdk.Version.Split('.').[0])
        match RuntimeSelection.resolveRuntimeChoiceFor hostMajor projects with
        | RuntimeCompat.RuntimeMissing _ as choice -> return Error(RuntimeNotInstalled(RuntimeCompat.describe choice))
        | choice ->
          match choice with
          | RuntimeCompat.RollForward _ -> logger.LogInfo(sprintf "  Isolated FSI host: %s" (RuntimeCompat.describe choice))
          | _ -> ()
          // #142: give the project's own code a way back to its real build output directory — the host
          // process sets AppContext.BaseDirectory from this at startup (see FsiHost/Program.fs).
          let projectOutputEnv =
            match primaryProjectOutputDir projects with
            | Some dir -> [ ProjectOutputEnvironmentVariable, dir ]
            | None -> []
          // #141: the project's FSharp.Core, if it differs in build from the host's own (both commonly
          // report the same version), silently loses at runtime — warn now instead of waiting for the
          // MissingMethodException that only shows up when user code happens to hit a missing member. NOT
          // yet rewriting it: see ProjectFSharpCoreIdentity's doc comment for why a late, fsiArgs-string-
          // level rewrite is provably bypassed by ShadowCopy's own re-derivation from the project's
          // original build output, and what the real fix needs.
          match projectFSharpCoreDll fsiArgs with
          | None -> ()
          | Some projectFSharpCore ->
            match detectFSharpCoreMismatchWith fileLengthOrNone (hostFSharpCoreDll dll) projectFSharpCore with
            | Some mismatch -> logger.LogWarning("  " + describeFSharpCoreMismatch mismatch)
            | None -> ()
          let options =
            { HostDll = dll
              Dotnet = dotnet
              FsiArgs = fsiArgs
              WorkingDir = workingDir
              Environment = projectOutputEnv @ RuntimeCompat.rollForwardEnv choice @ Middleware.ValueReadTracking.processEnvironment agent.ValueReads
              OnOutput =
                fun stream text ->
                  match stream with
                  | FsiHost.FsiProtocol.StdOut -> recorder.Write text
                  | FsiHost.FsiProtocol.StdErr -> logger.LogDebug(sprintf "[fsihost stderr] %s" (text.TrimEnd()))
              OnLog = fun line -> logger.LogDebug(sprintf "[fsihost] %s" line)
              StartupTimeoutMs = 120_000 }
          match! start options with
          | Ok host ->
            logger.LogInfo(sprintf "  Isolated FSI host started: %s, FSharp.Core %s (pid %d)" host.Runtime host.FSharpCoreVersion host.ProcessId)
            match! attach host agent with
            | Result.Ok session -> return Ok(session :> IFsiSession)
            | Result.Error reason -> return Error(AgentAttachFailed reason)
          | Error reason -> return Error(HostStartFailed reason)
  }
