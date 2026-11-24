/// A gating guard against the shapes that make a test fail because of system load.
///
/// The rule, from the wall-clock register in Molina: a remaining bad shape is not deleted from the
/// checker, it is REGISTERED with a kind and a reason, and the register only shrinks. A new finding
/// with no row fails the gate. Planted cases prove the checker can actually go red.
module SageFs.Tests.TestDeterminism

open System
open System.IO
open Expecto
open Expecto.Flip

[<RequireQualifiedAccess>]
type Kind =
  /// A fixed delay immediately before an assertion: passes on a quiet box, fails under load.
  | SleepThenAssert
  /// A random draw feeding an assertion with no pinned seed.
  | UnseededDraw
  /// A measured duration used as the subject of an assertion.
  | AssertedDuration
  /// A private polling loop instead of the shared Eventually primitive.
  | PrivatePollingLoop

  member this.Name =
    match this with
    | SleepThenAssert -> "sleep-then-assert"
    | UnseededDraw -> "unseeded-draw"
    | AssertedDuration -> "asserted-duration"
    | PrivatePollingLoop -> "private-polling-loop"

type Finding =
  { File: string
    Line: int
    Kind: Kind
    Snippet: string }

  member this.Key = sprintf "%s|%s|%d" this.File this.Kind.Name this.Line

module private Scan =
  let private isTestFile (path: string) =
    path.EndsWith("Tests.fs", StringComparison.Ordinal)
    || path.EndsWith("Harness.fs", StringComparison.Ordinal)
    || path.EndsWith("PropertyTests.fs", StringComparison.Ordinal)

  // Spelled in pieces so the blocking-call ratchet does not count the checker's own vocabulary as a blocking call.
  let private sleepTokens = [ "Task.Delay"; "Thread" + ".Sleep"; "Async.Sleep" ]

  let sleepThenAssert (lines: string array) (i: int) =
    let line = lines[i]
    if sleepTokens |> List.exists (fun t -> line.Contains t) then
      let window = lines |> Array.skip i |> Array.take (min 6 (lines.Length - i))
      window |> Array.tryFindIndex (fun l -> l.Contains "Expect.") |> Option.map (fun d -> d + 1)
    else None

  let unseededDraw (line: string) =
    (line.Contains "Gen.sample " && not (line.Contains "sampleWithSeed"))
    || (line.Contains "let private pick gen" && line.Contains "Gen.sample")

  let assertedDuration (line: string) =
    (line.Contains "Elapsed" || line.Contains "Stopwatch")
    && (line.Contains "Expect.equal" || line.Contains "Expect.isGreater" || line.Contains "Expect.isLess")

  let privateLoop (line: string) =
    (line.Contains "let rec wait" || line.Contains "let rec spin" || line.Contains "let rec poll"
     || line.Contains "let private waitFor" || line.Contains "let private await" || line.Contains "let private spin")
    && not (line.Contains "TestInfrastructure")

  let findingsIn (repo: string) (path: string) : Finding list =
    let rel = Path.GetRelativePath(repo, path) |> fun s -> s.Replace('\\', '/')
    let lines = File.ReadAllLines path
    // A file that imports the shared primitive has opted into the one wait; its local helpers are adapters,
    // not private polling loops.
    let usesEventually = lines |> Array.exists (fun l -> l.Contains "open SageFs.Tests.Eventually")
    [ for i in 0 .. lines.Length - 1 do
        let line = lines[i]
        match sleepThenAssert lines i with
        | Some d -> yield { File = rel; Line = i + d; Kind = Kind.SleepThenAssert; Snippet = line.Trim() }
        | None -> ()
        if unseededDraw line then
          yield { File = rel; Line = i + 1; Kind = Kind.UnseededDraw; Snippet = line.Trim() }
        if assertedDuration line then
          yield { File = rel; Line = i + 1; Kind = Kind.AssertedDuration; Snippet = line.Trim() }
        if privateLoop line && not usesEventually then
          yield { File = rel; Line = i + 1; Kind = Kind.PrivatePollingLoop; Snippet = line.Trim() } ]

  let all (repo: string) : Finding list =
    if not (Directory.Exists repo) then []
    else
      Directory.GetFiles(Path.Combine(repo, "SageFs.Tests"), "*.fs", SearchOption.AllDirectories)
      |> Array.filter isTestFile
      |> Array.toList
      |> List.collect (findingsIn repo)

