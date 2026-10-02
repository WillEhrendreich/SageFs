// scripts/mirror-to-orvus.fsx -- a second copy of every commit, on another machine.
// Run with: dotnet fsi scripts/mirror-to-orvus.fsx -- [--status | --install | --uninstall]
//
// Commits that exist only on this machine are one dead disk from gone, and most of them are never pushed to
// GitHub (a master that has not shipped, the branches agents work on, work that failed a gate). This pushes
// every ref of each repo below, `git push --mirror`, to a bare repo on Orvus. Orvus never holds a GitHub
// credential, so this is plain ssh. It covers commits only: work that is not committed is not copied.
//
//   (no argument)  mirror every repo once and say what happened to each
//   --status       compare each repo's master here and on Orvus, change nothing
//   --install      copy this script to a stable place and start a systemd user timer that runs it every
//                  ten minutes (a copy, so editing the working tree mid-change cannot break the backup)
//   --uninstall    stop and remove the timer
//
// Repos are the ones that are Will's own. Employer repositories are deliberately not in the list.
open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions

// ── named values ─────────────────────────────────────────────────────────────

let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
let workDir = Path.Combine(home, "Work")
let host = match Environment.GetEnvironmentVariable "SAGEFS_MIRROR_HOST" with | null | "" -> "will@orvus" | h -> h
/// Relative to the home directory on the host.
let remoteRoot = "backups"
let repos = [ "SageFs"; "sagefs.nvim"; "nehemiah"; "molina"; "SageTech" ]
let sshConnectSeconds = 8
let pushTimeout = TimeSpan.FromMinutes 15.
let timerEverySeconds = 600
let timerName = "sagefs-mirror"
let installedScript = Path.Combine(home, ".local", "share", "sagefs-mirror", "mirror-to-orvus.fsx")
let unitDir = Path.Combine(home, ".config", "systemd", "user")
let dotnet = Path.Combine(home, ".dotnet", "dotnet")

/// What happened to one repo.
type Outcome =
  | Mirrored of detail: string
  | Skipped of why: string
  | Failed of why: string

// ── processes ────────────────────────────────────────────────────────────────

let run (file: string) (args: string list) (cwd: string) (timeout: TimeSpan) : int * string =
  let psi = ProcessStartInfo(file)
  psi.WorkingDirectory <- cwd
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  args |> List.iter psi.ArgumentList.Add
  use p = Process.Start psi
  let out = p.StandardOutput.ReadToEndAsync()
  let err = p.StandardError.ReadToEndAsync()
  if p.WaitForExit timeout then p.ExitCode, out.Result + err.Result
  else
    (try p.Kill true with _ -> ())
    124, sprintf "timed out after %.0f minutes\n%s%s" timeout.TotalMinutes out.Result err.Result

let shortTimeout = TimeSpan.FromSeconds 30.
let git (repo: string) (args: string list) timeout = run "git" ("-C" :: repo :: args) repo timeout
let ssh (command: string) =
  run "ssh" [ "-n"; "-o"; "BatchMode=yes"; "-o"; sprintf "ConnectTimeout=%d" sshConnectSeconds; host; command ] home shortTimeout

let remoteUrl (name: string) = sprintf "%s:%s/%s.git" host remoteRoot name

// ── mirroring ────────────────────────────────────────────────────────────────

