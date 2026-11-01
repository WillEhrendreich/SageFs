module SageFs.Tests.ManagedAssetsTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

/// A restore's `project.assets.json`, cut down to what the managed reader looks at. The shapes are the ones a real restore
/// wrote for a class library that references SkiaSharp: the package's `lib/` assembly is under `runtime`, the same
/// assembly's reference copy is under `compile`, a placeholder is `_._`, and a project reference has type `project`.
let assetsJson (folderA: string) (folderB: string) =
  sprintf
    """{
  "version": 3,
  "targets": {
    "net10.0": {
      "SkiaSharp/4.153.1": { "type": "package", "compile": { "ref/net6.0/SkiaSharp.dll": {} }, "runtime": { "lib/net6.0/SkiaSharp.dll": {} } },
      "Placeholder/1.0.0": { "type": "package", "runtime": { "lib/net10.0/_._": {} } },
      "NoFolder/2.0.0": { "type": "package", "runtime": { "lib/net10.0/NoFolder.dll": {} } },
      "SiblingProj/1.0.0": { "type": "project", "runtime": { "bin/placeholder/SiblingProj.dll": {} } }
    },
    "net10.0/linux-x64": { "SkiaSharp/4.153.1": { "type": "package", "runtime": { "lib/net6.0/OtherTarget.dll": {} } } },
    "net8.0": { "SkiaSharp/4.153.1": { "type": "package", "runtime": { "lib/net6.0/OldTarget.dll": {} } } }
  },
  "libraries": {
    "SkiaSharp/4.153.1": { "type": "package", "path": "skiasharp/4.153.1" },
    "Placeholder/1.0.0": { "type": "package", "path": "placeholder/1.0.0" },
    "SiblingProj/1.0.0": { "type": "project", "path": "../SiblingProj/SiblingProj.fsproj" }
  },
  "packageFolders": { "%s": {}, "%s": {} }
}"""
    folderA
    folderB

let private full (path: string) = Path.GetFullPath path

