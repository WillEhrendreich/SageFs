module SageFs.Tests.NativeAssetsTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

/// A restore's `project.assets.json`, cut down to what native resolution reads. The shapes are the ones a real restore
/// wrote for a project that references SkiaSharp (a managed package that depends on a native package per platform).
let assetsJson (folder: string) =
  sprintf
    """{
  "version": 3,
  "targets": {
    "net10.0": {
      "SkiaSharp/2.88.8": { "type": "package", "runtime": { "lib/net6.0/SkiaSharp.dll": {} } },
      "SkiaSharp.NativeAssets.Linux.NoDependencies/2.88.8": {
        "type": "package",
        "runtimeTargets": {
          "runtimes/linux-arm64/native/libSkiaSharp.so": { "assetType": "native", "rid": "linux-arm64" },
          "runtimes/linux-x64/native/libSkiaSharp.so": { "assetType": "native", "rid": "linux-x64" }
        }
      },
      "SkiaSharp.NativeAssets.Win32/2.88.8": {
        "type": "package",
        "runtimeTargets": {
          "runtimes/win-x64/native/libSkiaSharp.dll": { "assetType": "native", "rid": "win-x64" },
          "runtimes/win-x86/native/libSkiaSharp.dll": { "assetType": "native", "rid": "win-x86" }
        }
      }
    }
  },
  "libraries": {
    "SkiaSharp/2.88.8": { "type": "package", "path": "skiasharp/2.88.8" },
    "SkiaSharp.NativeAssets.Linux.NoDependencies/2.88.8": { "type": "package", "path": "skiasharp.nativeassets.linux.nodependencies/2.88.8" },
    "SkiaSharp.NativeAssets.Win32/2.88.8": { "type": "package", "path": "skiasharp.nativeassets.win32/2.88.8" }
  },
  "packageFolders": { "%s": {} }
}"""
    (folder.Replace('\\', '/'))

let packagesRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sagefs-native-assets-packages"))

let everyFolderExists (_: string) = true

let read (json: string) : NativePackage list =
  match NativeAssets.read everyFolderExists json with
  | Result.Ok packages -> packages
  | Result.Error why -> failtestf "the assets file should read: %s" why

let linux = NativeAssets.compatibleRuntimes "linux-x64" "linux" "x64"

let namedPackage (name: string) (packages: NativePackage list) : NativePackage =
  packages |> List.find (fun p -> p.Name = name)

let notFound (library: string) =
  DllNotFoundException(sprintf "Unable to load shared library '%s' or one of its dependencies. In order to help diagnose loading problems, consider using a tool like strace." library)

let factsFor (packages: NativePackage list) : NativeFacts =
  { Runtime = "linux-x64"; Compatible = linux; Packages = packages }

