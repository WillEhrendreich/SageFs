/// Proves the bwrap argument builder (demo-gif-plan.md §4.1, §4.12) without
/// ever spawning bwrap: `Sandbox.args` is pure data, so the exact recipe the
/// Phase-0 spike proved (private namespaces, `--uid 0 --gid 0`, tmpfs, the
/// RO/RW binds, `--die-with-parent`) is checked here as a plain string-list
/// assertion, not a live-sandbox integration test.
module SageFs.Demos.Tests.SandboxTests

open Expecto
open Expecto.Flip
open SageFs.Demos.Sandbox

let private sampleSpec: CellSpec =
  { RoBinds = [ "/chrome-host", "/chrome-bin"; "/dotnet-host", "/dotnet-root" ]
    RwBinds = [ "/host/out", "/out" ]
    Env = [ "LIBGL_ALWAYS_SOFTWARE", "1" ]
    InnerCommand = [ "/bin/sh"; "-c"; "echo hi" ] }

/// The `--flag`, `arg1`, `arg2`, ... triples/pairs that follow one flag
/// literal, in order — lets a test assert "this exact flag got this exact
/// argument" without depending on where else that flag appears.
let private argsAfter (flag: string) (argCount: int) (all: string list) : string list list =
  all
  |> List.indexed
  |> List.filter (fun (_, v) -> v = flag)
  |> List.map (fun (i, _) -> all |> List.skip (i + 1) |> List.truncate argCount)

[<Tests>]
let tests =
  testList "Sandbox" [

    testCase "args carries every §4.12-proven namespace flag, plus --uid/--gid 0 and --die-with-parent" <| fun _ ->
      let a = args sampleSpec
      [ "--unshare-user"; "--unshare-net"; "--unshare-pid"; "--unshare-ipc"; "--die-with-parent"; "--clearenv" ]
      |> List.iter (fun flag -> a |> List.contains flag |> Expect.isTrue (sprintf "%s present" flag))
      argsAfter "--uid" 1 a |> Expect.equal "--uid 0 (Xvfb's /tmp/.X11-unix ownership check, §4.12)" [ [ "0" ] ]
      argsAfter "--gid" 1 a |> Expect.equal "--gid 0" [ [ "0" ] ]

    testCase "args tmpfs-mounts /tmp and /home/demo (§4.1: teardown is process exit, zero garbage)" <| fun _ ->
      let a = args sampleSpec
      argsAfter "--tmpfs" 1 a |> Expect.equal "both tmpfs mounts present, in order" [ [ "/tmp" ]; [ "/home/demo" ] ]

    testCase "args RO-binds the fixed /usr plus every CellSpec.RoBinds pair, and no more" <| fun _ ->
      let a = args sampleSpec
      let roBinds = argsAfter "--ro-bind" 2 a
      roBinds |> Expect.contains "the fixed /usr bind" [ "/usr"; "/usr" ]

      sampleSpec.RoBinds
      |> List.iter (fun (host, cell) -> roBinds |> Expect.contains (sprintf "%s -> %s" host cell) [ host; cell ])

      roBinds.Length |> Expect.equal "one --ro-bind per RoBinds entry, plus the fixed /usr" (1 + sampleSpec.RoBinds.Length)

    testCase "args RW-binds exactly CellSpec.RwBinds — the only writable window into a sealed cell (§4.1's data plane)" <| fun _ ->
      let a = args sampleSpec
      argsAfter "--bind" 2 a |> Expect.equal "the /out mount, host then cell path" [ [ "/host/out"; "/out" ] ]

    testCase "args passes every Env entry via --setenv, after --clearenv (no host env leaks in)" <| fun _ ->
      let a = args sampleSpec
      let clearIdx = a |> List.findIndex ((=) "--clearenv")
      let firstSetenvIdx = a |> List.findIndex ((=) "--setenv")
      firstSetenvIdx > clearIdx |> Expect.isTrue "--setenv comes after --clearenv"
      argsAfter "--setenv" 2 a |> Expect.equal "the one Env entry, key then value" [ [ "LIBGL_ALWAYS_SOFTWARE"; "1" ] ]

    testCase "args ends with -- followed by InnerCommand, verbatim" <| fun _ ->
      let a = args sampleSpec
      let dashDashIdx = a |> List.findIndex ((=) "--")
      a |> List.skip (dashDashIdx + 1) |> Expect.equal "inner command follows --, unchanged" sampleSpec.InnerCommand
  ]
