/// The local gate's leftovers: a checkout per invoking repo path, tier clones, a record per passed commit, logs. The
/// gate's own reap (scripts/gate-reap.fsx) and the daemon's hygiene plan read them through one module, so these
/// tests are the tests of the script's logic.
module SageFs.Tests.GateReaperTests

open System
open System.Diagnostics
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.GateReaper
open SageFs.Tests.HygieneFixtures

let private gate = "/gate"
let private repoA = "/work/repo-a"
let private repoB = "/work/repo-b"

/// A gate dir in memory: directories with a last-write time and a size, and files with one line.
type private World =
  { Dirs: Map<string, DateTime * int64>
    Lines: Map<string, string>
    Alive: Set<int> }

let private fsOf (w: World) : GateFs =
  let under (parent: string) (path: string) =
    path.StartsWith(parent + "/", StringComparison.Ordinal) && not (path.Substring(parent.Length + 1).Contains "/")
  { ChildDirs = fun dir -> w.Dirs |> Map.toList |> List.map fst |> List.filter (under dir)
    ChildFiles = fun dir -> w.Lines |> Map.toList |> List.map fst |> List.filter (under dir)
    Exists = fun p -> Map.containsKey p w.Dirs || Map.containsKey p w.Lines
    LastWrite = fun p -> match Map.tryFind p w.Dirs with | Some(t, _) -> t | None -> now
    Size = fun p -> match Map.tryFind p w.Dirs with | Some(_, s) -> s | None -> 0L
    FirstLine = fun p -> Map.tryFind p w.Lines
    IsAlive = fun pid -> Set.contains pid w.Alive }

let private daysAgo (d: float) : DateTime = now - TimeSpan.FromDays d

let private checkoutOf (repo: string) = gate + "/" + Names.checkoutPrefix + repoKey repo

let private baseWorld : World =
  { Dirs = Map.ofList [ gate, (now, 0L); repoA, (now, 0L); gate + "/passed", (now, 0L) ]
    Lines = Map.empty
    Alive = Set.empty }

let private withCheckout (repo: string) (age: float) (world: World) : World =
  { world with
      Dirs = world.Dirs |> Map.add (checkoutOf repo) (daysAgo age, 1000L)
      Lines = world.Lines |> Map.add (gate + "/" + Names.ownersDir + "/" + Names.checkoutPrefix + repoKey repo) repo }

let private standingsOf (w: World) (invoking: string option) : (string * Standing) list =
  entries (fsOf w) gate
  |> List.map (fun e -> Path.GetFileName(entryDir e), standingOf now (describe (fsOf w) now gate [ repoA ] invoking e))