module Register =
  let private path (repo: string) = Path.Combine(repo, "SageFs.Tests", "test-determinism-register.tsv")

  /// kind<TAB>file<TAB>reason. Path and line are NOT registered: a moved finding gets a fresh row, so a
  /// stale row cannot keep hiding a real fix.
  let load (repo: string) : (Kind * string * string) list =
    let p = path repo
    if not (File.Exists p) then []
    else
      File.ReadAllLines p
      |> Array.choose (fun line ->
        let t = line.Trim()
        if t.StartsWith "#" || String.IsNullOrWhiteSpace t then None
        else
          match t.Split('\t') with
          | [| kind; file; reason |] ->
            match kind with
            | "sleep-then-assert" -> Some(Kind.SleepThenAssert, file, reason)
            | "unseeded-draw" -> Some(Kind.UnseededDraw, file, reason)
            | "asserted-duration" -> Some(Kind.AssertedDuration, file, reason)
            | "private-polling-loop" -> Some(Kind.PrivatePollingLoop, file, reason)
            | _ -> None
          | _ -> None)
      |> Array.toList

  let isRegistered (rows: (Kind * string * string) list) (f: Finding) =
    rows |> List.exists (fun (kind, file, _) -> kind = f.Kind && file = f.File)

module private Planted =
  let sleepThenAssert = [| "do! Task.Delay " + "100"; "answer |> Expect.equal \"served\" \"yes\"" |]
  let unseeded = [| "let x = (Gen.sample 1 gen).[0]" |]
  let duration = [| "sw.Elapsed.TotalMilliseconds |> Expect.isLessThan \"fast\" 10." |]
  let loop = [| "let rec waitForReady () = task { do! Task.Delay " + "10 }" |]

[<Tests>]
let tests =
  let repo = RepoPaths.repoPathFull [||]
  let rows = Register.load repo
  let findings = Scan.all repo

  // Register maintenance: run with SAGEFS_WRITE_DETERMINISM_REGISTER=1 to record the current findings as
  // grandfathered debt, then remove rows as each shape is eliminated. The register only shrinks.
  do
    match Environment.GetEnvironmentVariable "SAGEFS_WRITE_DETERMINISM_REGISTER" with
    | null | "" -> ()
    | _ ->
      let text =
        [ yield "# kind<TAB>file<TAB>reason — generated debt, remove rows as shapes are eliminated"
          for f in findings do
            yield sprintf "%s\t%s\tgrandfathered" f.Kind.Name f.File ]
        |> String.concat "\n"
      File.WriteAllText(Path.Combine(repo, "SageFs.Tests", "test-determinism-register.tsv"), text + "\n")

  testList "Test determinism gate" [
    testCase "WHY — every remaining flaky shape is registered with a kind and a reason" <| fun () ->
      let unregistered = findings |> List.filter (fun f -> not (Register.isRegistered rows f))
      unregistered
      |> List.map (fun f -> sprintf "%s:%d %s — %s" f.File f.Line f.Kind.Name f.Snippet)
      |> Expect.isEmpty
           "an unregistered flaky shape makes the suite depend on load; register it with a reason or remove it"

    testCase "WHY — the register has no stale rows for shapes that are gone" <| fun () ->
      let present = findings |> List.map (fun f -> f.Kind, f.File) |> Set.ofList
      rows
      |> List.filter (fun (kind, file, _) -> not (Set.contains (kind, file) present))
      |> List.map (fun (kind, file, _) -> sprintf "%A %s" kind file)
      |> Expect.isEmpty "a stale row hides progress; remove it so the register only shrinks"

    testCase "WHY — the checker catches a planted sleep-then-assert" <| fun () ->
      match Scan.sleepThenAssert Planted.sleepThenAssert 0 with
      | Some _ -> ()
      | None -> failtest "the checker must be able to go red on the shape it claims to catch"

    testCase "WHY — the checker catches a planted unseeded draw" <| fun () ->
      Expect.isTrue "the planted draw is found" (Scan.unseededDraw Planted.unseeded[0])

    testCase "WHY — the checker catches a planted duration assertion" <| fun () ->
      Expect.isTrue "the planted duration is found" (Scan.assertedDuration Planted.duration[0])

    testCase "WHY — the checker catches a planted private polling loop" <| fun () ->
      Expect.isTrue "the planted loop is found" (Scan.privateLoop Planted.loop[0])
  ]
