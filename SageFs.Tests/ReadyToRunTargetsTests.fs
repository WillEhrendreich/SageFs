/// The ReadyToRun step is speed only, and its own comment says so: "It is speed only and fails
/// open... Nothing here fails a build or a pack."
///
/// That was not true of the cache cleanup. Its condition read every cached file's timestamp with
/// `DateTime.Parse`, and `Parse('')` throws, so an entry whose timestamp MSBuild could not read
/// FAILED THE BUILD (MSB4184) instead of the step skipping. The release gate's pack stage hit it
/// on 2026-10-19 with every tier green, and 0.6.914 did not go out.
///
/// An entry's timestamp can be unreadable because another build deleted it between the cleanup's
/// glob and its condition: the two target frameworks build beside each other and share the cache,
/// and the 14-day cleanup only started deleting anything on 2026-10-19, the day the oldest cached
/// entries crossed that line.
///
/// This runs the REAL condition, taken out of the REAL targets file, over an entry that has no
/// timestamp at all, and says what MSBuild said.
module SageFs.Tests.ReadyToRunTargetsTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open System.Xml.Linq
open SageFs.Tests.TestInfrastructure

/// The targets file, from the repo root.
let private targetsFile () =
  Path.Combine(RepoPaths.repoPathFull [||], "SageFs.Host", "ReadyToRun.targets")

/// The cleanup's own condition, read out of the targets file. Taken from the file rather than
/// written out here, so this test cannot drift from what the build does.
let private cacheFreshCondition () =
  let doc = XDocument.Load (targetsFile ())
  doc.Descendants()
  |> Seq.tryFind (fun e -> e.Name.LocalName = "_R2RCacheFresh")
  |> Option.bind (fun e ->
    match e.Attribute(XName.Get "Condition") with
    | null -> None
    | a -> Some a.Value)
  |> Option.defaultValue "MISSING"

/// A project that runs the real cleanup condition over one entry that has no timestamp: the item
/// is a literal, not a file, so MSBuild gives it no file metadata at all. The other entry is a real
/// file, so the glob's half of the target is exercised too.
///
/// The item groups are inside a target, like the ones they are copied from: that is where MSBuild
/// expands the metadata reference, and an entry with no timestamp expands to the empty string,
/// which is what the gate's failure was. At project level the same reference is left as text, so
/// the probe would fail for a different reason than the build does.
let private probeProject (cache: string) (outDir: string) (condition: string) : string =
  let project = XElement(XName.Get "Project")
  let propertyGroup name value =
    XElement(XName.Get "PropertyGroup", XElement(XName.Get name, value :> obj))
  project.Add(propertyGroup "SageFsReadyToRunCache" cache)
  project.Add(propertyGroup "SageFsReadyToRunCacheDays" "14")
  project.Add(propertyGroup "_R2RCache" (cache + "/"))
  project.Add(propertyGroup "_R2RHostDir" (outDir + "/host/"))
  project.Add(
    XElement(
      XName.Get "Target",
      XAttribute(XName.Get "Name", "Go"),
      XElement(
        XName.Get "ItemGroup",
        XElement(XName.Get "_R2RCacheEntry", XAttribute(XName.Get "Include", cache + "/*.dll")),
        XElement(XName.Get "_R2RCacheEntry", XAttribute(XName.Get "Include", "an-entry-with-no-timestamp.dll"))),
      XElement(
        XName.Get "ItemGroup",
        XElement(
          XName.Get "_R2RCacheFresh",
          XAttribute(XName.Get "Include", "@(_R2RCacheEntry)"),
          XAttribute(XName.Get "Condition", condition)))))
  XDocument(XDeclaration("1.0", "utf-8", "yes"), [| project :> obj |]).ToString()

[<Tests>]
let tests =
  testList "ReadyToRun targets" [

    testTask "the cache cleanup does not fail a build on a cached file MSBuild cannot stat" {
      let condition = cacheFreshCondition ()
      condition
      |> Expect.notEqual "the cleanup's condition was found in the targets file" "MISSING"
      let dir = Path.Combine(Path.GetTempPath(), "sagefs-r2r-cache-probe-" + Guid.NewGuid().ToString "N")
      try
        let cache = Path.Combine(dir, "cache")
        let outDir = Path.Combine(dir, "out")
        Directory.CreateDirectory cache |> ignore
        Directory.CreateDirectory outDir |> ignore
        File.WriteAllText(Path.Combine(cache, "a-real-cached-image.dll"), "x")
        let probe = Path.Combine(dir, "probe.proj")
        File.WriteAllText(probe, probeProject cache outDir condition)
        let psi = ProcessStartInfo("dotnet")
        psi.ArgumentList.Add "msbuild"
        psi.ArgumentList.Add probe
        psi.ArgumentList.Add "-t:Go"
        psi.ArgumentList.Add "-v:m"
        psi.ArgumentList.Add "-nologo"
        psi.UseShellExecute <- false
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        let proc = Process.Start psi
        try
          let output = proc.StandardOutput.ReadToEndAsync()
          let errors = proc.StandardError.ReadToEndAsync()
          let bound = new CancellationTokenSource(TestTimeouts.readyToRunCacheProbe)
          try
            do! proc.WaitForExitAsync bound.Token
            let! text = output
            let! errorText = errors
            let all = text + errorText
            proc.ExitCode
            |> Expect.equal (sprintf "the cache cleanup is speed only and must not fail the build; MSBuild said:\n%s" all) 0
          finally
            bound.Dispose()
        finally
          try if not proc.HasExited then proc.Kill true with _ -> ()
          try proc.Dispose() with _ -> ()
      finally
        try if Directory.Exists dir then Directory.Delete(dir, true) with _ -> ()
    }
  ]