[<Tests>]
let tests =
  testList "ManagedAssets" [
    testList "read" [
      let folderA = "/cache/a"
      let folderB = "/cache/b"
      let json = assetsJson folderA folderB
      // The package is only in the SECOND package folder, as with a fallback folder listed first.
      let directoryExists (path: string) = [ "/cache/b/skiasharp/4.153.1"; "/cache/a/placeholder/1.0.0" ] |> List.map full |> List.contains (full path)
      let fileExists (path: string) = [ "/cache/b/skiasharp/4.153.1/lib/net6.0/SkiaSharp.dll" ] |> List.map full |> List.contains (full path)

      testCase "WHY — a class library's package assemblies come from its restore, because its build output does not copy them" <| fun () ->
        ManagedAssets.read directoryExists fileExists 10 json
        |> Expect.equal "the one package assembly that exists, found in the second package folder" (AssembliesRead [ full "/cache/b/skiasharp/4.153.1/lib/net6.0/SkiaSharp.dll" ])

      testCase "WHY — a placeholder, a project reference, a package with no folder and a file that is not on disk add nothing" <| fun () ->
        ManagedAssets.read directoryExists (fun _ -> false) 10 json
        |> Expect.equal "nothing exists on disk, so nothing is referenced" (AssembliesRead [])

      testCase "WHY — only the runtime assets are read, so a reference-only assembly can never be handed to FSI" <| fun () ->
        match ManagedAssets.read directoryExists (fun _ -> true) 10 json with
        | AssembliesRead paths ->
          paths |> List.exists (fun p -> p.Contains "/ref/") |> Expect.isFalse "no ref/ assembly"
          paths |> List.exists (fun p -> p.EndsWith "_._") |> Expect.isFalse "no placeholder"
          paths |> List.exists (fun p -> p.Contains "OtherTarget" || p.Contains "OldTarget") |> Expect.isFalse "only the chosen target is read"
        | RestoreUnreadable reason -> failtestf "should read: %s" reason

      testCase "WHY — a file that is not a restore is reported, so a broken fallback is not read as a library with no packages" <| fun () ->
        match ManagedAssets.read directoryExists fileExists 10 "{ not json" with
        | RestoreUnreadable _ -> ()
        | AssembliesRead _ -> failtest "malformed JSON must be reported as unreadable"

      testCase "an assets file with no targets references nothing" <| fun () ->
        ManagedAssets.read directoryExists fileExists 10 "{}" |> Expect.equal "empty" (AssembliesRead [])
    ]

    testList "chooseTarget" [
      for runtimeMajor, targets, expected in
        [ 10, [ "net10.0"; "net10.0/linux-x64"; "net8.0" ], Some "net10.0"
          10, [ "net10.0/linux-x64"; "net10.0" ], Some "net10.0"
          10, [ "net8.0"; "net9.0" ], Some "net9.0"
          10, [ "net11.0"; "net12.0" ], Some "net11.0"
          10, [ "netstandard2.0"; "net48" ], None
          10, [], None ] do
        testCase (sprintf "WHY — runtime %d over %A reads %A, the closest target that can run on it" runtimeMajor targets expected) <| fun () ->
          ManagedAssets.chooseTarget runtimeMajor targets |> Expect.equal "the chosen target" expected
    ]

    testList "mergeReferences" [
      testCase "WHY — a copy in bin wins over the package's, so the project's own dependency version is the one FSI loads" <| fun () ->
        let bin = [ "/p/bin/Debug/net10.0/Foo.dll"; "/p/bin/Debug/net10.0/P.dll" ]
        let packages = [ "/cache/foo/1/lib/net6.0/foo.dll"; "/cache/bar/1/lib/net6.0/Bar.dll" ]
        ProjectLoading.ManualProjectParse.mergeReferences bin packages
        |> Expect.equal "bin copies first, then only the package assemblies bin does not hold"
             [ "/p/bin/Debug/net10.0/Foo.dll"; "/p/bin/Debug/net10.0/P.dll"; "/cache/bar/1/lib/net6.0/Bar.dll" ]

      testCase "no package assemblies leaves the bin list as it was" <| fun () ->
        ProjectLoading.ManualProjectParse.mergeReferences [ "/p/bin/a.dll" ] [] |> Expect.equal "unchanged" [ "/p/bin/a.dll" ]
    ]

    testList "collectPackageReferences" [
      let withRestore (layout: string -> string) (run: string -> string -> unit) =
        let root = Directory.CreateTempSubdirectory("sagefs-managed-").FullName
        try
          let cache = Path.Combine(root, "cache")
          let packageDll = Path.Combine(cache, "skiasharp", "4.153.1", "lib", "net6.0", "SkiaSharp.dll")
          Directory.CreateDirectory(Path.GetDirectoryName packageDll) |> ignore
          File.WriteAllText(packageDll, "")
          let json = assetsJson cache (Path.Combine(root, "unused"))
          let projectFile = layout root
          let projDir = Path.GetDirectoryName projectFile
          Directory.CreateDirectory projDir |> ignore
          File.WriteAllText(projectFile, "<Project />")
          run projectFile json
        finally
          Directory.Delete(root, true)

      testCase "WHY — a conventional project finds its restore in obj and references its packages" <| fun () ->
        withRestore (fun root -> Path.Combine(root, "Lib", "Lib.fsproj")) (fun projectFile json ->
          let obj = Path.Combine(Path.GetDirectoryName projectFile, "obj")
          Directory.CreateDirectory obj |> ignore
          File.WriteAllText(Path.Combine(obj, "project.assets.json"), json)
          let refs = ProjectLoading.ManualProjectParse.collectPackageReferences ManualProjectParseTests.quietLogger 10 [ projectFile ]
          refs |> List.map Path.GetFileName |> Expect.equal "the package assembly is referenced" [ "SkiaSharp.dll" ])

      testCase "WHY — an Arcade-style repo keeps its restore under artifacts/obj/<Project>, and is found from the project's directory" <| fun () ->
        withRestore (fun root -> Path.Combine(root, "src", "Lib", "Lib.fsproj")) (fun projectFile json ->
          let root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName projectFile))
          let objDir = Path.Combine(root, "artifacts", "obj", "Lib")
          Directory.CreateDirectory objDir |> ignore
          File.WriteAllText(Path.Combine(objDir, "project.assets.json"), json)
          let refs = ProjectLoading.ManualProjectParse.collectPackageReferences ManualProjectParseTests.quietLogger 10 [ projectFile ]
          refs |> List.map Path.GetFileName |> Expect.equal "the package assembly is referenced" [ "SkiaSharp.dll" ])

      testCase "WHY — a project that was never restored keeps today's behavior, with no references added" <| fun () ->
        withRestore (fun root -> Path.Combine(root, "Lib", "Lib.fsproj")) (fun projectFile _ ->
          ProjectLoading.ManualProjectParse.collectPackageReferences ManualProjectParseTests.quietLogger 10 [ projectFile ]
          |> Expect.isEmpty "nothing to read")
    ]
  ]
