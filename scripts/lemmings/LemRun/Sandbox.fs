/// The bubblewrap sandbox every lemming-facing process runs in, as pure functions from a description
/// to an argument list. Nothing here starts a process, so every wall can be read and tested as data.
///
/// The sandbox: the toolchain read-only, a tmpfs HOME, its own pid, ipc and uts namespaces (so it
/// cannot see or signal a host process, the shared daemon included), a cleared environment, and
/// only the working directory, a scratch directory and the run's own NuGet folder writable. The
/// network is NOT isolated unless asked: the model API and localhost are reachable, because the
/// lemming's MCP bridge talks to the daemon over localhost. What the daemon does for a lemming is
/// outside this sandbox (README.md, Isolation).
module LemRun.Sandbox

open System.IO

/// Whether the sandbox can reach the network. An oracle that runs the lemming's code is isolated.
type Network =
  | NetworkOpen
  | NetworkIsolated

/// What the core sandbox is built from.
type Core =
  { Home: string
    RunDir: string
    Workdir: string
    /// A directory put on PATH ahead of the system's (cmdc's, say).
    ExtraPath: string option
    /// The user's NuGet cache exists: it is mounted read-only as a fallback folder.
    NugetCache: bool
    /// The global tools (the published `sagefs`, which can stop the shared daemon) are hidden.
    MaskDotnetTools: bool
    /// Stored builds the sandbox may read, mounted read-only at their own paths.
    ReadOnlyMounts: string list }

/// The directory of the SDK the lemming builds with, and the scratch area it writes to.
let dotnetHome (c: Core) = Path.Combine(c.RunDir, "dotnethome")
let sbxDir (c: Core) = Path.Combine(c.RunDir, "out", "sbx")
let binDir (c: Core) = Path.Combine(c.RunDir, "bin")

let private mount (flag: string) (src: string) (dst: string) = [ flag; src; dst ]
let private roBind (p: string) = mount "--ro-bind" p p
let private bind (p: string) = mount "--bind" p p
let private setenv (name: string) (value: string) = [ "--setenv"; name; value ]

/// The core sandbox. The same arguments the bash harness built, in the same order.
let baseArgs (c: Core) : string list =
  let nuget = Path.Combine(dotnetHome c, "nuget")
  [ [ "--die-with-parent"; "--unshare-pid"; "--unshare-ipc"; "--unshare-uts"; "--clearenv" ]
    roBind "/usr" @ roBind "/etc"
    [ "--symlink"; "usr/bin"; "/bin"; "--symlink"; "usr/sbin"; "/sbin"; "--symlink"; "usr/lib"; "/lib"; "--symlink"; "usr/lib64"; "/lib64" ]
    roBind "/run/systemd/resolve"
    [ "--proc"; "/proc"; "--dev"; "/dev"; "--tmpfs"; "/tmp"; "--tmpfs"; c.Home ]
    roBind (Path.Combine(c.Home, ".dotnet"))
    roBind (Path.Combine(c.Home, ".local", "share", "mise"))
    bind c.Workdir
    roBind (binDir c)
    (c.ReadOnlyMounts |> List.collect roBind)
    bind (dotnetHome c)
    bind (sbxDir c)
    [ "--chdir"; c.Workdir ]
    setenv "HOME" c.Home @ setenv "TERM" "dumb" @ setenv "LANG" "C.UTF-8"
    setenv "PATH" (sprintf "%s/.dotnet:%s/.dotnet/tools:%s/usr/bin:/bin" c.Home c.Home (match c.ExtraPath with Some p -> p + ":" | None -> ""))
    setenv "DOTNET_ROOT" (Path.Combine(c.Home, ".dotnet")) @ setenv "DOTNET_CLI_HOME" (dotnetHome c)
    setenv "DOTNET_CLI_TELEMETRY_OPTOUT" "1" @ setenv "DOTNET_NOLOGO" "1" @ setenv "DOTNET_SKIP_FIRST_TIME_EXPERIENCE" "1"
    setenv "NUGET_PACKAGES" (Path.Combine(nuget, "packages"))
    setenv "NUGET_HTTP_CACHE_PATH" (Path.Combine(nuget, "http"))
    setenv "NUGET_PLUGINS_CACHE_PATH" (Path.Combine(nuget, "plugins"))
    // The packages the lemming restores go to a cache of this run's own, and the user's package cache
    // is only a read-only fallback, so a restore cannot change what the next build on this machine sees.
    (match c.NugetCache with
     | true ->
       let cache = Path.Combine(c.Home, ".nuget", "packages")
       roBind cache @ setenv "NUGET_FALLBACK_PACKAGES" cache
     | false -> [])
    (match c.MaskDotnetTools with
     | true -> [ "--tmpfs"; Path.Combine(c.Home, ".dotnet", "tools") ]
     | false -> []) ]
  |> List.concat