[<Tests>]
let tests =
  testList "The gate's leftovers" [

    testCase "the checkout key is the first 8 hex digits of the sha1 of the repo path, as the script computes it" <| fun _ ->
      // sha1("abc") is a9993e364706816aba3e25717850c26c9cd0d89d, a published test vector.
      repoKey "abc" |> Expect.equal "the published vector" "a9993e36"

    testCase "a checkout whose repo still exists and was used lately is kept, within its retention" <| fun _ ->
      let w = baseWorld |> withCheckout repoA 1.0
      match standingsOf w None |> List.exactlyOne |> snd with
      | Standing.WithinRetention _ -> ()
      | other -> failtestf "expected WithinRetention, got %A" other

    testCase "a checkout whose repo is gone is orphaned, however recent" <| fun _ ->
      let w = baseWorld |> withCheckout repoB 0.1
      match standingsOf w None |> List.exactlyOne |> snd with
      | Standing.Orphaned _ -> ()
      | other -> failtestf "expected Orphaned, got %A" other

    testCase "a checkout nobody used for the retention is expired even though its repo exists" <| fun _ ->
      let w = baseWorld |> withCheckout repoA (DataRetention.gateCheckoutRetention.TotalDays + 5.0)
      match standingsOf w None |> List.exactlyOne |> snd with
      | Standing.Expired _ -> ()
      | other -> failtestf "expected Expired, got %A" other

    testCase "the checkout of the repo the gate is running for is never a leftover, however old and whatever its owner" <| fun _ ->
      let w = baseWorld |> withCheckout repoB HygieneAges.ancient.TotalDays
      match standingsOf w (Some repoB) |> List.exactlyOne |> snd with
      | Standing.InUse(InUseReason.InvokingGate repo, _) -> repo |> Expect.equal "names the repo" repoB
      | other -> failtestf "expected InUse by the invoking gate, got %A" other

    testCase "while a gate is running nothing in the dir is touched" <| fun _ ->
      let w = { (baseWorld |> withCheckout repoB 0.1) with Lines = Map.ofList [ gate + "/" + Names.currentFile, "4242 abc"; gate + "/owners/" + Names.checkoutPrefix + repoKey repoB, repoB ]; Alive = Set.ofList [ 4242 ] }
      match standingsOf w None |> List.exactlyOne |> snd with
      | Standing.InUse(InUseReason.GateRunning 4242, _) -> ()
      | other -> failtestf "expected InUse by the running gate, got %A" other

    testCase "a dead gate's current file is not a use" <| fun _ ->
      let w = { (baseWorld |> withCheckout repoB 0.1) with Lines = Map.ofList [ gate + "/" + Names.currentFile, "4242 abc"; gate + "/owners/" + Names.checkoutPrefix + repoKey repoB, repoB ] }
      match standingsOf w None |> List.exactlyOne |> snd with
      | Standing.Orphaned _ -> ()
      | other -> failtestf "expected Orphaned, got %A" other

    testCase "only the newest few pass records are kept, and the older ones are superseded and reclaimable" <| fun _ ->
      let keep = DataRetention.gatePassRecordsKept
      let total = keep + 3
      let records = [ for i in 1 .. total -> gate + "/passed/sha" + string i, (daysAgo (float i), 270L) ]
      let w = { baseWorld with Dirs = records |> List.fold (fun m (k, v) -> Map.add k v m) baseWorld.Dirs }
      let standings = standingsOf w None
      standings |> List.length |> Expect.equal "every record is listed" total
      standings |> List.filter (fun (_, s) -> match s with | Standing.Newest _ -> true | _ -> false) |> List.length |> Expect.equal "the newest are kept" keep
      let superseded = standings |> List.filter (fun (_, s) -> match s with | Standing.Superseded _ -> true | _ -> false) |> List.map fst
      superseded |> List.length |> Expect.equal "the rest are superseded" 3
      superseded |> Expect.containsAll "the oldest ones go" [ "sha" + string total; "sha" + string (total - 1); "sha" + string (total - 2) ]

    testCase "the logs keep the newest few and the rest are stale" <| fun _ ->
      let logs = [ for i in 1 .. 7 -> sprintf "/gate/logs/%d.log" i ]
      let lastWrite (p: string) = daysAgo (float (Int32.Parse(Path.GetFileNameWithoutExtension p)))
      staleLogs lastWrite 3 logs |> Expect.equal "everything past the newest three" [ "/gate/logs/4.log"; "/gate/logs/5.log"; "/gate/logs/6.log"; "/gate/logs/7.log" ]
      staleLogs lastWrite 30 logs |> Expect.isEmpty "fewer than the limit: nothing is stale"
  ]

// ─── On a real directory ───────────────────────────────────────────────

let private write (path: string) (text: string) =
  Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
  File.WriteAllText(path, text)

let private age (path: string) (by: TimeSpan) =
  let t = DateTime.UtcNow - by
  match Directory.Exists path with
  | true -> Directory.SetLastWriteTimeUtc(path, t)
  | false -> File.SetLastWriteTimeUtc(path, t)

