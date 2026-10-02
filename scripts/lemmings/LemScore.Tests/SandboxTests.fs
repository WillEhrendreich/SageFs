module LemScore.Tests.SandboxTests

open System
open System.IO
open Expecto
open Expecto.Flip
open LemRun
open LemRun.Sandbox

let private core : Core =
  { Home = "/home/u"; RunDir = "/tmp/lem/r-01"; Workdir = "/tmp/lem/r-01/w"; ExtraPath = Some "/home/u/.local/share/mise/installs/node/1/bin"
    NugetCache = true; MaskDotnetTools = false; ReadOnlyMounts = [ "/store/bridge/b1" ] }

/// The arguments that follow a flag, in order.
let private after (flag: string) (args: string list) : string list list =
  args
  |> List.indexed
  |> List.filter (fun (_, a) -> a = flag)
  |> List.map (fun (i, _) -> args |> List.skip (i + 1) |> List.truncate (match flag with "--setenv" | "--ro-bind" | "--bind" | "--symlink" -> 2 | _ -> 1))

let private has (flag: string) (values: string list) (args: string list) : bool = after flag args |> List.contains values

let private bwrapUsable =
  let probe = Proc.run (Proc.spec "bwrap" [ "--ro-bind"; "/usr"; "/usr"; "--symlink"; "usr/bin"; "/bin"; "--symlink"; "usr/lib"; "/lib"; "--symlink"; "usr/lib64"; "/lib64"; "true" ]) (Some (TimeSpan.FromSeconds 10.0))
  probe.ExitCode = 0