let mirror (name: string) : Outcome =
  let path = Path.Combine(workDir, name)
  if not (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"))) then Skipped "not a git checkout"
  else
    // receive.shallowUpdate: SageFs is a shallow clone here, and a bare repo refuses a shallow push unless told not to.
    match ssh (sprintf "git init --bare --quiet %s/%s.git && git --git-dir=%s/%s.git config receive.shallowUpdate true" remoteRoot name remoteRoot name) with
    | code, o when code <> 0 -> Failed (sprintf "could not prepare the bare repo on %s: %s" host (o.Trim()))
    | _ ->
      // --no-verify: the repo's pre-push hook is about pushing master to GitHub, not about a backup.
      match git path [ "push"; "--mirror"; "--no-verify"; "--quiet"; remoteUrl name ] pushTimeout with
      | 0, o when String.IsNullOrWhiteSpace o -> Mirrored "refs pushed"
      | 0, o -> Mirrored (o.Trim().Split('\n').[0])
      | _, o -> Failed (o.Trim().Split('\n') |> Array.truncate 3 |> String.concat " | ")

/// The branch the checkout is on (master for most, main for some), so status compares like with like.
let trunkOf (name: string) =
  match git (Path.Combine(workDir, name)) [ "branch"; "--show-current" ] shortTimeout with
  | 0, o when o.Trim() <> "" -> o.Trim()
  | _ -> "master"

let trunkHere (name: string) =
  match git (Path.Combine(workDir, name)) [ "rev-parse"; trunkOf name ] shortTimeout with
  | 0, o -> Some (o.Trim())
  | _ -> None

let trunkThere (name: string) =
  match ssh (sprintf "git --git-dir=%s/%s.git rev-parse %s" remoteRoot name (trunkOf name)) with
  | 0, o -> Some (o.Trim())
  | _ -> None

let report (name: string) (outcome: Outcome) =
  match outcome with
  | Mirrored d -> printfn "  %-12s mirrored (%s)" name d
  | Skipped why -> printfn "  %-12s skipped: %s" name why
  | Failed why -> eprintfn "  %-12s FAILED: %s" name why

let status () =
  printfn "mirror to %s:%s" host remoteRoot
  let behind =
    repos
    |> List.map (fun name ->
      match trunkHere name, trunkThere name with
      | Some a, Some b when a = b -> printfn "  %-12s in step (%s %s)" name (trunkOf name) (a.Substring(0, 8)); false
      | Some a, Some b -> printfn "  %-12s BEHIND: here %s, there %s" name (a.Substring(0, 8)) (b.Substring(0, 8)); true
      | Some a, None -> printfn "  %-12s not on %s yet (here %s)" name host (a.Substring(0, 8)); true
      | None, _ -> printfn "  %-12s no %s here" name (trunkOf name); false)
  if List.contains true behind then 1 else 0

let mirrorAll () =
  printfn "%s mirroring %d repos to %s:%s" (DateTime.Now.ToString "HH:mm:ss") repos.Length host remoteRoot
  let results = repos |> List.map (fun name -> name, mirror name)
  results |> List.iter (fun (n, o) -> report n o)
  if results |> List.exists (fun (_, o) -> match o with Failed _ -> true | _ -> false) then 1 else 0

// ── the timer ────────────────────────────────────────────────────────────────

let systemctl args = run "systemctl" ("--user" :: args) home shortTimeout

let install () =
  Directory.CreateDirectory(Path.GetDirectoryName installedScript) |> ignore
  File.Copy(Path.Combine(__SOURCE_DIRECTORY__, Path.GetFileName __SOURCE_FILE__), installedScript, true)
  Directory.CreateDirectory unitDir |> ignore
  let service =
    String.Join("\n",
      [ "[Unit]"; "Description=Mirror this machine's git repos to Orvus"; ""
        "[Service]"; "Type=oneshot"
        sprintf "ExecStart=%s fsi %s" dotnet installedScript
        "Nice=10"; "" ])
  let timer =
    String.Join("\n",
      [ "[Unit]"; "Description=Mirror git repos to Orvus every ten minutes"; ""
        "[Timer]"; "OnBootSec=120"; sprintf "OnUnitActiveSec=%d" timerEverySeconds; "Persistent=true"; ""
        "[Install]"; "WantedBy=timers.target"; "" ])
  File.WriteAllText(Path.Combine(unitDir, timerName + ".service"), service)
  File.WriteAllText(Path.Combine(unitDir, timerName + ".timer"), timer)
  systemctl [ "daemon-reload" ] |> ignore
  match systemctl [ "enable"; "--now"; timerName + ".timer" ] with
  | 0, _ ->
    printfn "installed: %s.timer runs every %d minutes (script copied to %s)" timerName (timerEverySeconds / 60) installedScript
    0
  | _, o -> eprintfn "could not enable the timer: %s" o; 1

let uninstall () =
  systemctl [ "disable"; "--now"; timerName + ".timer" ] |> ignore
  [ ".service"; ".timer" ] |> List.iter (fun ext -> let f = Path.Combine(unitDir, timerName + ext) in if File.Exists f then File.Delete f)
  systemctl [ "daemon-reload" ] |> ignore
  printfn "removed %s.timer" timerName
  0

let argv = fsi.CommandLineArgs |> Array.toList |> List.tail |> List.filter (fun a -> a <> "--")

let code =
  match argv with
  | [] -> mirrorAll ()
  | [ "--status" ] -> status ()
  | [ "--install" ] -> install ()
  | [ "--uninstall" ] -> uninstall ()
  | _ -> eprintfn "usage: dotnet fsi scripts/mirror-to-orvus.fsx -- [--status | --install | --uninstall]"; 64

exit code
