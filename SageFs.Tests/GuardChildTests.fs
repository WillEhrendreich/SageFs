/// The guards against the failures that end a process, run in a child process because that is the only place they can
/// be shown: a stack overflow kills the process that has it, and a loop nothing stops never returns. Each scenario
/// clicks one real getter through the real evaluator and the real patcher. The controls run the same getters with the
/// guards off, so a green case means the guard did it and not that the getter was gentle.
module SageFs.Tests.GuardChildTests

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Tests.GuardChild

/// What the child said and how it ended.
type private ChildResult =
  { ExitCode: int
    /// The `GUARDCHILD` line as `key=value` pairs, empty when the child never printed one (it died first).
    Fields: Map<string, string>
    Stdout: string
    Stderr: string }

/// How to start this assembly again: `dotnet SageFs.Tests.dll` when the host is the dotnet muxer, the executable itself
/// when it is the apphost.
type private Launcher =
  | ThroughDotnet of dotnet: string * assembly: string
  | Direct of executable: string

let private launcher () : Launcher =
  let executable = Environment.ProcessPath
  match Path.GetFileNameWithoutExtension executable with
  | "dotnet" -> ThroughDotnet (executable, Assembly.GetExecutingAssembly().Location)
  | _ -> Direct executable

let private parseFields (stdout: string) : Map<string, string> =
  let line = stdout.Split('\n') |> Array.tryFind (fun l -> l.StartsWith "GUARDCHILD ")
  match line with
  | None -> Map.empty
  | Some found ->
    // `result=` can hold spaces (an exception message), so each field starts at its own ` key=`.
    let marker = [ "scenario"; "result"; "trip"; "guarded"; "pending" ]
    let starts = marker |> List.map (fun key -> key, found.IndexOf(" " + key + "=")) |> List.filter (fun (_, i) -> i >= 0) |> List.sortBy snd
    starts
    |> List.mapi (fun n (key, i) ->
      let valueStart = i + key.Length + 2
      let valueEnd = match List.tryItem (n + 1) starts with | Some (_, next) -> next | None -> found.Length
      key, found.Substring(valueStart, valueEnd - valueStart).Trim())
    |> Map.ofList

/// Runs one scenario in a child process. Both streams are drained into files while it runs, because a redirected pipe
/// nobody reads fills and stops the child before it says anything.
let private runChild (scenario: GuardScenario) : Task<ChildResult> =
  task {
    let directory = Path.Combine(Path.GetTempPath(), sprintf "sagefs-guard-child-%s" (Guid.NewGuid().ToString "N"))
    Directory.CreateDirectory directory |> ignore
    let stdoutPath = Path.Combine(directory, "stdout.txt")
    let stderrPath = Path.Combine(directory, "stderr.txt")
    try
      let info =
        match launcher () with
        | ThroughDotnet (dotnet, assembly) ->
          let i = ProcessStartInfo(dotnet)
          i.ArgumentList.Add assembly
          i
        | Direct executable -> ProcessStartInfo(executable)
      info.ArgumentList.Add "--guard-child"
      info.ArgumentList.Add (GuardScenario.toArgument scenario)
      info.RedirectStandardOutput <- true
      info.RedirectStandardError <- true
      info.UseShellExecute <- false
      info.CreateNoWindow <- true
      use stdoutFile = new StreamWriter(stdoutPath)
      use stderrFile = new StreamWriter(stderrPath)
      use child = new Process(StartInfo = info)
      child.OutputDataReceived.Add(fun e -> lock stdoutFile (fun () -> match e.Data with | null -> () | line -> stdoutFile.WriteLine line))
      child.ErrorDataReceived.Add(fun e -> lock stderrFile (fun () -> match e.Data with | null -> () | line -> stderrFile.WriteLine line))
      child.Start() |> ignore
      child.BeginOutputReadLine()
      child.BeginErrorReadLine()
      use cts = new CancellationTokenSource(TestTimeouts.patience)
      try
        do! child.WaitForExitAsync cts.Token
      with :? OperationCanceledException ->
        child.Kill true
        failtestf "the child for %s did not end within %O" (GuardScenario.toArgument scenario) TestTimeouts.patience
      // The no-argument wait returns once the redirected streams reached end of file.
      child.WaitForExit()
      stdoutFile.Flush()
      stderrFile.Flush()
      let stdout = File.ReadAllText stdoutPath
      return
        { ExitCode = child.ExitCode
          Fields = parseFields stdout
          Stdout = stdout
          Stderr = File.ReadAllText stderrPath }
    finally
      try Directory.Delete(directory, true) with _ -> ()
  }