[<Tests>]
let tests =
  testList "NativeAssets" [

    testCase "WHY — the runtimes a native file may be for on Linux include the running one, os-arch, the bare os and the unix family, because that is the order NuGet's runtime graph resolves them in" <| fun _ ->
      NativeAssets.compatibleRuntimes "linux-x64" "linux" "x64"
      |> Expect.equal "most specific first" [ "linux-x64"; "linux"; "unix-x64"; "unix" ]

    testCase "WHY — Windows has no unix family, so a unix asset is never offered to a Windows process" <| fun _ ->
      NativeAssets.compatibleRuntimes "win-x64" "win" "x64"
      |> Expect.equal "windows only" [ "win-x64"; "win" ]

    testCase "WHY — a distro runtime id that differs from os-arch is kept first and the portable one still follows" <| fun _ ->
      NativeAssets.compatibleRuntimes "linux-musl-x64" "linux" "x64"
      |> Expect.equal "musl first, then portable" [ "linux-musl-x64"; "linux-x64"; "linux"; "unix-x64"; "unix" ]

    testCase "WHY — a library's stem drops the directory, the lib prefix, the extension and a version suffix, so libSkiaSharp.so and SkiaSharp.dll are one library" <| fun _ ->
      [ "/x/libSkiaSharp.so"; "SkiaSharp.dll"; "libSkiaSharp.dylib"; "libsodium.so.26.4.0" ]
      |> List.map NativeAssets.libraryStem
      |> Expect.equal "stems" [ "skiasharp"; "skiasharp"; "skiasharp"; "sodium" ]

    testCase "WHY — reading an assets file finds every package that ships a native file and joins its path to the package folder that holds it" <| fun _ ->
      let packages = read (assetsJson packagesRoot)
      packages |> List.map (fun p -> p.Name) |> List.sort
      |> Expect.equal "only the packages with native files" [ "SkiaSharp.NativeAssets.Linux.NoDependencies"; "SkiaSharp.NativeAssets.Win32" ]
      let linuxPackage = namedPackage "SkiaSharp.NativeAssets.Linux.NoDependencies" packages
      linuxPackage.Assets
      |> List.map (fun a -> a.Scope, a.File)
      |> Expect.contains
           "the linux-x64 file under the package's own folder"
           (ForRuntime "linux-x64",
            Path.GetFullPath(Path.Combine(packagesRoot, "skiasharp.nativeassets.linux.nodependencies", "2.88.8", "runtimes", "linux-x64", "native", "libSkiaSharp.so")))

    testCase "WHY — a restore that lists a fallback folder first still resolves to the folder that really holds the package" <| fun _ ->
      let fallback = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sagefs-native-assets-fallback"))
      let fallbackEntry = sprintf "\"%s\": {}" (fallback.Replace('\\', '/'))
      let json = (assetsJson fallback).Replace(fallbackEntry, sprintf "%s, \"%s\": {}" fallbackEntry (packagesRoot.Replace('\\', '/')))
      let holdsPackages (dir: string) = dir.StartsWith(packagesRoot, StringComparison.Ordinal)
      match NativeAssets.read holdsPackages json with
      | Result.Error why -> failtestf "should read: %s" why
      | Result.Ok packages ->
        packages
        |> List.collect (fun p -> p.Assets)
        |> List.forall (fun a -> a.File.StartsWith(packagesRoot, StringComparison.Ordinal))
        |> Expect.isTrue "every file is under the folder that exists"

    testCase "WHY — a package whose folder cannot be found has nothing the host could load, so it is left out instead of named with a path that does not exist" <| fun _ ->
      match NativeAssets.read (fun _ -> false) (assetsJson packagesRoot) with
      | Result.Ok packages -> packages |> Expect.isEmpty "no folder, no packages"
      | Result.Error why -> failtestf "should read: %s" why

    testCase "WHY — text that is not an assets file is an error that names what was wrong, not an empty answer that reads as 'no native libraries'" <| fun _ ->
      match NativeAssets.read everyFolderExists "{ not json" with
      | Result.Error why -> why |> Expect.isNotEmpty "says why"
      | Result.Ok _ -> failtest "a broken file must not read as an empty restore"

    testCase "WHY — an entry in a RID-specific restore's own native group is for that restore, so it applies on any machine that restore was run for" <| fun _ ->
      let json =
        """{ "targets": { "net10.0/linux-x64": { "Acme.Native/1.0.0": { "type": "package", "native": { "runtimes/linux-x64/native/libacme.so": {} } } } },
             "libraries": { "Acme.Native/1.0.0": { "type": "package", "path": "acme.native/1.0.0" } },
             "packageFolders": { "/packages": {} } }"""
      let packages = read json
      packages |> List.collect (fun p -> p.Assets |> List.map (fun a -> a.Scope))
      |> Expect.equal "scoped to the restore" [ ForRestoredTarget ]
      NativeAssets.directories linux packages |> List.length |> Expect.equal "one directory" 1

    testCase "WHY — a package listed under two targets of a multi-target restore is one package with one copy of each file" <| fun _ ->
      let json = (assetsJson packagesRoot).Replace("\"targets\": {", "\"targets\": { \"net8.0\": { \"SkiaSharp.NativeAssets.Win32/2.88.8\": { \"type\": \"package\", \"runtimeTargets\": { \"runtimes/win-x64/native/libSkiaSharp.dll\": { \"assetType\": \"native\", \"rid\": \"win-x64\" } } } },")
      let win = read json |> namedPackage "SkiaSharp.NativeAssets.Win32"
      win.Assets |> List.length |> Expect.equal "win-x64 and win-x86 once each" 2

    testCase "WHY — the directories to search are the folders of the native files for THIS machine only, so a Windows dll's folder is never probed on Linux" <| fun _ ->
      NativeAssets.directories linux (read (assetsJson packagesRoot))
      |> Expect.equal
           "just the linux-x64 folder"
           [ Path.GetFullPath(Path.Combine(packagesRoot, "skiasharp.nativeassets.linux.nodependencies", "2.88.8", "runtimes", "linux-x64", "native")) ]

    testCase "WHY — a package that ships natives for other platforms is NOT a gap when another package provides the same library here, because SkiaSharp depends on a native package per platform and only one can apply" <| fun _ ->
      NativeAssets.gaps linux (read (assetsJson packagesRoot))
      |> Expect.isEmpty "the Windows package is not a gap beside the Linux one"

    testCase "WHY — a package that ships natives only for other platforms IS a gap, and it says which platforms it does ship" <| fun _ ->
      let onlyWindows = read (assetsJson packagesRoot) |> List.filter (fun p -> p.Name = "SkiaSharp.NativeAssets.Win32")
      NativeAssets.gaps linux onlyWindows
      |> Expect.equal
           "named with the runtimes it ships and its library"
           [ { Name = "SkiaSharp.NativeAssets.Win32"; Version = "2.88.8"; ShippedRuntimes = [ "win-x64"; "win-x86" ]; LibraryStems = [ "skiasharp" ] } ]

    testCase "WHY — the assets files the daemon names arrive as one path-separated string, and blanks mean none" <| fun _ ->
      let joined = String.Join(string Path.PathSeparator, [ "/a/obj/project.assets.json"; "/b/obj/project.assets.json" ])
      NativeAssets.splitFileList joined |> Expect.equal "two files" [ "/a/obj/project.assets.json"; "/b/obj/project.assets.json" ]
      NativeAssets.splitFileList "" |> Expect.isEmpty "empty"
      NativeAssets.splitFileList "  " |> Expect.isEmpty "blank"

    testCase "WHY — facts from several assets files merge, and a file that cannot be read is reported beside them instead of hiding the rest" <| fun _ ->
      let files = Map.ofList [ "/good/project.assets.json", assetsJson packagesRoot ]
      let readText path =
        match Map.tryFind path files with
        | Some text -> text
        | None -> raise (FileNotFoundException path)
      let facts, problems = NativeAssets.factsFrom readText everyFolderExists "linux-x64" "linux" "x64" [ "/good/project.assets.json"; "/missing/project.assets.json" ]
      facts.Packages |> List.length |> Expect.equal "the good file's two packages" 2
      problems |> List.length |> Expect.equal "the missing file is reported" 1
      problems |> List.head |> Expect.stringContains "and names the file" "/missing/project.assets.json"

    testList "NativeDiagnosis" [

      testCase "WHY — a library that no package here ships for this runtime is named with its package, the runtimes the package does ship, this runtime, and what to do" <| fun _ ->
        let onlyWindows = read (assetsJson packagesRoot) |> List.filter (fun p -> p.Name = "SkiaSharp.NativeAssets.Win32")
        let failure = TypeInitializationException("SkiaSharp.SKImageInfo", notFound "libSkiaSharp")
        match NativeDiagnosis.explain (factsFor onlyWindows) failure with
        | NativeDiagnosis.NotANativeLoadFailure -> failtest "a DllNotFoundException inside a type initializer is a native load failure"
        | NativeDiagnosis.NativeLoadFailure text ->
          text |> Expect.stringContains "names the library" "libSkiaSharp"
          text |> Expect.stringContains "names the package" "SkiaSharp.NativeAssets.Win32 2.88.8"
          text |> Expect.stringContains "names the runtimes it does ship" "win-x64, win-x86"
          text |> Expect.stringContains "names this runtime" "linux-x64"
          text |> Expect.stringContains "says what to do" "Use a version or variant of the package"

      testCase "WHY — when no restored package could have provided the library, the message says so and still gives this runtime and the next step, not a bare loader message" <| fun _ ->
        match NativeDiagnosis.explain (factsFor []) (notFound "libmissing") with
        | NativeDiagnosis.NotANativeLoadFailure -> failtest "should be a native load failure"
        | NativeDiagnosis.NativeLoadFailure text ->
          text |> Expect.stringContains "names the library" "libmissing"
          text |> Expect.stringContains "names this runtime" "linux-x64"
          text |> Expect.stringContains "says what to do" "install the library on this machine"

      testCase "WHY — a gap in some OTHER package is listed without being blamed, when the missing library does not match it" <| fun _ ->
        let onlyWindows = read (assetsJson packagesRoot) |> List.filter (fun p -> p.Name = "SkiaSharp.NativeAssets.Win32")
        match NativeDiagnosis.explain (factsFor onlyWindows) (notFound "libother") with
        | NativeDiagnosis.NotANativeLoadFailure -> failtest "should be a native load failure"
        | NativeDiagnosis.NativeLoadFailure text ->
          text |> Expect.stringContains "lists the package as a possible owner" "These packages ship native libraries but none for linux-x64"
          text |> Expect.stringContains "with its runtimes" "SkiaSharp.NativeAssets.Win32 2.88.8 (ships win-x64, win-x86)"

      testCase "WHY — the loader's exception is found wherever it sits in the chain: inside an aggregate, or under a type initializer under a wrapper" <| fun _ ->
        let wrapped = InvalidOperationException("outer", TypeInitializationException("T", notFound "libx"))
        let aggregate = AggregateException(InvalidOperationException "unrelated", notFound "liby")
        [ wrapped :> exn; aggregate :> exn ]
        |> List.iter (fun failure ->
          match NativeDiagnosis.explain (factsFor []) failure with
          | NativeDiagnosis.NativeLoadFailure _ -> ()
          | NativeDiagnosis.NotANativeLoadFailure -> failtestf "should find the DllNotFoundException in %s" (failure.GetType().Name))

      testCase "WHY — a failure with no native library in it is left alone, so an ordinary exception's message is never rewritten" <| fun _ ->
        NativeDiagnosis.explain (factsFor []) (InvalidOperationException "boom")
        |> Expect.equal "not native" NativeDiagnosis.NotANativeLoadFailure
        NativeDiagnosis.annotate (factsFor []) (InvalidOperationException "boom") "boom"
        |> Expect.equal "the message is unchanged" "boom"

      testCase "WHY — an annotated message keeps the original text first, so the user still sees what .NET said" <| fun _ ->
        let failure = TypeInitializationException("T", notFound "libx")
        NativeDiagnosis.annotate (factsFor []) failure failure.Message
        |> Expect.stringStarts "original first" failure.Message
    ]
  ]
