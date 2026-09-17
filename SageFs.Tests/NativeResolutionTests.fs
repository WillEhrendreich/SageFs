module SageFs.Tests.NativeResolutionTests

open System.IO
open System.Runtime.InteropServices
open Expecto
open Expecto.Flip
open SageFs

/// RED→GREEN tests for the Raylib worker-crash fix (fix/raylib-worker-crash).
///
/// Root cause (proven via a managed crash dump, not a hypothesis): hosting a
/// Raylib-cs project made `Raylib.InitWindow` P/Invoke `libraylib.so`, which the
/// default CoreCLR probe never finds in the project output's
/// `runtimes/<rid>/native/`. The resulting DllNotFoundException, thrown on a
/// user-spawned background thread, escalated to a process FailFast that killed
/// the worker. `NativeResolution.candidatePaths` is the pure decision that makes
/// that native lib discoverable; these tests pin the policy.
[<Tests>]
let tests = testList "NativeResolution" [

  testCase "WHY — libraylib.so is the preferred (decorated) candidate on Linux" <| fun () ->
    NativeResolution.fileNameCandidates false false "raylib"
    |> List.head
    |> Expect.equal "linux decorates as lib<name>.so first" "libraylib.so"

  testCase "WHY — Linux name candidates cover decorated, ext-only, and bare forms" <| fun () ->
    NativeResolution.fileNameCandidates false false "raylib"
    |> Expect.equal "expected the three linux shapes" [ "libraylib.so"; "raylib.so"; "raylib" ]

  testCase "WHY — Windows uses .dll with no lib prefix" <| fun () ->
    NativeResolution.fileNameCandidates true false "raylib"
    |> Expect.equal "windows shapes" [ "raylib.dll"; "raylib" ]

  testCase "WHY — macOS uses lib*.dylib" <| fun () ->
    NativeResolution.fileNameCandidates false true "raylib"
    |> Expect.equal "osx shapes" [ "libraylib.dylib"; "raylib.dylib"; "raylib" ]

  testCase "WHY — an already-decorated name is not double-decorated" <| fun () ->
    NativeResolution.fileNameCandidates false false "libraylib.so"
    |> List.head
    |> Expect.equal "keeps a single lib prefix and .so" "libraylib.so"

  testCase "WHY — candidatePaths probes runtimes/<rid>/native under each root" <| fun () ->
    let root = Path.Combine("/proj", "bin", "Release", "net10.0")
    let expected = Path.GetFullPath(Path.Combine(root, "runtimes", "linux-x64", "native", "libraylib.so"))
    NativeResolution.candidatePaths [ root ] [ "linux-x64" ] false false "raylib"
    |> List.contains expected
    |> Expect.isTrue "must include the runtimes/<rid>/native path where the build copies the lib"

  testCase "WHY — the flat root dir is probed before the runtimes subdir" <| fun () ->
    let root = Path.GetFullPath(Path.Combine("/proj", "out"))
    let paths = NativeResolution.candidatePaths [ root ] [ "linux-x64" ] false false "raylib"
    let flat = Path.GetFullPath(Path.Combine(root, "libraylib.so"))
    let nested = Path.GetFullPath(Path.Combine(root, "runtimes", "linux-x64", "native", "libraylib.so"))
    (List.findIndex ((=) flat) paths < List.findIndex ((=) nested) paths)
    |> Expect.isTrue "flat directory candidate must come before the runtimes/native candidate"

  testCase "WHY — a rooted library name is returned verbatim (no re-decoration)" <| fun () ->
    let abs = Path.GetFullPath("/opt/native/libcustom.so")
    NativeResolution.candidatePaths [ "/proj/out" ] [ "linux-x64" ] false false abs
    |> Expect.equal "rooted passthrough" [ abs ]

  testCase "WHY — every produced candidate is an absolute, de-duplicated path" <| fun () ->
    let paths =
      NativeResolution.candidatePaths
        [ Path.Combine("/a", "b"); Path.Combine("/a", "b") ]   // duplicate root
        [ "linux-x64"; "linux-x64" ]                            // duplicate rid
        false false "raylib"
    paths |> List.forall Path.IsPathRooted
    |> Expect.isTrue "all candidates are rooted"
    (List.length paths = List.length (List.distinct paths))
    |> Expect.isTrue "no duplicate candidates"

  testCase "WHY — a blank library name yields no candidates (never throws)" <| fun () ->
    NativeResolution.candidatePaths [ "/proj/out" ] [ "linux-x64" ] false false ""
    |> Expect.isEmpty "blank name is a no-op, not a crash"

  // Integration: the resolver, given the ACTUAL built RaylibHello output dir,
  // must surface an on-disk candidate — proving the fix resolves the real
  // libraylib.so that the default probe missed. Skips cleanly if the sample
  // has not been built (e.g. a partial CI matrix), so the suite stays green.
  testCase "WHY — resolver finds the real libraylib.so in the built sample output" <| fun () ->
    // Derive repo root from this source file's location (no hardcoded paths).
    let repoRoot =
      let here = __SOURCE_DIRECTORY__   // .../SageFs.Tests
      Path.GetFullPath(Path.Combine(here, ".."))
    let sampleBin =
      Path.Combine(repoRoot, "samples", "demos", "SageFs.Samples.RaylibHello", "bin")
    // Only meaningful when the sample was built WITH its native lib for this
    // RID (some configs/matrix legs may not deposit it); skip otherwise so the
    // suite stays green rather than asserting against something not on disk.
    let anyNativeBuilt =
      Directory.Exists sampleBin
      && (Directory.EnumerateFiles(sampleBin, "*raylib*", SearchOption.AllDirectories) |> Seq.isEmpty |> not)
    match anyNativeBuilt with
    | false -> skiptest "RaylibHello native lib not built for this RID"
    | true ->
      // Pass every build-output dir as a root; the resolver must surface the
      // native lib from whichever config actually deposited it.
      let outDirs =
        Directory.EnumerateDirectories sampleBin
        |> Seq.collect Directory.EnumerateDirectories
        |> Seq.toList
      let rids = NativeResolution.currentRids ()
      let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
      let isOsx = RuntimeInformation.IsOSPlatform OSPlatform.OSX
      NativeResolution.candidatePaths outDirs rids isWindows isOsx "raylib"
      |> List.exists File.Exists
      |> Expect.isTrue "a native candidate must exist on disk under a built output dir"
]