[<Tests>]
let walls =
  testList "the core sandbox (as data)" [
    testCase "its own pid, ipc and uts namespaces, a cleared environment, and it dies with its parent" <| fun _ ->
      let args = baseArgs core
      for flag in [ "--die-with-parent"; "--unshare-pid"; "--unshare-ipc"; "--unshare-uts"; "--clearenv" ] do
        args |> List.contains flag |> Expect.isTrue (sprintf "has %s" flag)

    testCase "the home directory is an empty tmpfs, and no desktop variable is passed in" <| fun _ ->
      let args = baseArgs core
      after "--tmpfs" args |> Expect.contains "home is tmpfs" [ "/home/u" ]
      for name in [ "DISPLAY"; "WAYLAND_DISPLAY"; "XDG_RUNTIME_DIR"; "DBUS_SESSION_BUS_ADDRESS" ] do
        after "--setenv" args |> List.exists (fun v -> v.Head = name) |> Expect.isFalse (sprintf "no %s" name)

    testCase "the toolchain is read-only, the working directory and scratch are writable" <| fun _ ->
      let args = baseArgs core
      has "--ro-bind" [ "/home/u/.dotnet"; "/home/u/.dotnet" ] args |> Expect.isTrue "dotnet read-only"
      has "--ro-bind" [ "/home/u/.local/share/mise"; "/home/u/.local/share/mise" ] args |> Expect.isTrue "mise read-only"
      has "--bind" [ "/tmp/lem/r-01/w"; "/tmp/lem/r-01/w" ] args |> Expect.isTrue "workdir writable"
      has "--bind" [ "/tmp/lem/r-01/dotnethome"; "/tmp/lem/r-01/dotnethome" ] args |> Expect.isTrue "dotnet home writable"
      has "--bind" [ "/tmp/lem/r-01/out/sbx"; "/tmp/lem/r-01/out/sbx" ] args |> Expect.isTrue "scratch writable"
      has "--ro-bind" [ "/tmp/lem/r-01/bin"; "/tmp/lem/r-01/bin" ] args |> Expect.isTrue "the run's bin read-only"

    testCase "a stored build is mounted read-only at its own path, once, and is never writable" <| fun _ ->
      let args = baseArgs core
      has "--ro-bind" [ "/store/bridge/b1"; "/store/bridge/b1" ] args |> Expect.isTrue "mounted read-only"
      has "--bind" [ "/store/bridge/b1"; "/store/bridge/b1" ] args |> Expect.isFalse "not writable"

    testCase "the user's NuGet cache is only a read-only fallback, and the run writes packages to its own folder" <| fun _ ->
      let args = baseArgs core
      has "--ro-bind" [ "/home/u/.nuget/packages"; "/home/u/.nuget/packages" ] args |> Expect.isTrue "cache read-only"
      has "--setenv" [ "NUGET_FALLBACK_PACKAGES"; "/home/u/.nuget/packages" ] args |> Expect.isTrue "fallback named"
      has "--setenv" [ "NUGET_PACKAGES"; "/tmp/lem/r-01/dotnethome/nuget/packages" ] args |> Expect.isTrue "own packages folder"
      baseArgs { core with NugetCache = false } |> after "--setenv" |> List.exists (fun v -> v.Head = "NUGET_FALLBACK_PACKAGES") |> Expect.isFalse "no cache, no fallback"

    testCase "PATH puts dotnet, then the extra directory, then the system's" <| fun _ ->
      baseArgs core
      |> after "--setenv"
      |> List.find (fun v -> v.Head = "PATH")
      |> List.item 1
      |> Expect.equal "path" "/home/u/.dotnet:/home/u/.dotnet/tools:/home/u/.local/share/mise/installs/node/1/bin:/usr/bin:/bin"
      baseArgs { core with ExtraPath = None }
      |> after "--setenv"
      |> List.find (fun v -> v.Head = "PATH")
      |> List.item 1
      |> Expect.equal "path without an extra directory" "/home/u/.dotnet:/home/u/.dotnet/tools:/usr/bin:/bin"

    testCase "the global tools (the published sagefs can stop the daemon) are hidden on request" <| fun _ ->
      baseArgs { core with MaskDotnetTools = true } |> after "--tmpfs" |> Expect.contains "masked" [ "/home/u/.dotnet/tools" ]
      baseArgs core |> after "--tmpfs" |> List.contains [ "/home/u/.dotnet/tools" ] |> Expect.isFalse "visible by default"

    testCase "the lemming gets the credential, read-only, over a private ~/.commandcode; an oracle gets none" <| fun _ ->
      let lemming = lemmingArgs core [ "PATH", "/x" ] [ "--bind"; "/a"; "/a" ]
      has "--bind" [ "/tmp/lem/r-01/cmdchome"; "/home/u/.commandcode" ] lemming |> Expect.isTrue "private home"
      has "--ro-bind" [ "/home/u/.commandcode/auth.json"; "/home/u/.commandcode/auth.json" ] lemming |> Expect.isTrue "credential read-only"
      lemming |> List.rev |> List.take 3 |> Expect.equal "caller's mounts come last, so they win" [ "/a"; "/a"; "--bind" ] |> ignore
      let oracle = execArgs core NetworkIsolated []
      oracle |> List.exists (fun a -> a.Contains "auth.json") |> Expect.isFalse "no credential"
      oracle |> Expect.contains "no network" "--unshare-net"
      execArgs core NetworkOpen [] |> List.contains "--unshare-net" |> Expect.isFalse "a restore keeps the network"

    testCase "the caller's environment is set after the core's, so its PATH wins" <| fun _ ->
      let args = lemmingArgs core [ "PATH", "/mine" ] []
      let paths = after "--setenv" args |> List.filter (fun v -> v.Head = "PATH")
      paths |> List.length |> Expect.equal "two PATH settings" 2
      (List.last paths).[1] |> Expect.equal "the later one is the caller's" "/mine"

    testCase "the time limit is timeout(1), so its exit codes keep their meaning" <| fun _ ->
      withTimeout 30 1500 [ "cmdc"; "-p"; "x" ] |> Expect.equal "command" [ "timeout"; "--kill-after=30"; "1500"; "cmdc"; "-p"; "x" ]

    testCase "cmdc runs headless, yolo, with no session and no update, on the named model with a turn cap" <| fun _ ->
      let c = cmdcCommand "do it" "stealth/space-bunny-alpha" 7
      c |> List.take 3 |> Expect.equal "start" [ "cmdc"; "-p"; "do it" ]
      for flag in [ "--yolo"; "--no-session"; "--skip-onboarding"; "--no-auto-update" ] do c |> Expect.contains flag flag
      c |> List.pairwise |> Expect.contains "model" ("--model", "stealth/space-bunny-alpha")
      c |> List.pairwise |> Expect.contains "turns" ("--max-turns", "7")
  ]

