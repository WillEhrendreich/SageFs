// scripts/pre-push.fsx   the git pre-push hook: refuse a master push that is not shippable.
//
// Not run by hand. git runs it as the pre-push hook through the shim next to it, `scripts/pre-push`, which
// `scripts/install-hooks.fsx` links into .git/hooks. (`dotnet fsi` only runs files that end in .fsx, and git
// runs the hook under the name `pre-push`, so the shim is a one-line shebang that names this file.)
//
// Refuses to push master to a commit that has not passed scripts/local-gate.fsx on this machine. The check is
// only a file lookup, so pushing a gated commit is instant. To gate and push in one step, run
// `dotnet fsi scripts/ship.fsx`.
//
// A master push must also RAISE the version. Publishing is keyed on it, and NuGet's --skip-duplicate means a
// repeated version ships nothing at all while the pipeline stays green. Nothing bumps on commit any more
// (scripts/ship.fsx owns it), so this is where the rule is enforced.
//
// Emergency override (logged, never silent): SAGEFS_SKIP_GATE=1 git push ...
// The self-hosted "main build" job still runs the full gate on an ungated commit before promoting anything,
// so the override skips only the local wait.
//
// The decisions are ReleaseRules.judgePush, tested in SageFs.Tests; this file is the IO around it: stdin
// (the lines git feeds a pre-push hook), `git show`, the gate's pass records, stderr and the override log.
//
// Exit codes: 0 every pushed ref is fine; 1 at least one is refused.
#load "ReleaseRules.fs"

open System
open System.Diagnostics
open System.IO

// ── named values ─────────────────────────────────────────────────────────────

let gitTimeout = TimeSpan.FromSeconds 30.
let gateHome =
  match Environment.GetEnvironmentVariable "SAGEFS_GATE_HOME" with
  | null | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".local", "share", "sagefs-gate")
  | path -> path
let overridesLog = Path.Combine(gateHome, "overrides.log")
let skipGate = Environment.GetEnvironmentVariable "SAGEFS_SKIP_GATE" = "1"

// ── facts about the world ────────────────────────────────────────────────────

/// `git show <sha>:Directory.Build.props`, or None when git cannot show it (an object this clone lacks).
let propsAt (sha: string) : string option =
  try
    let psi = ProcessStartInfo("git")
    [ "show"; sha + ":Directory.Build.props" ] |> List.iter psi.ArgumentList.Add
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEndAsync()
    let err = p.StandardError.ReadToEndAsync()
    match p.WaitForExit gitTimeout && p.ExitCode = 0 with
    | true -> Some out.Result
    | false -> None
  with _ -> None

let facts : ReleaseRules.PushFacts =
  { VersionAt = fun sha -> propsAt sha |> Option.bind ReleaseRules.versionIn
    HasPass = fun sha -> File.Exists(Path.Combine(gateHome, "passed", sha, "ok"))
    SkipGate = skipGate }

/// The override is logged, never silent: one line per ungated push, kept next to the gate's own state.
let recordOverride (sha: string) : unit =
  try
    Directory.CreateDirectory gateHome |> ignore
    File.AppendAllText(overridesLog, sprintf "%s skip-gate %s\n" (DateTimeOffset.Now.ToString "yyyy-MM-dd'T'HH:mm:sszzz") sha)
  with _ -> ()

// ── the hook ─────────────────────────────────────────────────────────────────

let pushedRefs : ReleaseRules.PushedRef list =
  Seq.initInfinite (fun _ -> Console.In.ReadLine())
  |> Seq.takeWhile (fun line -> not (isNull line))
  |> Seq.choose ReleaseRules.parsePushLine
  |> List.ofSeq

let verdicts = pushedRefs |> List.map (ReleaseRules.judgePush facts)

for verdict in verdicts do
  ReleaseRules.messagesFor verdict |> List.iter (eprintfn "%s")
  match verdict with
  | ReleaseRules.SkippedGate sha -> recordOverride sha
  | _ -> ()

exit (if List.exists ReleaseRules.refuses verdicts then 1 else 0)
