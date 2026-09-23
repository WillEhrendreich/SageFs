module SageFs.Tests.DaemonPresenceTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

// `sagefs stop`/`status` used to collapse two very different situations into
// one `None`: nobody home, and a daemon process that is alive and holding
// the port but never answers HTTP (wedged). A wedged daemon then read as
// "no daemon running" and `sagefs stop` did nothing while the old process
// kept the port — the exact incident this closes. `DaemonPresence.classify`
// is the pure three-way decision every caller (stop, status) now goes
// through; this file proves the decision itself, with no real daemon, no
// real HTTP call, and no real process involved.

let private mkDaemonInfo pid =
  { Pid = pid
    Port = 37749
    DashboardPort = 37750
    StartedAt = DateTime.UtcNow
    WorkingDirectory = Path.Combine(Path.GetTempPath(), "sagefs-presence-tests")
    Version = "test"
    ApiVersion = None
    SessionCount = None }

[<Tests>]
let daemonPresenceTests =
  testList "DaemonPresence.classify" [

    test "an HTTP probe that answers is Running, regardless of a local pid" {
      let info = mkDaemonInfo 111
      DaemonPresence.classify (Some info) (Some 222)
      |> Expect.equal "HTTP wins" (DaemonPresence.Running info)
    }

    test "no HTTP answer and no local pid is NotRunning" {
      DaemonPresence.classify None None
      |> Expect.equal "nobody home" DaemonPresence.NotRunning
    }

    test "WHY — DaemonPresence.classify — no HTTP answer but a live local pid is Wedged, not NotRunning, because a wedged daemon must not read the same as no daemon" {
      DaemonPresence.classify None (Some 333)
      |> Expect.equal "wedged, holding the port without answering" (DaemonPresence.Wedged 333)
    }

    test "describe is exhaustive and names the pid for a wedged daemon" {
      DaemonPresence.describe (DaemonPresence.Wedged 333)
      |> Expect.stringContains "the pid is in the description, since it's the recovery handle" "333"
    }

    test "describe names the port for a running daemon" {
      DaemonPresence.describe (DaemonPresence.Running (mkDaemonInfo 111))
      |> Expect.stringContains "port is in the description" "37749"
    }

    test "describe says nothing is running" {
      DaemonPresence.describe DaemonPresence.NotRunning
      |> Expect.stringContains "plainly says nothing is running" "no daemon running"
    }
  ]
