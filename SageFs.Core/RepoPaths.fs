/// Runtime-located paths for THIS repository.
///
/// ## Why this exists instead of `__SOURCE_DIRECTORY__`
///
/// In COMPILED code `__SOURCE_DIRECTORY__` is a build-time F# constant: the compiler
/// bakes the source directory into the assembly when it compiles. A test that locates a
/// CHECKED-OUT repository through it therefore measures wherever the assembly was
/// COMPILED, not wherever it RUNS. That is the whole failure: the fixture looks right,
/// the paths look plausible, and it silently resolves against a stale or absent tree.
///
/// (In a `.fsx` SCRIPT the same constant is correct — the FSI host expands it per run,
/// naming the script's real location regardless of the working directory. That is why
/// the script locators in `scripts/` keep it as the START of an upward walk. This file
/// is the answer for the compiled case only.)
///
/// The fix is to resolve at RUNTIME, from the executing assembly's own location, and
/// walk up until a marker that only a real checkout has. Every path here is built with
/// `System.IO.Path`, so the composed result is platform-independent by construction and
/// no absolute path is ever written into source.
///
/// ## How to use it
///
/// This module declares NO namespace — it is a global-namespace
/// `[<RequireQualifiedAccess>] module` — so `open SageFs.RepoPaths` fails with FS0039.
/// Call it fully qualified:
///
/// ```fsharp
/// RepoPaths.repoPath [| "samples"; "getting-started.fsx" |]
/// ```
///
/// A fixture sitting BESIDE a test's source file is NOT at the repo root; for that,
/// use the assembly's own directory (see `design.md`).
///
/// Prefer `Path.Combine` over string concatenation with separators: it is what makes
/// this platform-independent, and it is why this file exists at all.
///
/// The values live in a `[<RequireQualifiedAccess>]` module rather than directly in the
/// namespace: F# forbids value declarations in a `namespace`, and qualifying every call
/// (`RepoPaths.repoRoot ()`) also keeps the helper from colliding with a call site's own
/// `Path`/`File` opens.
[<RequireQualifiedAccess>]
module RepoPaths

open System
open System.IO

/// The file that identifies a directory as the root of THIS repository. A checkout has
/// exactly one at its root; a build output directory, a temp dir, or a published package
/// does not.
[<Literal>]
let private RepoMarker = "SageFs.slnx"

/// How far up to walk looking for the marker before giving up.
///
/// A CI agent can build into a deep path, and an artifact can be unpacked somewhere
/// unusual, so this is generous on purpose — but it is FINITE, so a genuinely missing
/// checkout produces a clear failure instead of an unbounded walk.
[<Literal>]
let private MaxWalkDepth = 24

/// Is `dir` the root of this repository?
///
/// Probes both as a FILE and a DIRECTORY, because `.git` is a directory in a normal
/// checkout and a FILE in a worktree — and a probe that only checks one of the two
/// silently fails on half of every real repository. `RepoMarker` is the primary signal
/// (a source file, never ambiguous); `.git` is accepted as well so the helper still
/// works in a checkout where the solution file has been removed.
let isRepoRoot (dir: string) : bool =
    if String.IsNullOrWhiteSpace dir then
        false
    else
        let marker = Path.Combine(dir, RepoMarker)
        let git = Path.Combine(dir, ".git")
        File.Exists marker || Directory.Exists marker || File.Exists git || Directory.Exists git

/// Walk from `start` towards the filesystem root, returning the first repository root.
///
/// Returns `None` when no ancestor qualifies, which is the honest answer: "I looked and
/// there was nothing" is not the same as "here is the root" — and a caller that treats
/// the failure as success ends up reading a fixture from the wrong tree.
let findRepoRootUpward (start: string) : string option =
    if String.IsNullOrWhiteSpace start then
        None
    else
    // `GetFullPath` collapses `..` and `.` LEXICALLY. A trailing separator survives
    // it and would defeat the directory probe below, so trim to the canonical form
    // FIRST — and never trim a filesystem root to the empty string.
    let mutable dir =
        try
            let full = Path.GetFullPath start
            let root = Path.GetPathRoot full
            if full = root then full else Path.TrimEndingDirectorySeparator full
        with _ ->
            start

    let mutable depth = 0
    let mutable found = None

    while found.IsNone && depth <= MaxWalkDepth && not (String.IsNullOrEmpty dir) do
        if isRepoRoot dir then
            found <- Some dir
        else
            let parent = Path.GetDirectoryName dir
            // `GetDirectoryName` returns null at a filesystem root, which is the
            // natural end of the walk.
            if String.IsNullOrEmpty parent || parent = dir then
                dir <- ""
            else
                dir <- Path.TrimEndingDirectorySeparator parent
            depth <- depth + 1

    found