/// What cmdc is given on top of the core sandbox: its own private ~/.commandcode with only the
/// credential bound over it, read-only, plus the caller's extra environment and mounts.
let lemmingArgs (c: Core) (extraEnv: (string * string) list) (extraBwrap: string list) : string list =
  let commandcode = Path.Combine(c.Home, ".commandcode")
  baseArgs c
  @ mount "--bind" (Path.Combine(c.RunDir, "cmdchome")) commandcode
  @ mount "--ro-bind" (Path.Combine(commandcode, "auth.json")) (Path.Combine(commandcode, "auth.json"))
  @ setenv "COMMANDCODE_SKIP_UPDATES" "1"
  @ (extraEnv |> List.collect (fun (k, v) -> setenv k v))
  @ extraBwrap

/// The sandbox for an oracle that runs code the lemming wrote: the core sandbox, no network unless
/// the run needs a restore, and no credentials (nothing here binds them).
let execArgs (c: Core) (network: Network) (extraBwrap: string list) : string list =
  baseArgs c
  @ (match network with
     | NetworkIsolated -> [ "--unshare-net" ]
     | NetworkOpen -> [])
  @ extraBwrap

/// The command that ends a process after a limit, the way the bash harness did, so its exit
/// codes (124 on the limit, 137 when it had to KILL) mean what they always meant.
let withTimeout (killAfterSeconds: int) (seconds: int) (command: string list) : string list =
  [ "timeout"; sprintf "--kill-after=%d" killAfterSeconds; string seconds ] @ command

/// The command line cmdc is run with, headless, on a free model.
let cmdcCommand (prompt: string) (model: string) (maxTurns: int) : string list =
  [ "cmdc"; "-p"; prompt; "--model"; model; "--max-turns"; string maxTurns
    "--output-format"; "json"; "--yolo"; "--no-session"; "--skip-onboarding"; "--no-auto-update" ]

/// Runs inside the sandbox, in place of the shell the bash harness used: runs the command, and when
/// it has exited lists what is still alive in the sandbox (the residue of processes) into
/// `<out/sbx>/ps.txt`. Everything in the sandbox dies with it, so this is a record, not a cleanup.
/// The exit code is the command's.
let supervise (psDir: string) (command: string list) : int =
  match command with
  | [] -> 2
  | file :: args ->
    let code = Proc.runInheriting (Proc.spec file args)
    let ps = Proc.run (Proc.spec "ps" [ "-eo"; "pid,ppid,etimes,args"; "--no-headers" ]) None
    File.WriteAllText(Path.Combine(psDir, "ps.txt"), ps.Stdout)
    code

/// Creates the directories the sandbox mounts: bubblewrap refuses a mount whose source is missing.
let ensureDirs (c: Core) : unit =
  for d in [ dotnetHome c; Path.Combine(dotnetHome c, "nuget"); sbxDir c; binDir c ] do
    Directory.CreateDirectory d |> ignore
