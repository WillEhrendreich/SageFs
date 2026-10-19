module SageFs.Tests.NativeResolutionTests

open System
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

  // Integration: the resolver, given an output dir laid out the way a build of
  // RaylibHello lays it out, must surface an on-disk candidate — proving the fix
  // resolves the real libraylib.so that the default probe missed. The test makes
  // that output dir itself: a build copies the Raylib-cs package's own
  // `runtimes/<rid>/native/` under the output, so the test copies the same files
  // from the restored package (restoring the sample first if the package is not
  // in the NuGet cache), at the version the repo pins. It needs no earlier build.
  testCase "WHY — resolver finds the real libraylib.so in an output laid out the way the sample's build lays it out" <| fun () ->
    // Derive repo root from the runtime repo-root probe (no hardcoded paths).
    let repoRoot =
      RepoPaths.repoPathFull [||]
    let version =
      System.Xml.Linq.XDocument.Load(Path.Combine(repoRoot, "Directory.Packages.props")).Descendants()
      |> Seq.filter (fun e -> e.Name.LocalName = "PackageVersion" && (string (e.Attribute "Include")).Contains "Raylib-cs")
      |> Seq.map (fun e -> e.Attribute("Version").Value)
      |> Seq.tryHead
      |> Option.defaultWith (fun () -> failtest "Directory.Packages.props pins no Raylib-cs version")
    let nugetRoot =
      match Environment.GetEnvironmentVariable "NUGET_PACKAGES" with
      | null | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".nuget", "packages")
      | configured -> configured
    let package = Path.Combine(nugetRoot, "raylib-cs", version)
    let sample = Path.Combine(repoRoot, "samples", "demos", "SageFs.Samples.RaylibHello", "SageFs.Samples.RaylibHello.fsproj")
    match Directory.Exists package with
    | true -> ()
    | false ->
      let psi = Diagnostics.ProcessStartInfo("dotnet")
      for arg in [ "restore"; sample; "--nologo"; "-v:q" ] do psi.ArgumentList.Add arg
      psi.RedirectStandardOutput <- true
      psi.RedirectStandardError <- true
      use restore = Diagnostics.Process.Start psi
      let output = restore.StandardOutput.ReadToEndAsync()
      let errors = restore.StandardError.ReadToEndAsync()
      restore.WaitForExit(TestTimeouts.asMs TestTimeouts.sessionReadyColdBuild) |> Expect.isTrue "the sample's restore finishes"
      restore.ExitCode |> Expect.equal (sprintf "restoring the sample succeeds. Output: %s %s" output.Result errors.Result) 0
    Directory.Exists package |> Expect.isTrue (sprintf "the Raylib-cs %s package is restored at %s" version package)
    let rids = NativeResolution.currentRids ()
    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isOsx = RuntimeInformation.IsOSPlatform OSPlatform.OSX
    let outDir = Path.Combine(Path.GetTempPath(), "sagefs-raylib-out-" + Guid.NewGuid().ToString "N")
    try
      Directory.CreateDirectory outDir |> ignore
      let shipped =
        rids |> List.tryFind (fun rid -> Directory.Exists(Path.Combine(package, "runtimes", rid, "native")))
      let found () =
        NativeResolution.candidatePaths [ outDir ] rids isWindows isOsx "raylib"
        |> List.exists File.Exists
      match shipped with
      | Some rid ->
        let nativeOut = Path.Combine(outDir, "runtimes", rid, "native")
        Directory.CreateDirectory nativeOut |> ignore
        for file in Directory.EnumerateFiles(Path.Combine(package, "runtimes", rid, "native")) do
          File.Copy(file, Path.Combine(nativeOut, Path.GetFileName file))
        found () |> Expect.isTrue "a native candidate must exist on disk under the output dir"
      | None ->
        // The package ships no native library for this machine's runtime identifiers, so there is nothing for the
        // resolver to surface, and it must say so by finding nothing rather than inventing a path.
        found () |> Expect.isFalse (sprintf "the package ships no native library for %A, so the resolver finds none" rids)
    finally
      try Directory.Delete(outDir, true) with _ -> ()
]
