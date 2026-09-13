/// The VS Code actor's own RED tests (demo-actors-plan.md §2.1's item 2:
/// "a demos harness test that Actors/VsCode.fs resolves a non-empty rect
/// for a real caret target against a launched VS Code on Xvfb — RED until
/// launch+rectFor work"). Split into two tiers: fast, always-run
/// fail-loud/graceful-degradation proofs (no external dependency, run in
/// the default suite), and one genuinely heavy `[Integration]` proof that
/// launches a REAL VS Code build on a REAL isolated Xvfb display and calls
/// this actor's real `ResolveRect` against it — `Tests.skiptest` (a
/// visible "Skipped", never a silent pass) when this exact box's own
/// missing `xdotool`/VS Code build make that genuinely impossible, per
/// `Actors/VsCode.fs`'s own module-doc account of this island's spike.
module SageFs.Demos.Tests.VsCodeActorTests

open System
open System.Diagnostics
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Demos
open SageFs.Demos.Domain
open SageFs.Demos.Actors

let private nowhereRect: Rect = { X = 0; Y = 0; W = 1280; H = 720 }

[<Tests>]
let fastTests =
  testList "VsCode actor - fail-loud and graceful-degradation contract" [

    testCase "WHY - launch fails loud (never a bare exception) when the VS Code binary does not exist" <| fun _ ->
      let result =
        VsCode.launch "/definitely/not/a/real/code/binary" "/tmp/ext" "/tmp/sfsdemo-fast/ud" "/tmp/sfsdemo-fast/ext" "/tmp/sfsdemo-fast/ws" nowhereRect ":99" 47799
        |> Async.RunSynchronously

      match result with
      | Error msg -> msg |> Expect.stringContains "actionable error names the fix" "SAGEFS_TE_VSCODE_PATH"
      | Ok _ -> failwith "expected a fail-loud Error for a nonexistent VS Code binary"

    testCase "WHY - launch fails loud when --user-data-dir would exceed VS Code's own IPC socket path ceiling" <| fun _ ->
      // A path deep enough to trip the ~107-char AF_UNIX ceiling this
      // island's own live spike hit (`Error: listen EINVAL`) — checked
      // BEFORE ever spawning a process, so this needs no real binary.
      let longPath = "/tmp/" + String.replicate 10 "deeply-nested-scratch-dir/"
      let result =
        VsCode.launch "/usr/bin/true" "/tmp/ext" longPath "/tmp/ext-dir" "/tmp/ws" nowhereRect ":99" 47799
        |> Async.RunSynchronously

      match result with
      | Error msg -> msg |> Expect.stringContains "actionable error names the length problem" "too long"
      | Ok _ -> failwith "expected a fail-loud Error for an over-long --user-data-dir"

    testCase "WHY - resolveRect against nothing listening is an honest None, never a thrown exception" <| fun _ ->
      let handle: VsCode.Handle =
        { Process = Unchecked.defaultof<_>
          ControlBaseUrl = "http://127.0.0.1:1"
          Http = new Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds 2.0) }

      let result = VsCode.resolveRect handle "editor" |> Async.RunSynchronously
      result |> Expect.equal "no control channel listening ⇒ honest None, not a crash" None

    testCase "WHY - observe against nothing listening is an honest false within its timeout, never a hang" <| fun _ ->
      let handle: VsCode.Handle =
        { Process = Unchecked.defaultof<_>
          ControlBaseUrl = "http://127.0.0.1:1"
          Http = new Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds 2.0) }

      let result = VsCode.observe handle "http://127.0.0.1:1" "daemon:sessionReady" 500.0 |> Async.RunSynchronously
      result |> Expect.equal "unreachable daemon ⇒ honest false" false

    testCase "WHY - an unrecognized observe selector is an honest false, never a fabricated pass" <| fun _ ->
      let handle: VsCode.Handle =
        { Process = Unchecked.defaultof<_>
          ControlBaseUrl = "http://127.0.0.1:1"
          Http = new Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds 2.0) }

      let result = VsCode.observe handle "http://127.0.0.1:1" "not-a-real-signal" 200.0 |> Async.RunSynchronously
      result |> Expect.equal "unknown selector ⇒ honest false" false

    testCase "WHY - Runtime.VsCode.resolveCodeBin fails loud (mirrors --integration-vsc) when nothing is cached and no env override is set" <| fun _ ->
      let priorEnv = Environment.GetEnvironmentVariable "SAGEFS_TE_VSCODE_PATH"
      Environment.SetEnvironmentVariable("SAGEFS_TE_VSCODE_PATH", null)

      try
        match Runtime.VsCode.resolveCodeBin "/tmp/definitely-not-a-sagefs-repo-root" with
        | Error msg -> msg |> Expect.stringContains "actionable message names the fix" "SAGEFS_TE_VSCODE_PATH"
        | Ok p -> failwithf "expected Error for a repo root with no cached VS Code build, got Ok %s" p
      finally
        Environment.SetEnvironmentVariable("SAGEFS_TE_VSCODE_PATH", priorEnv)

    testCase "WHY - toLiveActor reports ActorId.VsCode, matching the seam's own dispatch table" <| fun _ ->
      let handle: VsCode.Handle =
        { Process = Unchecked.defaultof<_>
          ControlBaseUrl = "http://127.0.0.1:1"
          Http = new Net.Http.HttpClient() }

      let liveActor = VsCode.toLiveActor handle "http://127.0.0.1:1"
      liveActor.Id |> Expect.equal "the wrapped VS Code actor reports ActorId.VsCode" ActorId.VsCode
      CellAgent.actorIdOfString "vscode" |> Expect.equal "the seam's own token maps to the same ActorId" (Some liveActor.Id)
  ]

