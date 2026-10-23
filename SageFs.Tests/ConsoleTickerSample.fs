/// The shipped console ticker sample, copied to a private directory.
///
/// Two suites need it on its own: one saves to a file of the app it started, the other evaluates
/// into a file of the app it started. The shared sample is used by other suites in the same run,
/// and an edit there restarts their running app under them, so each takes a copy.
///
/// The copy sits beside the real sample, so the fsproj's relative path to the repository
/// (Directory.Build.props, SageFs.Core) still resolves.
module SageFs.Tests.ConsoleTickerSample

open System
open System.IO

let private sharedDir =
  Path.Combine(RepoPaths.repoPathFull [||], "samples", "demos", "SageFs.Samples.ConsoleTicker")

/// The files that make the sample buildable. `packages.lock.json` matters: CI restores in locked
/// mode, and a project with no lock file would fail there.
let private sampleFiles =
  [ "SageFs.Samples.ConsoleTicker.fsproj"; "Program.fs"; "Ticker.fs"; "packages.lock.json" ]

/// A private copy of the sample, named so a failure can name it.
///
/// The copy is a SIBLING of the real sample, not a directory inside it: the sample's project
/// file reaches the repository's own build props and SageFs.Core by a relative path, and a copy
/// one level deeper resolves them to the wrong place and fails the build.
let copy () : string =
  let dir =
    Path.Combine(
      Path.GetDirectoryName(Path.GetFullPath(sharedDir)),
      sprintf "SageFs.Samples.ConsoleTicker.Copy-%s" (Guid.NewGuid().ToString("N").Substring(0, 8)))
  Directory.CreateDirectory dir |> ignore
  for name in sampleFiles do
    File.Copy(Path.Combine(sharedDir, name), Path.Combine(dir, name))
  dir

/// The message the ticker prints after its counter, as shipped.
let shippedMessage = "SageFs keeps ticking"