[<Tests>]
let realTests =
  testList "The gate's reap on a real directory" [

    testCase "it removes orphaned and old checkouts and surplus records and logs, and keeps the invoking repo's checkout" <| fun _ ->
      let root = Directory.CreateTempSubdirectory("sagefs-gate-reap-").FullName
      try
        let gateDir = Path.Combine(root, "gate")
        let invoking = Path.Combine(root, "repo-live")
        let gone = Path.Combine(root, "repo-gone")
        Directory.CreateDirectory invoking |> ignore
        let make (repo: string) =
          let dir = Path.Combine(gateDir, Names.checkoutPrefix + repoKey repo)
          write (Path.Combine(dir, "bin", "x.dll")) "x"
          write (Path.Combine(dir, ".git")) "gitdir: nowhere"
          write (Path.Combine(gateDir, Names.ownersDir, Names.checkoutPrefix + repoKey repo)) repo
          dir
        let live = make invoking
        let orphan = make gone
        age live HygieneAges.ancient
        let tier = Path.Combine(gateDir, Names.checkoutPrefix + repoKey gone + Names.tiersSuffix, "default")
        write (Path.Combine(tier, "bundle.bin")) "x"
        let keep = DataRetention.gatePassRecordsKept
        for i in 1 .. keep + 2 do
          let dir = Path.Combine(gateDir, Names.passedDir, sprintf "sha%02d" i)
          write (Path.Combine(dir, "ok")) "ok"
          age dir (TimeSpan.FromHours(float i))
        let logs = Path.Combine(gateDir, Names.logsDir)
        for i in 1 .. DataRetention.gateLogsKept + 3 do
          let log = Path.Combine(logs, sprintf "%02d.log" i)
          write log "log"
          age log (TimeSpan.FromHours(float i))
        let result = reap (realFs (fun _ -> false)) DateTime.UtcNow gateDir [ invoking ] (Some invoking) File.Delete
        Directory.Exists live |> Expect.isTrue "the invoking repo's checkout is never touched, however old"
        Directory.Exists orphan |> Expect.isFalse "a checkout whose repo is gone is removed"
        Directory.Exists tier |> Expect.isFalse "so are its tier clones"
        Directory.GetDirectories(Path.Combine(gateDir, Names.passedDir)).Length |> Expect.equal "only the newest records stay" keep
        Directory.Exists(Path.Combine(gateDir, Names.passedDir, "sha01")) |> Expect.isTrue "the newest record stays"
        Directory.Exists(Path.Combine(gateDir, Names.passedDir, sprintf "sha%02d" (keep + 2))) |> Expect.isFalse "the oldest record goes"
        Directory.GetFiles(logs).Length |> Expect.equal "only the newest logs stay" DataRetention.gateLogsKept
        result.LogsDeleted |> Expect.equal "the stale logs are counted" 3
        (result.Report.ReclaimedBytes > 0L) |> Expect.isTrue "it says what it gave back"
      finally Directory.Delete(root, true)

    testCase "running the reap twice is the same as once" <| fun _ ->
      let root = Directory.CreateTempSubdirectory("sagefs-gate-reap-").FullName
      try
        let gateDir = Path.Combine(root, "gate")
        let gone = Path.Combine(root, "repo-gone")
        let dir = Path.Combine(gateDir, Names.checkoutPrefix + repoKey gone)
        write (Path.Combine(dir, "x")) "x"
        write (Path.Combine(gateDir, Names.ownersDir, Names.checkoutPrefix + repoKey gone)) gone
        let fs = realFs (fun _ -> false)
        reap fs DateTime.UtcNow gateDir [] None File.Delete |> ignore
        let second = reap fs DateTime.UtcNow gateDir [] None File.Delete
        second.Report.ReclaimedBytes |> Expect.equal "nothing the second time" 0L
        Directory.Exists dir |> Expect.isFalse "and it stayed gone"
      finally Directory.Delete(root, true)

    testCase "a checkout entry that is a link out of the gate dir is refused and what it points at survives" <| fun _ ->
      let root = Directory.CreateTempSubdirectory("sagefs-gate-reap-").FullName
      try
        let gateDir = Path.Combine(root, "gate")
        let outside = Path.Combine(root, "precious")
        write (Path.Combine(outside, "keep.txt")) "keep"
        Directory.CreateDirectory gateDir |> ignore
        let gone = Path.Combine(root, "repo-gone")
        let link = Path.Combine(gateDir, Names.checkoutPrefix + repoKey gone)
        Directory.CreateSymbolicLink(link, outside) |> ignore
        write (Path.Combine(gateDir, Names.ownersDir, Names.checkoutPrefix + repoKey gone)) gone
        reap (realFs (fun _ -> false)) DateTime.UtcNow gateDir [] None File.Delete |> ignore
        File.Exists(Path.Combine(outside, "keep.txt")) |> Expect.isTrue "the target of the link is untouched"
      finally Directory.Delete(root, true)

    testCase "the script itself loads and runs against a real gate dir and says what it did" <| fun _ ->
      let root = Directory.CreateTempSubdirectory("sagefs-gate-reap-").FullName
      try
        let gateDir = Path.Combine(root, "gate")
        let repo = Path.Combine(root, "repo")
        let gone = Path.Combine(root, "repo-gone")
        Directory.CreateDirectory repo |> ignore
        let dir = Path.Combine(gateDir, Names.checkoutPrefix + repoKey gone)
        write (Path.Combine(dir, "x")) "x"
        write (Path.Combine(gateDir, Names.ownersDir, Names.checkoutPrefix + repoKey gone)) gone
        let script = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "scripts", "gate-reap.fsx"))
        let psi = ProcessStartInfo("dotnet", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
        for a in [ "fsi"; script; gateDir; repo ] do psi.ArgumentList.Add a
        use p = Process.Start psi
        let out = p.StandardOutput.ReadToEndAsync()
        let err = p.StandardError.ReadToEndAsync()
        p.WaitForExit()
        p.ExitCode |> Expect.equal (sprintf "exits cleanly. stdout: %s stderr: %s" out.Result err.Result) 0
        out.Result |> Expect.stringContains "says what it removed" "reap: removed 1 entries"
        Directory.Exists dir |> Expect.isFalse "the orphaned checkout is gone"
      finally Directory.Delete(root, true)
  ]