/// Finds a repo root the same way `Runtime.fs`'s own `findRepoRoot` does
/// (walk up looking for `SageFs.slnx`) — this test needs it to locate the
/// cached VS Code build under `sagefs-vscode/.vscode-test/` and the
/// COMPILED extension under `sagefs-vscode/dist/`.
let rec private findRepoRoot (dir: string) : string option =
  if File.Exists(Path.Combine(dir, "SageFs.slnx")) then
    Some dir
  else
    match Directory.GetParent dir with
    | null -> None
    | parent -> findRepoRoot parent.FullName

let private commandExists (name: string) : bool =
  try
    let psi = ProcessStartInfo("which", name, RedirectStandardOutput = true, UseShellExecute = false)
    use p = Process.Start psi
    p.WaitForExit()
    p.ExitCode = 0
  with _ ->
    false

[<Tests>]
let integrationTests =
  testList "[Integration] VsCode actor - real launch on a real isolated Xvfb" [

    testCase "WHY - resolveRect against a REAL launched VS Code returns real ext-host-sourced geometry" <| fun _ ->
      let repoRoot = findRepoRoot (Directory.GetCurrentDirectory ()) |> Option.defaultValue (Directory.GetCurrentDirectory ())
      let extDevPath = Runtime.VsCode.extensionDevPath repoRoot
      let compiledExtension = Path.Combine(extDevPath, "dist", "Extension.js")

      match Runtime.VsCode.resolveCodeBin repoRoot with
      | Error msg -> Tests.skiptest (sprintf "no VS Code build resolvable: %s" msg)
      | Ok _ when not (File.Exists compiledExtension) ->
        Tests.skiptest (sprintf "extension not compiled — run `npm run compile` under %s first" extDevPath)
      | Ok codeBin when not (commandExists "Xvfb") -> Tests.skiptest "Xvfb not installed on this box"
      | Ok codeBin ->

      let scratch = Path.Combine(Path.GetTempPath (), "sagefs-demos-vscode-actor-test")
      if Directory.Exists scratch then Directory.Delete(scratch, true)
      [ "userdata"; "userdata-vscode"; "extensions"; "workspace" ] |> List.iter (fun d -> Directory.CreateDirectory(Path.Combine(scratch, d)) |> ignore)
      File.WriteAllText(Path.Combine(scratch, "workspace", "dummy.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")

      let display = ":198"
      let xvfb = ProcessStartInfo("Xvfb", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
      for a in [ display; "-screen"; "0"; "1280x720x24"; "-nocursor" ] do
        xvfb.ArgumentList.Add a
      use xvfbProc = Process.Start xvfb

      let socketPath = sprintf "/tmp/.X11-unix/X%s" (display.TrimStart ':')
      let mutable waited = 0
      while not (File.Exists socketPath) && waited < 5000 do
        Threading.Thread.Sleep 100
        waited <- waited + 100

      try
        if not (File.Exists socketPath) then
          failwith "Xvfb never came up"

        let handle =
          VsCode.launch
            codeBin
            extDevPath
            (Path.Combine(scratch, "userdata-vscode"))
            (Path.Combine(scratch, "extensions"))
            (Path.Combine(scratch, "workspace"))
            { X = 0; Y = 0; W = 1280; H = 720 }
            display
            47798
          |> Async.RunSynchronously

        match handle with
        | Error msg -> failwithf "VS Code actor failed to launch: %s" msg
        | Ok h ->
          try
            // The extension activates on `workspaceContains:**/*.fsproj`
            // (a real one is in `scratch/workspace`) — give it a moment
            // beyond `launch`'s own settle sleep before the first probe.
            Async.Sleep 2000 |> Async.RunSynchronously
            let rect = VsCode.resolveRect h "editor" |> Async.RunSynchronously

            if not (commandExists "xdotool") then
              // Documented, honest degradation (Actors/VsCode.fs's own
              // module doc): without xdotool the control channel still
              // answers, but window geometry genuinely cannot resolve.
              rect |> Expect.equal "no xdotool ⇒ the command still runs, honestly returning no rect" None
            else
              match rect with
              | None -> failwith "expected a real rect with xdotool present — the control channel or geometry query regressed"
              | Some r ->
                Expect.isTrue "editor rect has non-zero width" (r.W > 0)
                Expect.isTrue "editor rect has non-zero height" (r.H > 0)
          finally
            VsCode.close h |> Async.RunSynchronously
      finally
        (try
          if not xvfbProc.HasExited then
            xvfbProc.Kill()
         with _ ->
           ())
  ]