/// The root of the repository this assembly was BUILT INTO, found at runtime.
///
/// ## Why `Assembly.Location` and NOT `AppContext.BaseDirectory`
///
/// `AppContext.BaseDirectory` is the base directory of the currently-running HOST, not
/// of the assembly that called it. Measured under `dotnet fsi`, where a script `#r`s a
/// compiled assembly:
///
/// ```
/// AppContext.BaseDirectory      = /home/will/.dotnet/sdk/11.0.100-rc.1.26425.128/FSharp/
/// SageFs.Core assembly Location = /home/will/Work/SageFs/SageFs.Core/bin/Debug/net11.0/SageFs.Core.dll
/// ```
///
/// So walking up from `BaseDirectory` walks up from the F# COMPILER's install directory,
/// finds no repository, and a script run from a perfectly good checkout cannot locate it.
/// That is the same class of bug as the build-time constant, wearing a runtime hat: a
/// path that measures the wrong thing while looking perfectly reasonable.
///
/// `typeof<...>.Assembly.Location` is the assembly's own directory, which is a property
/// of the RUN and survives both scenarios. A single-file publish has an empty `Location`,
/// so that case falls through to the working directory rather than throwing — `None` is
/// the honest answer there, not a fabricated path.
let assemblyRepoRoot () : string option =
    let asmDir =
        try
            let loc = Reflection.Assembly.GetExecutingAssembly().Location
            if String.IsNullOrWhiteSpace loc then None else Some loc
        with _ ->
            // A shadow-copied or otherwise relocated assembly can refuse to report its
            // location. Falling back to the host base directory is still better than
            // inventing a path, and the caller falls back again if it is not in a repo.
            Some AppContext.BaseDirectory

    match asmDir with
    | None -> None
    | Some loc ->
        match Path.GetDirectoryName loc with
        | d when not (String.IsNullOrWhiteSpace d) -> findRepoRootUpward d
        | _ -> findRepoRootUpward AppContext.BaseDirectory

/// The root of the repository the CALLER is running in, found at runtime.
///
/// This is the correct answer for scripts and tools, which run from a directory the user
/// chose. It walks up from the working directory, so `dotnet fsi scripts/foo.fsx` works
/// from anywhere inside the checkout. It is deliberately NOT cached across calls, because
/// a test may change the working directory between cases and a cached value would
/// silently describe the first one.
let cwdRepoRoot () : string option =
    findRepoRootUpward (Directory.GetCurrentDirectory())

/// The repository root, from whichever signal is available.
///
/// Order matters: an explicit `SAGEFS_REPO_ROOT` wins (an operator who knows where their
/// checkout is should not have to be guessed at), then the assembly's own location (the
/// most trustworthy, because it is where this code provably came from), then the working
/// directory for a script with no assembly of its own.
///
/// Never returns a hardcoded path, so a checkout anywhere on the machine works.
let repoRoot () : string option =
    let fromEnv =
        match Environment.GetEnvironmentVariable "SAGEFS_REPO_ROOT" with
        | null
        | "" -> None
        | v -> Some v

    match fromEnv with
    | Some r -> findRepoRootUpward r
    | None ->
        match assemblyRepoRoot () with
        | Some r -> Some r
        | None -> cwdRepoRoot ()

/// The repository root, or a thrown error that says where it looked.
///
/// For call sites that genuinely cannot proceed without it — most tests that read a
/// checked-in fixture. The message names BOTH signals tried, because "file not found"
/// without that is a debugging session and with it is one line.
let requireRepoRoot () : string =
    match repoRoot () with
    | Some r -> r
    | None ->
        failwithf
            "Could not locate the %s repository. Looked upward from the assembly location (%s) and the working directory (%s). \
             Run from inside a checkout, or set SAGEFS_REPO_ROOT to point at it."
            RepoMarker
            (AppContext.BaseDirectory)
            (Directory.GetCurrentDirectory())

/// `requireRepoRoot` joined with one or more relative segments.
///
/// The single place relative paths are built, so no call site concatenates separators
/// by hand.
let repoPath ([<ParamArray>] segments: string array) : string =
    segments |> Array.fold (fun acc seg -> Path.Combine(acc, seg)) (requireRepoRoot ())

/// `repoPath`, resolved to a canonical full path.
///
/// `Path.GetFullPath` collapses `.` and `..` without touching the filesystem, so this is
/// free and makes two spellings of one location compare equal.
let repoPathFull ([<ParamArray>] segments: string array) : string =
    repoPath segments |> Path.GetFullPath