/// A scratch run directory with everything the sandbox mounts.
let private withRun (body: Core -> unit) : unit =
  let run = Path.Combine(Path.GetTempPath(), "lem-sbx-" + Guid.NewGuid().ToString("N").Substring(0, 8))
  let work = Path.Combine(run, "w")
  Directory.CreateDirectory work |> ignore
  let stored = Path.Combine(run, "stored")
  Directory.CreateDirectory stored |> ignore
  File.WriteAllText(Path.Combine(stored, "marker"), "kept")
  try
    let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
    let c : Core =
      { Home = home; RunDir = run; Workdir = work; ExtraPath = None; NugetCache = false; MaskDotnetTools = false; ReadOnlyMounts = [ stored ] }
    ensureDirs c
    body c
  finally
    try Directory.Delete(run, true) with _ -> ()

let private sandboxed (c: Core) (network: Network) (command: string list) : Proc.Captured =
  Proc.run (Proc.spec "bwrap" (execArgs c network [] @ withTimeout 5 60 command)) (Some (TimeSpan.FromSeconds 90.0))

[<Tests>]
let realSandbox =
  let guarded (name: string) (body: Core -> unit) =
    testCase name <| fun _ ->
      if not bwrapUsable then skiptest "bubblewrap cannot start here"
      withRun body
  testList "the core sandbox (a real bubblewrap)" [
    guarded "the working directory is writable, a stored build is not, and the system is not" <| fun c ->
      (sandboxed c NetworkIsolated [ "sh"; "-c"; sprintf "echo x > %s/new && echo wrote" c.Workdir ]).Stdout |> Expect.stringContains "workdir" "wrote"
      File.Exists(Path.Combine(c.Workdir, "new")) |> Expect.isTrue "and it reached the host"
      let stored = List.head c.ReadOnlyMounts
      (sandboxed c NetworkIsolated [ "sh"; "-c"; sprintf "echo x > %s/evil && echo wrote || echo denied" stored ]).Stdout |> Expect.stringContains "stored" "denied"
      File.Exists(Path.Combine(stored, "evil")) |> Expect.isFalse "the stored build is untouched"
      (sandboxed c NetworkIsolated [ "sh"; "-c"; "echo x > /etc/evil && echo wrote || echo denied" ]).Stdout |> Expect.stringContains "etc" "denied"

    guarded "the host's home is not there: only the mounted toolchain, and the credential directory is absent" <| fun c ->
      let listing = (sandboxed c NetworkIsolated [ "sh"; "-c"; sprintf "ls -A %s" c.Home ]).Stdout
      listing |> Expect.stringContains "dotnet is mounted" ".dotnet"
      listing |> Expect.stringContains "the toolchain is mounted" ".local"
      listing.Contains ".ssh" |> Expect.isFalse "no ssh directory"
      listing.Contains ".commandcode" |> Expect.isFalse "an oracle has no credential"

    guarded "it cannot see a host process, and an oracle has no network" <| fun c ->
      let count = (sandboxed c NetworkIsolated [ "sh"; "-c"; "ls /proc | grep -c '^[0-9]'" ]).Stdout.Trim() |> Int32.Parse
      (count < 12) |> Expect.isTrue (sprintf "its own pid namespace (saw %d processes)" count)
      let interfaces = (sandboxed c NetworkIsolated [ "sh"; "-c"; "tail -n +3 /proc/net/dev | cut -d: -f1 | tr -d ' '" ]).Stdout.Trim()
      interfaces |> Expect.equal "only loopback" "lo"

    guarded "the supervisor runs the command, returns its exit code and lists what is alive afterwards" <| fun c ->
      let psDir = Sandbox.sbxDir c
      let code = Sandbox.supervise psDir [ "sh"; "-c"; "exit 7" ]
      code |> Expect.equal "the command's exit code" 7
      File.ReadAllText(Path.Combine(psDir, "ps.txt")) |> Expect.stringContains "ps lists itself" "ps -eo"
      Sandbox.supervise psDir [] |> Expect.equal "nothing to run is refused" 2
  ]
