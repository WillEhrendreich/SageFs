/// Stamps that let a pipeline stage skip work whose inputs have not changed: the decision, the key, and the shell
/// guard that skips `npm ci`. The rules are build/BuildStamps.fs; ci-pipeline.fsx only supplies the facts.
module SageFs.Tests.BuildStampsTests

open System
open System.Diagnostics
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Build
open SageFs.Build.BuildStamps

let parts =[ "sdk", "11.0.100"; "head", "abc123"; "tree", "clean" ]

let pureTests =
  testList "Build stamps (pure)" [
    testCase "the same inputs make the same key, and the key is a SHA-256" <| fun _ ->
      keyOf parts |> Expect.equal "deterministic" (keyOf parts)
      keyOf parts |> Expect.hasLength "64 hex characters" 64

    testProperty "changing any one input changes the key" <|
      fun (index: byte) (replacement: NonEmptyString) ->
        let i = int index % parts.Length
        let changed = parts |> List.mapi (fun j (name, value) -> match j = i with | true -> name, value + "x" + replacement.Get | false -> name, value)
        keyOf changed <> keyOf parts

    testCase "a value cannot slide into the next input's place" <| fun _ ->
      keyOf [ "a", "b\nc=d" ] |> Expect.notEqual "one input holding a newline is not two inputs" (keyOf [ "a", "b"; "c", "d" ])

    testCase "the stage is skipped only when the stamp is the current key and the outputs are there" <| fun _ ->
      let key = keyOf parts
      decide key (Stamp key) Outputs.Present |> Expect.equal "everything matches" Verdict.UpToDate
      (match decide key (Stamp "old") Outputs.Present with
       | Verdict.Rebuild _ -> ()
       | other -> failtestf "a stale stamp must rebuild, got %A" other)
      (match decide key NoStamp Outputs.Present with
       | Verdict.Rebuild _ -> ()
       | other -> failtestf "no stamp must rebuild, got %A" other)
      (match decide key (Stamp key) (Outputs.Missing "a.nupkg") with
       | Verdict.Rebuild why -> why |> Expect.stringContains "the missing output is named" "a.nupkg"
       | other -> failtestf "a stamp over missing outputs must rebuild, got %A" other)

    testCase "a stamp file round-trips, and a missing or unreadable one is no stamp" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "sagefs-stamp-" + Guid.NewGuid().ToString "N")
      try
        let path = Path.Combine(dir, "nested", "x.stamp")
        read path |> Expect.equal "nothing there yet" NoStamp
        write path "k1"
        read path |> Expect.equal "what was written" (Stamp "k1")
        write path "k2"
        read path |> Expect.equal "rewritten whole" (Stamp "k2")
        File.WriteAllText(path, "")
        read path |> Expect.equal "an empty file is no stamp" NoStamp
      finally
        try Directory.Delete(dir, true) with _ -> ()
  ]

/// Runs `sh -c script` in `dir` with `bin` first on PATH, returns the exit code.
let runShell(dir: string) (bin: string) (script: string) : Threading.Tasks.Task<int> =
  task {
    let psi = ProcessStartInfo("sh")
    psi.WorkingDirectory <- dir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.ArgumentList.Add "-c"
    psi.ArgumentList.Add script
    psi.Environment["PATH"] <- bin + ":" + Environment.GetEnvironmentVariable "PATH"
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEndAsync()
    let err = p.StandardError.ReadToEndAsync()
    do! p.WaitForExitAsync()
    let! _ = out
    let! _ = err
    return p.ExitCode
  }

let npmTests =
  testList "npm ci guard" [
    testTask "npm ci runs when the lockfile is new or changed, and is skipped when it is not" {
      let root = Path.Combine(Path.GetTempPath(), "sagefs-npm-guard-" + Guid.NewGuid().ToString "N")
      let bin = Path.Combine(root, "bin")
      let project = Path.Combine(root, "project")
      Directory.CreateDirectory bin |> ignore
      Directory.CreateDirectory project |> ignore
      let calls = Path.Combine(root, "calls.txt")
      // A stand-in `npm` and `node`: `npm ci` records the call and makes node_modules, as the real one does.
      File.WriteAllText(Path.Combine(bin, "npm"), sprintf "#!/bin/sh\necho \"npm $*\" >> '%s'\nmkdir -p node_modules\n" calls)
      File.WriteAllText(Path.Combine(bin, "node"), "#!/bin/sh\necho v99.0.0\n")
      for tool in [ "npm"; "node" ] do
        File.SetUnixFileMode(Path.Combine(bin, tool), UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
      try
        File.WriteAllText(Path.Combine(project, "package-lock.json"), "{\"v\":1}")
        let installs () = match File.Exists calls with | true -> File.ReadAllLines calls |> Array.length | false -> 0
        let script = npmCiScript [ "--include=dev" ]
        let! first = runShell project bin script
        first |> Expect.equal "first run succeeds" 0
        installs () |> Expect.equal "first run installs" 1
        let! second = runShell project bin script
        second |> Expect.equal "second run succeeds" 0
        installs () |> Expect.equal "same lockfile: skipped" 1
        File.WriteAllText(Path.Combine(project, "package-lock.json"), "{\"v\":2}")
        let! _ = runShell project bin script
        installs () |> Expect.equal "changed lockfile: installs again" 2
        Directory.Delete(Path.Combine(project, "node_modules"), true)
        let! _ = runShell project bin script
        installs () |> Expect.equal "node_modules gone: installs again even with the same lockfile" 3
        (File.ReadAllText calls).Contains "ci --include=dev" |> Expect.isTrue "the arguments are passed to npm ci"
      finally
        try Directory.Delete(root, true) with _ -> ()
    }
  ]

[<Tests>]
let tests = testList "Build stamps suite" [ pureTests; npmTests ]