let private field (name: string) (child: ChildResult) : string =
  match Map.tryFind name child.Fields with
  | Some value -> value
  | None -> failtestf "the child printed no %s (exit %d). stdout: %s stderr: %s" name child.ExitCode child.Stdout child.Stderr

let private survived (child: ChildResult) =
  child.ExitCode |> Expect.equal (sprintf "the child lived to say so. stdout: %s stderr: %s" child.Stdout child.Stderr) 0

[<Tests>]
let guardChildTests =
  testList "guards against what ends a process (child processes)" [

    testTask "WHY — a getter that returns is guarded in several methods and unaffected" {
      let! child = runChild Returns
      survived child
      field "result" child |> Expect.equal "its value" "ok:42"
      field "trip" child |> Expect.equal "nothing tripped" "none"
      field "guarded" child |> Expect.equal "the getter itself" "1"
      field "pending" child |> Expect.equal "no stop left pending" "0"
    }

    testTask "WHY — a getter that recurses forever throws instead of ending the process, and says the stack ran out" {
      let! child = runChild RecursesForever
      survived child
      field "result" child |> Expect.stringStarts "a throw" "threw:"
      field "result" child |> Expect.stringContains "that says why" "stack"
      field "trip" child |> Expect.equal "the stack guard" "stack"
      field "pending" child |> Expect.equal "no stop left pending" "0"
    }

    testTask "WHY — the same recursion with guards off ends the process: the control that shows the guard did it" {
      let! child = runChild RecursesForeverUnguarded
      child.Fields |> Expect.isEmpty "the child never got to say anything"
      child.ExitCode |> Expect.notEqual "the process was killed" 0
      child.Stderr |> Expect.stringContains "by a stack overflow" "Stack overflow"
    }

    testTask "WHY — a getter that spins forever is stopped at the deadline, its thread ends, and nothing is left pending" {
      let! child = runChild SpinsForever
      survived child
      field "result" child |> Expect.equal "timed out" "timed-out"
      field "trip" child |> Expect.equal "a loop guard stopped it" "loop"
      field "pending" child |> Expect.equal "the thread ended, so its stop was let go" "0"
    }

    testTask "WHY — the same spin with guards off is only given up on: its thread keeps running and its stop stays pending" {
      let! child = runChild SpinsForeverUnguarded
      survived child
      field "result" child |> Expect.equal "timed out" "timed-out"
      field "trip" child |> Expect.equal "no guard stopped it" "none"
      field "guarded" child |> Expect.equal "nothing was guarded" "0"
      field "pending" child |> Expect.equal "the abandoned thread still has a stop pending" "1"
    }

    testTask "WHY — a spin inside a catch-all is still stopped, because the stop is thrown again at the next check" {
      let! child = runChild SpinsInsideACatchAll
      survived child
      field "result" child |> Expect.equal "timed out" "timed-out"
      field "trip" child |> Expect.equal "stopped" "loop"
      field "pending" child |> Expect.equal "the thread ended" "0"
    }

    testTask "WHY — a spin in a helper the getter calls is stopped, because the helper was reached and guarded too" {
      let! child = runChild SpinsInAHelper
      survived child
      field "trip" child |> Expect.equal "stopped" "loop"
      (int (field "guarded" child), 2) |> Expect.isGreaterThanOrEqual "the getter and the helper"
    }

    testTask "WHY — a blocked wait is freed by Interrupt, and no guard is claimed for it" {
      let! child = runChild BlocksOnAWait
      survived child
      field "result" child |> Expect.equal "timed out" "timed-out"
      field "trip" child |> Expect.equal "nothing tripped" "none"
      field "pending" child |> Expect.equal "the thread ended through the interrupt" "0"
    }

    testTask "WHY — a spin inside an async state machine is the stated limit: only the deadline gives up on it, the thread is abandoned, the process lives" {
      let! child = runChild SpinsInAStateMachine
      survived child
      field "result" child |> Expect.equal "timed out" "timed-out"
      field "trip" child |> Expect.equal "the state machine is not guarded" "none"
      field "pending" child |> Expect.equal "the abandoned thread still has a stop pending" "1"
    }
  ]
