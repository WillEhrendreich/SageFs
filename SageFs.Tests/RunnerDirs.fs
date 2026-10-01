/// The temp dirs the integration runners make (a daemon's data dir, a copied fixture) and take away again. A runner
/// that is killed never reaches its cleanup, and /tmp/sagefs-hr alone reached 5 GB, so each dir records its owner in
/// a marker, the runner removes its own on the way out, and the next runner's start removes the ones whose owner is
/// gone and that nobody has written to for a while. Only dirs with a marker are ever swept: one without it is not
/// provably ours.
module SageFs.Tests.RunnerDirs

open System
open System.IO
open SageFs

/// The families of runner temp dirs. A closed set with one name each, so a new runner picks a family and cannot invent a
/// directory the sweep does not know.
[<RequireQualifiedAccess>]
type Family =
  | BrowserRuns
  | HotReloadRuns
  | LiveTestingRuns
  /// The throwaway databases the unit tests open (a friction store each), kept in one dir per test process.
  | TestDatabases
  /// The scratch dirs unit tests make for themselves, kept in one dir per test process.
  | TestScratch

let familyName (family: Family) : string =
  match family with
  | Family.BrowserRuns -> "sagefs-browser"
  | Family.HotReloadRuns -> "sagefs-hr"
  | Family.LiveTestingRuns -> "sagefs-lt"
  | Family.TestDatabases -> "sagefs-test-db"
  | Family.TestScratch -> "sagefs-test-scratch"

/// The marker file naming the process a run dir belongs to. The same name the other temp roots use.
[<Literal>]
let OwnerMarker = "owner.pid"

let private parentOf (temp: string) (family: Family) : string = Path.Combine(temp, familyName family)

/// Remove the dirs of a family whose owner is gone and that nobody has written to for `DataRetention.tempRunMaxAge`.
/// A dir with no marker, a live owner, or an owner whose liveness cannot be told is left alone.
let sweepStale (temp: string) (family: Family) (now: DateTime) : (string * int64) list =
  OrphanTempDirSweep.sweepOlderThan
    (parentOf temp family)
    "*"
    OwnerMarker
    ShadowCopy.processLiveness
    DataRetention.tempRunMaxAge
    now

/// Make a fresh run dir for this process under the temp dir, after sweeping the family's stale ones.
let createIn (temp: string) (family: Family) : string =
  sweepStale temp family DateTime.UtcNow |> ignore
  let dir = Path.Combine(parentOf temp family, Guid.NewGuid().ToString "N")
  Directory.CreateDirectory dir |> ignore
  OrphanTempDirSweep.writeOwnerPid OwnerMarker dir Environment.ProcessId
  dir

/// A run dir under the real OS temp dir.
let create (family: Family) : string = createIn (Path.GetTempPath()) family

/// Take a run dir away on the way out. Best effort: a runner that cannot delete its own dir leaves it for the sweep.
let remove (dir: string) : unit =
  try Directory.Delete(dir, true) with _ -> ()

/// One dir per test process for the scratch dirs unit tests make for themselves, removed when the process exits. A killed
/// process leaves it for the next run's sweep. (Hundreds of loose `sagefs-*` dirs per run used to stay in /tmp.)
let private scratchRoot : Lazy<string> =
  lazy
    (let dir = create Family.TestScratch
     AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> remove dir)
     dir)

/// A fresh scratch dir for one test, under this process's scratch root. Not created: the test creates what it needs, and
/// some tests are about a directory that does not exist yet.
let scratchPath (prefix: string) : string = Path.Combine(scratchRoot.Value, prefix + Guid.NewGuid().ToString "N")

/// `scratchPath`, created.
let scratchDir (prefix: string) : string =
  let dir = scratchPath prefix
  Directory.CreateDirectory dir |> ignore
  dir
