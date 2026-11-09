module SageFs.Tests.ThreadSandboxTests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs

// ---- a classic-BPF interpreter, so the tests run the program the way the kernel would ----
//
// The numbers below are written out again on purpose. They come from the Linux x86-64 syscall table
// (arch/x86/entry/syscalls/syscall_64.tbl), include/uapi/linux/seccomp.h and include/uapi/asm-generic/fcntl.h.
// If the builder and this file shared constants, a wrong number would agree with itself.

/// What the kernel hands a seccomp filter (struct seccomp_data).
type private SeccompData = { Nr: int; Arch: uint32; Args: uint64[] }

/// What the program decided, decoded from the 32-bit return value.
type private Verdict =
  | VAllow
  | VErrno of int
  | VKill
  | VTrap
  | VOther of uint32
  | VFault of string

let private auditArchX86_64 = 0xC000003Eu
let private auditArchI386 = 0x40000003u
let private auditArchAarch64 = 0xC00000B7u

let private retActionMask = 0xFFFF0000u
let private retAllow = 0x7FFF0000u
let private retErrno = 0x00050000u
let private retTrap = 0x00030000u
let private retKillThread = 0x00000000u
let private retKillProcess = 0x80000000u

let private eperm = 1
let private enosys = 38

let private bpfLdWAbs = 0x20us
let private bpfJeqK = 0x15us
let private bpfJgeK = 0x35us
let private bpfJsetK = 0x45us
let private bpfRetK = 0x06us

let private instructionCount (program: byte[]) = program.Length / 8

/// A sock_filter is little endian on every architecture the filter is verified for.
let private u16At (b: byte[]) (i: int) = uint16 b[i] ||| (uint16 b[i + 1] <<< 8)
let private u32At (b: byte[]) (i: int) =
  uint32 b[i] ||| (uint32 b[i + 1] <<< 8) ||| (uint32 b[i + 2] <<< 16) ||| (uint32 b[i + 3] <<< 24)

let private decode (k: uint32) =
  let action = k &&& retActionMask
  match action with
  | a when a = retAllow -> VAllow
  | a when a = retErrno -> VErrno (int (k &&& 0xFFFFu))
  | a when a = retTrap -> VTrap
  | a when a = retKillThread || a = retKillProcess -> VKill
  | _ -> VOther k

let private loadWord (d: SeccompData) (k: uint32) =
  match k with
  | 0u -> Ok (uint32 d.Nr)
  | 4u -> Ok d.Arch
  | 8u | 12u -> Ok 0u
  | k when k >= 16u && k < 64u && k % 4u = 0u ->
    let arg = d.Args[int ((k - 16u) / 8u)]
    Ok (match (k - 16u) % 8u with
        | 0u -> uint32 (arg &&& 0xFFFFFFFFUL)
        | _ -> uint32 (arg >>> 32))
  | k -> Error (sprintf "load at offset %d is outside seccomp_data" k)

/// Runs the program on one syscall. Every step is bounds-checked and the program may only jump forward, so
/// a bad program is a VFault here instead of a hang or a crash.
let private runBpf (program: byte[]) (d: SeccompData) : Verdict =
  let count = instructionCount program
  let mutable pc = 0
  let mutable acc = 0u
  let mutable result = None
  let mutable steps = 0
  while result.IsNone do
    steps <- steps + 1
    match pc >= count || steps > count + 1 with
    | true -> result <- Some (VFault (sprintf "ran off the program at pc %d" pc))
    | false ->
      let at = pc * 8
      let code = u16At program at
      let jt = int program[at + 2]
      let jf = int program[at + 3]
      let k = u32At program (at + 4)
      match code with
      | c when c = bpfLdWAbs ->
        match loadWord d k with
        | Ok v -> acc <- v; pc <- pc + 1
        | Error e -> result <- Some (VFault e)
      | c when c = bpfJeqK -> pc <- pc + 1 + (if acc = k then jt else jf)
      | c when c = bpfJgeK -> pc <- pc + 1 + (if acc >= k then jt else jf)
      | c when c = bpfJsetK -> pc <- pc + 1 + (if acc &&& k <> 0u then jt else jf)
      | c when c = bpfRetK -> result <- Some (decode k)
      | c -> result <- Some (VFault (sprintf "unknown opcode 0x%x at pc %d" c pc))
  result.Value

// ---- the model: what the filter is supposed to decide, written without looking at the builder ----

let private x32Bit = 0x40000000

let private nrSocket = 41
let private nrClone = 56
let private nrClone3 = 435
let private nrOpen = 2
let private nrOpenat = 257
let private nrRead = 0
let private nrWrite = 1
let private nrMmap = 9
let private nrFutex = 202
let private nrMadvise = 28
let private nrGetpid = 39
let private nrExit = 60

/// socket, connect, accept, sendto, sendmsg, bind, listen, socketpair, accept4, sendmmsg.
let private networkNrs = [ 41; 42; 43; 44; 46; 49; 50; 53; 288; 307 ]

/// truncate, ftruncate, rename, mkdir, rmdir, creat, link, unlink, symlink, chmod, fchmod, chown, fchown,
/// lchown, mknod, mkdirat, mknodat, fchownat, unlinkat, renameat, linkat, symlinkat, fchmodat, renameat2,
/// openat2, fchmodat2, then the spawn calls fork, vfork, execve, execveat.
let private deniedOutrightNrs =
  [ 76; 77; 82; 83; 84; 85; 86; 87; 88; 90; 91; 92; 93; 94; 133; 258; 259; 260; 263; 264; 265; 266; 268; 316; 437; 452
    57; 58; 59; 322 ]

let private oWronly = 0x1UL
let private oRdwr = 0x2UL
let private oCreat = 0x40UL
let private oTrunc = 0x200UL
let private oAppend = 0x400UL
let private writeFlagsMask = oWronly ||| oRdwr ||| oCreat ||| oTrunc ||| oAppend
let private cloneThread = 0x10000UL

let private low32 (v: uint64) = v &&& 0xFFFFFFFFUL

type private Expected =
  | ExpectAllow
  | ExpectErrno of int

let private model (policy: SandboxPolicy) (d: SeccompData) : Expected =
  let writesAndSpawn =
    match policy with
    | NoNetwork -> false
    | NoNetworkNoWritesNoSpawn -> true
  match d.Arch = auditArchX86_64, d.Nr >= x32Bit || d.Nr < 0 with
  | false, _ -> ExpectAllow
  | true, true -> ExpectErrno eperm
  | true, false ->
    match List.contains d.Nr networkNrs with
    | true -> ExpectErrno eperm
    | false ->
      match d.Nr with
      // A thread the filtered one made would inherit the filter for the life of the process, and the runtime makes its pool,
      // gate and timer threads on behalf of whichever thread asks. So no policy lets the filtered thread make one.
      | nr when nr = nrClone && (low32 d.Args[0] &&& cloneThread) <> 0UL -> ExpectErrno eperm
      | nr when nr = nrClone3 -> ExpectErrno enosys
      | _ ->
        match writesAndSpawn with
        | false -> ExpectAllow
        | true ->
          match d.Nr with
          | nr when List.contains nr deniedOutrightNrs -> ExpectErrno eperm
          | nr when nr = nrOpen ->
            (match low32 d.Args[1] &&& writeFlagsMask with 0UL -> ExpectAllow | _ -> ExpectErrno eperm)
          | nr when nr = nrOpenat ->
            (match low32 d.Args[2] &&& writeFlagsMask with 0UL -> ExpectAllow | _ -> ExpectErrno eperm)
          | nr when nr = nrClone -> ExpectErrno eperm
          | _ -> ExpectAllow

let private verdictOf (e: Expected) =
  match e with
  | ExpectAllow -> VAllow
  | ExpectErrno n -> VErrno n

let private allPolicies = [ NoNetwork; NoNetworkNoWritesNoSpawn ]
let private allArchitectures = [ X86_64; Aarch64 ]

// ---- generators ----

/// Syscall numbers weighted toward the ones the filter has an opinion about, plus the x32 range.
let private genNr : Gen<int> =
  let interesting =
    networkNrs @ deniedOutrightNrs
    @ [ nrOpen; nrOpenat; nrClone; nrClone3; nrRead; nrWrite; nrMmap; nrFutex; nrMadvise; nrGetpid; nrExit ]
  Gen.frequency
    [ 4, Gen.elements interesting
      // The three calls whose verdict depends on an argument get their own weight.
      3, Gen.elements [ nrOpen; nrOpenat; nrClone ]
      2, Gen.choose (0, 600)
      1, Gen.choose (x32Bit, x32Bit + 600)
      1, Gen.elements [ -1; Int32.MinValue; Int32.MaxValue ] ]

let private genRandom64 : Gen<uint64> =
  Gen.map2 (fun hi lo -> (uint64 hi <<< 32) ||| uint64 lo) (Gen.choose (0, Int32.MaxValue)) (Gen.choose (0, Int32.MaxValue))

/// Arguments that sometimes have the write flags cleared and sometimes CLONE_THREAD flipped, so both sides of
/// every flag test are reached.
let private genArg : Gen<uint64> =
  Gen.frequency
    [ 2, genRandom64
      2, Gen.map (fun v -> v &&& ~~~writeFlagsMask) genRandom64
      1, Gen.map (fun v -> v ||| cloneThread) genRandom64
      1, Gen.map (fun v -> v &&& ~~~cloneThread) genRandom64
      // Exactly one write flag set, so a mask that misses any single bit is found.
      2, Gen.map2 (fun flag v -> (v &&& ~~~writeFlagsMask) ||| flag) (Gen.elements [ oWronly; oRdwr; oCreat; oTrunc; oAppend ]) genRandom64 ]

/// One verdict costs microseconds, and the argument-dependent calls are a small slice of the space, so run many.
let private manyRuns = { FsCheckConfig.defaultConfig with maxTest = 3000 }

let private genArch : Gen<uint32> =
  Gen.frequency
    [ 6, Gen.constant auditArchX86_64
      1, Gen.constant auditArchI386
      1, Gen.constant auditArchAarch64
      1, Gen.map uint32 (Gen.choose (0, Int32.MaxValue)) ]

let private genData : Gen<SeccompData> =
  gen {
    let! nr = genNr
    let! arch = genArch
    let! args = Gen.arrayOfLength 6 genArg
    return { Nr = nr; Arch = arch; Args = args }
  }

let private dataFor (nr: int) (args: uint64 list) =
  let padded = Array.append (Array.ofList args) (Array.zeroCreate 6) |> Array.take 6
  { Nr = nr; Arch = auditArchX86_64; Args = padded }

let private verdictIs (policy: SandboxPolicy) (nr: int) (args: uint64 list) =
  runBpf (Seccomp.denyProgram X86_64 policy) (dataFor nr args)

/// The same, for the program as it was before thread creation was refused (the twin).
let private verdictBefore (policy: SandboxPolicy) (nr: int) (args: uint64 list) =
  runBpf (Seccomp.denyProgramFor Seccomp.ThreadCreation.Allowed X86_64 policy) (dataFor nr args)

// ---- the platform guard for the tests that install a real filter ----

let private isLinuxX64 =
  OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture = Architecture.X64

/// The steps one sandbox call has reached, each with the milliseconds since the call was made. A timeout carries
/// them, so one failure says which step took the time: a missing `callerStarted` is a wait for a thread, a missing
/// `workStarted` is the filter install, a missing `workFinished` is the work, a missing `joined` is the hand-back.
type private SandboxTrace() =
  let began = Diagnostics.Stopwatch.GetTimestamp()
  let marks = System.Collections.Concurrent.ConcurrentQueue<string>()
  member _.Mark(step: string) =
    marks.Enqueue(sprintf "%s=%.0fms" step (Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds))
  member _.Steps = String.Join("; ", marks)

/// Runs the sandbox on a thread of its own with the suite's ceiling, so a stuck thread fails the test instead of
/// the run. The caller thread is not a pool thread: `Task.Run` here started the clock when the call was queued, and
/// a starved pool then lost 30 s waits for a call that takes 0.16 s alone. The clock now starts with the thread.
let private runSandboxedTracedWithin (patience: TimeSpan) (policy: SandboxPolicy) (work: (string -> unit) -> 'T) : Task<SandboxOutcome<'T>> =
  let trace = SandboxTrace()
  trace.Mark "called"
  let load = try (File.ReadAllText "/proc/loadavg").Split(' ').[0] with _ -> "?"
  let poolBusy = sprintf "pool pending=%d threads=%d" ThreadPool.PendingWorkItemCount ThreadPool.ThreadCount
  let call =
    TestInfrastructure.DedicatedThread.run "sagefs-sandbox-caller" (fun () ->
      trace.Mark "callerStarted"
      let outcome =
        ThreadSandbox.run policy (fun () ->
          trace.Mark "workStarted"
          let result = work trace.Mark
          trace.Mark "workFinished"
          result)
      trace.Mark "joined"
      outcome)
  task {
    try
      return! call.WaitAsync patience
    with :? TimeoutException ->
      return
        failwithf "the sandbox call did not finish in %A. load=%s, at the call %s, now pool pending=%d threads=%d. steps: [%s]"
          patience load poolBusy ThreadPool.PendingWorkItemCount ThreadPool.ThreadCount trace.Steps
  }

let private runSandboxedWithin (patience: TimeSpan) (policy: SandboxPolicy) (work: unit -> 'T) : Task<SandboxOutcome<'T>> =
  runSandboxedTracedWithin patience policy (fun _ -> work ())

let private runSandboxed (policy: SandboxPolicy) (work: unit -> 'T) : Task<SandboxOutcome<'T>> =
  runSandboxedWithin TestTimeouts.patienceInProcess policy work

/// What every real-filter case asserts on a host where no filter can be installed (not Linux x86-64):
/// the sandbox says so and the work does not run, so containment is never claimed there.
let private expectNoContainmentHere () : Task<unit> =
  task {
    let ran = ResizeArray<string>()
    let! outcome = runSandboxed NoNetwork (fun () -> ran.Add "ran"; 1)
    match outcome with
    | Unavailable _ -> ()
    | other -> failtestf "expected Unavailable on a host without the filter, got %A" other
    ran |> Expect.isEmpty "the work never ran"
  }

/// A case that installs a real seccomp filter. It runs `body` where the filter is verified (Linux x86-64) and
/// asserts the fail-closed answer everywhere else, so no host skips it.
let private realFilterCase (name: string) (body: unit -> Task<unit>) : Test =
  testTask name {
    match isLinuxX64 with
    | true -> do! body ()
    | false -> do! expectNoContainmentHere ()
  }

/// One field of the line a sandbox child wrote. A child that wrote nothing died or hung, and the failure shows what it printed.
let private childSaid (child: SandboxChild.ChildOutcome) (name: string) : string =
  match Map.tryFind name child.Fields with
  | Some value -> value
  | None -> failtestf "the child said no %s (exit %d). stdout: %s stderr: %s" name child.ExitCode child.Stdout child.Stderr

let private withLoopbackListener (body: int -> Task) : Task =
  task {
    let listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    try
      do! body (listener.LocalEndpoint :?> IPEndPoint).Port
    finally
      listener.Stop()
  }

/// Connects from a pool thread. The kernel completes the handshake from the listen backlog, so no accept is needed.
let private connectFromPoolThread (port: int) : Task =
  task {
    use client = new TcpClient()
    do! client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TestTimeouts.patienceInProcess)
  }

/// A blocking connect on whichever thread calls it.
let private connectHere (port: int) =
  use client = new TcpClient()
  client.Connect(IPAddress.Loopback, port)

let private withTempDir(body: string -> Task) : Task =
  task {
    let dir = Path.Combine(Path.GetTempPath(), "sagefs-sandbox-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    try
      do! body dir
    finally
      Directory.Delete(dir, true)
  }

[<Tests>]
let seccompProgramTests =
  testList "Seccomp.denyProgram (pure, every OS)" [

    testCase "WHY — the program is whole instructions and fits the kernel's 4096 limit, because the kernel rejects anything else at install" <| fun _ ->
      for arch in allArchitectures do
        for policy in allPolicies do
          let program = Seccomp.denyProgram arch policy
          (program.Length % 8) |> Expect.equal (sprintf "%A %A is whole 8-byte instructions" arch policy) 0
          (instructionCount program >= 1) |> Expect.isTrue (sprintf "%A %A is not empty" arch policy)
          (instructionCount program <= 4096) |> Expect.isTrue (sprintf "%A %A fits 4096 instructions" arch policy)

    testCase "WHY — every jump lands inside the program and goes forward, and the last instruction is a RET, so no path runs off the end" <| fun _ ->
      for arch in allArchitectures do
        for policy in allPolicies do
          let program = Seccomp.denyProgram arch policy
          let count = instructionCount program
          for i in 0 .. count - 1 do
            let code = u16At program (i * 8)
            let jt = int program[i * 8 + 2]
            let jf = int program[i * 8 + 3]
            match code with
            | c when c = bpfJeqK || c = bpfJgeK || c = bpfJsetK ->
              ((i + 1 + jt) < count && (i + 1 + jf) < count)
              |> Expect.isTrue (sprintf "%A %A: jump at %d lands inside the program" arch policy i)
            | _ -> ()
          let lastCode = u16At program ((count - 1) * 8)
          lastCode |> Expect.equal (sprintf "%A %A ends in RET" arch policy) bpfRetK

    testCase "WHY — no instruction returns KILL or TRAP, because a denied call must fail with an errno and nothing may kill the process" <| fun _ ->
      for arch in allArchitectures do
        for policy in allPolicies do
          let program = Seccomp.denyProgram arch policy
          for i in 0 .. instructionCount program - 1 do
            let code = u16At program (i * 8)
            match code = bpfRetK with
            | false -> ()
            | true ->
              let k = u32At program (i * 8 + 4)
              match decode k with
              | VAllow | VErrno _ -> ()
              | other -> failtestf "%A %A: RET at %d returns %A" arch policy i other

    testProperty "WHY — a run of the program always ends in ALLOW or ERRNO, never a fault, KILL or TRAP, whatever the syscall and arch"
    <| Prop.forAll (Arb.fromGen genData) (fun d ->
      allArchitectures
      |> List.forall (fun arch ->
        allPolicies
        |> List.forall (fun policy ->
          match runBpf (Seccomp.denyProgram arch policy) d with
          | VAllow | VErrno _ -> true
          | _ -> false)))

    testProperty "WHY — a syscall from an architecture the program does not know is ALLOWed, never killed, because the filter is a deny list for the one ABI it was written for"
    <| Prop.forAll (Arb.fromGen genData) (fun d ->
      let foreign = { d with Arch = (match d.Arch = auditArchX86_64 with true -> auditArchI386 | false -> d.Arch) }
      allPolicies |> List.forall (fun policy -> runBpf (Seccomp.denyProgram X86_64 policy) foreign = VAllow))

    testPropertyWithConfig manyRuns "WHY — NoNetwork decides every x86-64 syscall the way the model says, so the BPF and the policy cannot drift"
    <| Prop.forAll (Arb.fromGen genData) (fun d ->
      runBpf (Seccomp.denyProgram X86_64 NoNetwork) d = verdictOf (model NoNetwork d))

    testPropertyWithConfig manyRuns "WHY — NoNetworkNoWritesNoSpawn decides every x86-64 syscall the way the model says, flags included"
    <| Prop.forAll (Arb.fromGen genData) (fun d ->
      runBpf (Seccomp.denyProgram X86_64 NoNetworkNoWritesNoSpawn) d = verdictOf (model NoNetworkNoWritesNoSpawn d))

    testProperty "WHY — the same inputs give the same bytes, because the program is built once per policy and compared in tests"
    <| Prop.forAll (Arb.fromGen (Gen.elements allPolicies)) (fun policy ->
      allArchitectures
      |> List.forall (fun arch -> Seccomp.denyProgram arch policy = Seccomp.denyProgram arch policy))

    testCase "WHY — a read-only open is allowed under the full policy, because reading is what a getter legitimately does" <| fun _ ->
      let o_rdonly_cloexec = 0x80000UL
      verdictIs NoNetworkNoWritesNoSpawn nrOpenat [ 0UL; 0UL; o_rdonly_cloexec ] |> Expect.equal "openat read-only" VAllow
      verdictIs NoNetworkNoWritesNoSpawn nrOpen [ 0UL; 0UL ] |> Expect.equal "open read-only" VAllow

    testCase "WHY — an open that can create, write, truncate or append is refused with EPERM, one flag at a time" <| fun _ ->
      for flag in [ oWronly; oRdwr; oCreat; oTrunc; oAppend ] do
        verdictIs NoNetworkNoWritesNoSpawn nrOpenat [ 0UL; 0UL; flag ] |> Expect.equal (sprintf "openat flag 0x%x" flag) (VErrno eperm)
        verdictIs NoNetworkNoWritesNoSpawn nrOpen [ 0UL; flag ] |> Expect.equal (sprintf "open flag 0x%x" flag) (VErrno eperm)

    testCase "WHY — no policy lets the filtered thread make a thread, because a thread it made would inherit the filter for the life of the process" <| fun _ ->
      for policy in allPolicies do
        verdictIs policy nrClone [ cloneThread ] |> Expect.equal (sprintf "clone with CLONE_THREAD under %A" policy) (VErrno eperm)

    testCase "WHY — NoNetworkNoWritesNoSpawn refuses a clone without CLONE_THREAD too, so it makes no process either" <| fun _ ->
      verdictIs NoNetworkNoWritesNoSpawn nrClone [ 0UL ] |> Expect.equal "clone without CLONE_THREAD" (VErrno eperm)

    testCase "WHY — clone3 answers ENOSYS under every policy, because its flags are in a struct the filter cannot read and glibc then falls back to clone" <| fun _ ->
      for policy in allPolicies do
        verdictIs policy nrClone3 [ 0UL ] |> Expect.equal (sprintf "clone3 under %A" policy) (VErrno enosys)

    testCase "WHY — the twin: the program before thread creation was refused let the filtered thread make a thread, which is the leak" <| fun _ ->
      for policy in allPolicies do
        verdictBefore policy nrClone [ cloneThread ] |> Expect.equal (sprintf "clone with CLONE_THREAD under %A, before" policy) VAllow
        verdictIs policy nrClone [ cloneThread ] |> Expect.notEqual (sprintf "and now under %A" policy) VAllow

    testCase "WHY — NoNetwork leaves file writes and process creation alone, so the two policies are different tiers" <| fun _ ->
      verdictIs NoNetwork nrOpenat [ 0UL; 0UL; oCreat ||| oWronly ] |> Expect.equal "openat O_CREAT under NoNetwork" VAllow
      verdictIs NoNetwork nrClone [ 0UL ] |> Expect.equal "clone without CLONE_THREAD under NoNetwork" VAllow
      verdictIs NoNetwork nrSocket [ 2UL; 1UL; 0UL ] |> Expect.equal "socket under NoNetwork" (VErrno eperm)

    testCase "WHY — the pool is ready with any worker thread, and not ready with none, because with none there is nothing to serve what a getter queues" <| fun _ ->
      SandboxPool.readinessOf 0 |> Expect.equal "no worker" PoolReadiness.NoWorker
      for workers in [ 1; 2; 16; 184 ] do
        SandboxPool.readinessOf workers |> Expect.equal (sprintf "%d workers" workers) PoolReadiness.Ready

    testPropertyWithConfig manyRuns "WHY — however calls overlap, enter and leave and expire, the pool's limits are never lowered under one worker or raised above what the host set, and are back exactly when nothing is held"
      (fun (ops: byte list) (existing: byte) ->
        let host = { MinWorkers = 8; MaxWorkers = 32767; MinIo = 8; MaxIo = 1000 }
        let mutable limits = host
        let mutable hold = PoolHold.Free 0
        let mutable tickets : int list = []
        let move (m: PoolMove) =
          match m with
          | PoolMove.Keep -> ()
          | PoolMove.Pin pinned -> limits <- pinned
          | PoolMove.Restore saved -> limits <- saved
        let mutable ok = true
        let check () =
          ok <- ok && limits.MinWorkers >= 1 && limits.MinWorkers <= limits.MaxWorkers && limits.MaxWorkers <= host.MaxWorkers
          match hold with
          | PoolHold.Free _ -> ok <- ok && limits = host
          | PoolHold.Held _ -> ()
        for op in ops do
          match int op % 4 with
          | 0 ->
            let next, m, epoch = PoolHold.enter hold limits (int existing)
            hold <- next
            tickets <- epoch :: tickets
            move m
          | 1 ->
            // The oldest call to enter leaves, which may be a call of an epoch that already expired.
            (match List.rev tickets with
             | [] -> ()
             | oldest :: rest ->
               tickets <- List.rev rest
               let next, m = PoolHold.leave hold oldest
               hold <- next
               move m)
          | 2 ->
            (match tickets with
             | [] -> ()
             | newest :: rest ->
               tickets <- rest
               let next, m = PoolHold.leave hold newest
               hold <- next
               move m)
          | _ ->
            (match hold with
             | PoolHold.Held (epoch, _, _) ->
               let next, m = PoolHold.expire hold epoch
               hold <- next
               move m
             | PoolHold.Free _ -> ())
          check ()
        // Every call that is left ends: the limits are the host's again.
        for ticket in tickets do
          let next, m = PoolHold.leave hold ticket
          hold <- next
          move m
        (match hold with
         | PoolHold.Held (epoch, _, _) ->
           let next, m = PoolHold.expire hold epoch
           hold <- next
           move m
         | PoolHold.Free _ -> ())
        ok && limits = host)

    testCase "WHY — a call that overlaps another does not save the other's lowered limits as the ones to restore, and an expired call's late leave changes nothing" <| fun _ ->
      let host = { MinWorkers = 16; MaxWorkers = 32767; MinIo = 16; MaxIo = 1000 }
      let first, firstMove, firstEpoch = PoolHold.enter (PoolHold.Free 0) host 5
      firstMove |> Expect.equal "the first call pins to the 5 threads that exist" (PoolMove.Pin { host with MinWorkers = 1; MaxWorkers = 5 })
      let second, secondMove, secondEpoch = PoolHold.enter first (PoolHold.pinnedFor host 5) 5
      secondMove |> Expect.equal "the second call joins and moves nothing" PoolMove.Keep
      secondEpoch |> Expect.equal "in the same epoch" firstEpoch
      let afterFirstLeaves, moveOnFirstLeaving = PoolHold.leave second firstEpoch
      moveOnFirstLeaving |> Expect.equal "the first one out leaves the limits pinned for the second" PoolMove.Keep
      let afterSecondLeaves, moveOnLast = PoolHold.leave afterFirstLeaves secondEpoch
      moveOnLast |> Expect.equal "the last one out restores what the host had before the first" (PoolMove.Restore host)
      afterSecondLeaves |> Expect.equal "and nothing is held" (PoolHold.Free firstEpoch)
      // An epoch that expired while its call ran on: the call leaves late, after another epoch began.
      let held, _, abandonedEpoch = PoolHold.enter (PoolHold.Free 0) host 5
      let expired, expiredMove = PoolHold.expire held abandonedEpoch
      expiredMove |> Expect.equal "expiry restores" (PoolMove.Restore host)
      let later, _, laterEpoch = PoolHold.enter expired host 5
      let afterLate, lateMove = PoolHold.leave later abandonedEpoch
      lateMove |> Expect.equal "the abandoned call's leave moves nothing" PoolMove.Keep
      afterLate |> Expect.equal "and does not end the later epoch's hold" later
      laterEpoch |> Expect.notEqual "the later hold is a new epoch" abandonedEpoch

    testCase "WHY — the pinned limits never exceed what the host set and never fall under one worker, whatever the pool holds" <| fun _ ->
      let host = { MinWorkers = 8; MaxWorkers = 4; MinIo = 8; MaxIo = 1000 }
      (PoolHold.pinnedFor host 100).MaxWorkers |> Expect.equal "a host maximum of 4 is kept when 100 threads exist" 4
      (PoolHold.pinnedFor host 0).MaxWorkers |> Expect.equal "no threads still leaves one worker's room" 1
      (PoolHold.pinnedFor host 3).MinWorkers |> Expect.equal "the minimum is one" 1

    testCase "WHY — a pool that could not be prepared says so on the row: the getter was not run, and the reason names the pool" <| fun _ ->
      let text = SandboxUnavailable.describe (PoolNotReady "no worker thread exists")
      text |> Expect.stringContains "says the getter was not run" "was not run"
      text |> Expect.stringContains "names the thread pool" "thread pool"
      text |> Expect.stringContains "carries the reason" "no worker thread exists"

    testCase "WHY — an Aarch64 process is reported unavailable, because its syscall numbers are not verified on this host" <| fun _ ->
      match Seccomp.support Aarch64 with
      | Error (UnverifiedArchitecture _) -> ()
      | other -> failtestf "expected UnverifiedArchitecture, got %A" other
      Seccomp.support X86_64 |> Expect.equal "x86-64 is supported" (Ok ())

    testCase "WHY — the runtime's architecture maps to a sandbox architecture, and anything else is unverified rather than assumed" <| fun _ ->
      Seccomp.architectureOf Architecture.X64 |> Expect.equal "X64" (Ok X86_64)
      Seccomp.architectureOf Architecture.Arm64 |> Result.bind Seccomp.support
      |> Expect.isError "Arm64 is not usable"
      for other in [ Architecture.X86; Architecture.Arm; Architecture.Wasm; Architecture.S390x ] do
        match Seccomp.architectureOf other with
        | Error (UnverifiedArchitecture name) -> name |> Expect.isNotEmpty (sprintf "%A names itself" other)
        | result -> failtestf "%A: expected UnverifiedArchitecture, got %A" other result
  ]

[<Tests>]
let sandboxOutcomeTests =
  testList "ThreadSandbox.runWithInstaller (seam, every OS)" [

    testCase "WHY — work runs once, on a fresh dedicated thread that is not the caller and not a pool thread" <| fun _ ->
      let callerId = Thread.CurrentThread.ManagedThreadId
      let outcome =
        ThreadSandbox.runWithInstaller (fun () -> Ok ()) (fun () ->
          Thread.CurrentThread.ManagedThreadId, Thread.CurrentThread.IsThreadPoolThread, Thread.CurrentThread.IsBackground)
      match outcome with
      | Completed (id, isPool, isBackground) ->
        (id <> callerId) |> Expect.isTrue "a different thread from the caller"
        isPool |> Expect.isFalse "not a pool thread"
        isBackground |> Expect.isTrue "a background thread, so an abandoned one never holds the process open"
      | other -> failtestf "expected Completed, got %A" other

    testCase "WHY — an exception thrown by the work comes back as Threw with the same exception, and the process survives" <| fun _ ->
      let boom = InvalidOperationException "getter boom"
      match ThreadSandbox.runWithInstaller (fun () -> Ok ()) (fun () -> raise boom : int) with
      | Threw ex -> Object.ReferenceEquals(ex, boom) |> Expect.isTrue "the very exception the work threw"
      | other -> failtestf "expected Threw, got %A" other

    testCase "WHY — when the filter cannot be installed the work does not run and the outcome says why, so containment is never claimed falsely" <| fun _ ->
      let ran = ResizeArray<string>()
      let outcome =
        ThreadSandbox.runWithInstaller (fun () -> Error (InstallFailed ("seccomp", 22))) (fun () -> ran.Add "ran"; 1)
      outcome |> Expect.equal "unavailable with the step and errno" (Unavailable (InstallFailed ("seccomp", 22)))
      ran |> Expect.isEmpty "the work never ran"

    testCase "WHY — an installer that itself throws is Unavailable and the work does not run, because an unknown install state is not contained" <| fun _ ->
      let ran = ResizeArray<string>()
      let outcome =
        ThreadSandbox.runWithInstaller (fun () -> failwith "installer exploded") (fun () -> ran.Add "ran"; 1)
      match outcome with
      | Unavailable (InstallFailed _) -> ()
      | other -> failtestf "expected Unavailable InstallFailed, got %A" other
      ran |> Expect.isEmpty "the work never ran"

    testCase "WHY — a missing libc surfaces as LibraryNotFound with the library's name" <| fun _ ->
      let outcome =
        ThreadSandbox.runWithInstaller (fun () -> raise (DllNotFoundException "libc.so.6")) (fun () -> 1)
      match outcome with
      | Unavailable (LibraryNotFound name) -> name |> Expect.stringContains "names the library" "libc"
      | other -> failtestf "expected Unavailable LibraryNotFound, got %A" other

    testCase "WHY — every unavailable reason can be shown to a person, so the UI can say there is no I/O containment and why" <| fun _ ->
      let reasons =
        [ NotLinux
          UnverifiedArchitecture "Arm64"
          InstallFailed ("seccomp", 22)
          LibraryNotFound "libc.so.6" ]
      for reason in reasons do
        SandboxUnavailable.describe reason |> Expect.isNotEmpty (sprintf "%A has text" reason)
      SandboxUnavailable.describe (InstallFailed ("seccomp", 22)) |> Expect.stringContains "carries the step" "seccomp"
      SandboxUnavailable.describe (UnverifiedArchitecture "Arm64") |> Expect.stringContains "carries the architecture" "Arm64"

    testCase "WHY — an installer that reports NotLinux makes the outcome Unavailable NotLinux and the work does not run" <| fun _ ->
      let ran = ResizeArray<string>()
      let outcome = ThreadSandbox.runWithInstaller (fun () -> Error NotLinux) (fun () -> ran.Add "ran"; 1)
      outcome |> Expect.equal "NotLinux" (Unavailable NotLinux)
      ran |> Expect.isEmpty "the work never ran"

    testCase "WHY — the platform check names the reason on this host: NotLinux off Linux, and never NotLinux on it" <| fun _ ->
      match OperatingSystem.IsLinux(), ThreadSandbox.availability () with
      | false, Error NotLinux -> ()
      | true, Error NotLinux -> failtest "this is Linux, so availability must not say NotLinux"
      | false, other -> failtestf "off Linux, expected Error NotLinux, got %A" other
      | true, _ -> ()

    testCase "WHY — run answers what availability answers when no filter can be installed here, and the work does not run" <| fun _ ->
      let ran = ResizeArray<string>()
      let outcome = ThreadSandbox.run NoNetwork (fun () -> ran.Add "ran"; 1)
      match ThreadSandbox.availability () with
      | Error reason ->
        outcome |> Expect.equal "the same reason availability gave" (Unavailable reason)
        ran |> Expect.isEmpty "the work never ran"
      | Ok _ -> outcome |> Expect.equal "the filter installed and the work ran" (Completed 1)
  ]

[<Tests>]
let realThreadTests =
  testList "ThreadSandbox.run (real seccomp filter, Linux x86-64)" [

    realFilterCase "WHY — a connect on the filtered thread fails with Permission denied while another thread reaches the same listener before and after" <| fun () -> task {
      do!
        withLoopbackListener (fun port ->
          task {
            do! connectFromPoolThread port
            let! outcome =
              runSandboxed NoNetwork (fun () ->
                use client = new TcpClient()
                client.Connect(IPAddress.Loopback, port))
            match outcome with
            | Threw (:? SocketException as se) -> se.SocketErrorCode |> Expect.equal "EPERM is AccessDenied" SocketError.AccessDenied
            | other -> failtestf "expected a SocketException, got %A" other
            do! connectFromPoolThread port
          })
    }

    // The cases below depend on a fact about a whole process (a collection, a pool), so each runs in a process of its own
    // (SandboxChild) where no other case's threads are. They assert what the child READ, never how long anything took.
    realFilterCase "WHY — a sandbox call finishes while every pool thread is blocked, because neither the thread that makes it nor the thread its work runs on is a pool thread" <| fun () -> task {
      let! child = SandboxChild.run SandboxChild.SandboxScenario.BlockedPool
      childSaid child "callerIsPoolThread" |> Expect.equal "the caller is a thread of its own" "false"
      childSaid child "workIsPoolThread" |> Expect.equal "so is the thread the work runs on" "false"
      // Every worker the pool starts at once was inside a blocker when the work finished, so a call that needed the pool had none.
      (int (childSaid child "blockersRunningAtFinish") >= int (childSaid child "poolMinWorkers"))
      |> Expect.isTrue "the pool's workers were all blocked when the work finished"
      childSaid child "outcome" |> Expect.equal "and the work completed" "completed"
      childSaid child "sum" |> Expect.equal "with its answer" (string SandboxChild.expectedSum)
    }

    testTask "WHY — the probe the pool-independence case reads can say yes: a pool thread reports that it is one" {
      let! child = SandboxChild.run SandboxChild.SandboxScenario.ProbeSeesAPoolThread
      childSaid child "isPoolThread" |> Expect.equal "a work item on the pool is on a pool thread" "true"
    }

    realFilterCase "WHY — the filtered thread still allocates, collects garbage, JITs fresh code and gets a hot method promoted, so the runtime keeps working under the filter" <| fun () -> task {
      let! child = SandboxChild.run SandboxChild.SandboxScenario.RuntimeWork
      childSaid child "outcome" |> Expect.equal "the work completed" "completed"
      childSaid child "sum" |> Expect.equal "ordinary work" (string SandboxChild.expectedSum)
      childSaid child "hot" |> Expect.equal "a method called past the promotion threshold still answers" (string SandboxChild.hotCalls)
      childSaid child "compiled" |> Expect.equal "a compiled regex JITs under the filter" "true"
    }

    realFilterCase "WHY — a getter that queues pool work and waits for it leaves no thread under the filter in a fresh process, and a pool thread still writes and spawns" <| fun () -> task {
      let! child = SandboxChild.run SandboxChild.SandboxScenario.PoolWorkNoLeak
      childSaid child "filteredBefore" |> Expect.equal "no thread carried a filter before" "0"
      childSaid child "outcome" |> Expect.equal "the getter got its answer from the pool" "completed:72"
      // The kernel names the thread: a pool worker made on behalf of the filtered thread inherits the filter for good.
      childSaid child "filteredAfter" |> Expect.equal (sprintf "no thread still carries the filter (%s)" (childSaid child "filteredNames")) "0"
      childSaid child "poolWrite" |> Expect.equal "a pool thread writes a file" "wrote"
      childSaid child "poolSpawn" |> Expect.equal "a pool thread starts a process" "spawned"
    }

    realFilterCase "WHY — after sandbox calls that queue pool work, no pool item fails a file cycle (create a directory, write, read back), because no worker carries the filter" <| fun () -> task {
      let! child = SandboxChild.run SandboxChild.SandboxScenario.PoolFileCycles
      childSaid child "completedCalls" |> Expect.equal "every sandbox call completed" (childSaid child "rounds")
      childSaid child "errors" |> Expect.equal (sprintf "no file cycle failed (first: %s)" (childSaid child "firstError")) "0"
      childSaid child "filteredAfter" |> Expect.equal "and no thread carries the filter" "0"
    }

    realFilterCase "WHY — a thread cannot be made on the filtered thread: starting one is refused inside the work, and the call still completes" <| fun () -> task {
      let! child = SandboxChild.run SandboxChild.SandboxScenario.ThreadCreationRefused
      childSaid child "outcome" |> Expect.equal "the call completed" "completed"
      (childSaid child "thread").StartsWith "refused:" |> Expect.isTrue (sprintf "starting a thread was refused (%s)" (childSaid child "thread"))
      childSaid child "filteredAfter" |> Expect.equal "and no thread carries the filter afterwards" "0"
    }

    realFilterCase "WHY — the twin: with the filter as it was before thread creation was refused, the same getter leaves the pool workers it made under the filter" <| fun () -> task {
      let! child = SandboxChild.run SandboxChild.SandboxScenario.PoolWorkNoLeakBefore
      childSaid child "outcome" |> Expect.equal "the old call completed too" "completed:72"
      (int (childSaid child "filteredAfter") > 0)
      |> Expect.isTrue (sprintf "the kernel names a thread still under the filter (%s)" (childSaid child "filteredNames"))
    }

    realFilterCase "WHY — the pool is held at the threads it has while a call runs, so it never tries to make a thread on the filtered one even when every worker is busy, and its limits are back after" <| fun () -> task {
      let! child = SandboxChild.run SandboxChild.SandboxScenario.PoolBusy
      childSaid child "outcome" |> Expect.equal "the getter got its answer once the workers were let go" "completed:1"
      childSaid child "minDuring" |> Expect.equal "the minimum was lowered to one during the call" "1"
      (int (childSaid child "maxDuring") >= 1) |> Expect.isTrue "the maximum was held at the threads that existed"
      childSaid child "maxDuring" |> Expect.notEqual "under what the host had set" (childSaid child "maxBefore")
      childSaid child "minAfter" |> Expect.equal "the minimum is back" (childSaid child "minBefore")
      childSaid child "maxAfter" |> Expect.equal "the maximum is back" (childSaid child "maxBefore")
      childSaid child "filteredAfter" |> Expect.equal "no thread carries the filter" "0"
      childSaid child "servedAfter" |> Expect.equal "and the pool serves afterwards" "72"
    }

    realFilterCase "WHY — the twin: with the thread refused but the pool's limits not held, a busy pool tries to make a thread on the filtered one and the getter gets the exception" <| fun () -> task {
      let! child = SandboxChild.run SandboxChild.SandboxScenario.PoolBusyBefore
      (childSaid child "outcome").StartsWith "threw:"
      |> Expect.isTrue (sprintf "the pool's request for a thread was refused into the getter (%s)" (childSaid child "outcome"))
    }

    realFilterCase "WHY — a hold whose call never ends is let go by its ceiling, and the call's late leave changes nothing" <| fun () -> task {
      let! child = SandboxChild.run SandboxChild.SandboxScenario.PoolHoldExpires
      childSaid child "outcome" |> Expect.equal "the hold expired" "expired"
      childSaid child "maxHeld" |> Expect.notEqual "the limits were lowered while held" (childSaid child "maxBefore")
      childSaid child "minExpired" |> Expect.equal "expiry put the minimum back" (childSaid child "minBefore")
      childSaid child "maxExpired" |> Expect.equal "expiry put the maximum back" (childSaid child "maxBefore")
      childSaid child "minLate" |> Expect.equal "the late leave left the minimum" (childSaid child "minBefore")
      childSaid child "maxLate" |> Expect.equal "the late leave left the maximum" (childSaid child "maxBefore")
      childSaid child "moves" |> Expect.equal "one pin then one restore" "pin,restore"
    }

    realFilterCase "WHY — a getter that queues far more pool work than there are workers leaves nothing under the filter, and the pool still serves and still grows" <| fun () -> task {
      let! child = SandboxChild.run SandboxChild.SandboxScenario.PoolExhausted
      childSaid child "outcome" |> Expect.equal "the getter got every answer" "completed:300"
      childSaid child "filteredAfter" |> Expect.equal (sprintf "no thread carries the filter (%s)" (childSaid child "filteredNames")) "0"
      childSaid child "servedAfter" |> Expect.equal "the pool serves after it" "72"
      childSaid child "grewWhenBlocked" |> Expect.equal "the pool adds a worker when every one is blocked" "ran"
    }

    realFilterCase "WHY — creating a file is refused under NoNetworkNoWritesNoSpawn while reading one still works" <| fun () -> task {
      do!
        withTempDir (fun dir ->
          task {
            let readable = Path.Combine(dir, "readable.txt")
            File.WriteAllText(readable, "hello")
            let blocked = Path.Combine(dir, "blocked.txt")
            let! outcome =
              runSandboxed NoNetworkNoWritesNoSpawn (fun () ->
                let read = File.ReadAllText readable
                let listing = Directory.GetFiles dir |> Array.length
                try
                  File.WriteAllText(blocked, "nope")
                  read, listing, "wrote"
                with :? UnauthorizedAccessException -> read, listing, "denied")
            match outcome with
            | Completed (read, listing, write) ->
              read |> Expect.equal "read works" "hello"
              listing |> Expect.equal "listing works" 1
              write |> Expect.equal "the write was refused" "denied"
            | other -> failtestf "expected Completed, got %A" other
            File.Exists blocked |> Expect.isFalse "the file was never created"
          })
    }

    realFilterCase "WHY — mkdir, delete, rename and truncate are refused too" <| fun () -> task {
      do!
        withTempDir (fun dir ->
          task {
            let victim = Path.Combine(dir, "victim.txt")
            File.WriteAllText(victim, "keep me")
            let attempt (what: string) (act: unit -> unit) =
              try act (); what + ": allowed" with :? UnauthorizedAccessException -> what + ": denied"
            let! outcome =
              runSandboxed NoNetworkNoWritesNoSpawn (fun () ->
                [ attempt "mkdir" (fun () -> Directory.CreateDirectory(Path.Combine(dir, "sub")) |> ignore)
                  attempt "delete" (fun () -> File.Delete victim)
                  attempt "rename" (fun () -> File.Move(victim, Path.Combine(dir, "moved.txt")))
                  attempt "truncate" (fun () -> File.WriteAllBytes(victim, [||])) ])
            match outcome with
            | Completed results ->
              results |> Expect.equal "each one refused" [ "mkdir: denied"; "delete: denied"; "rename: denied"; "truncate: denied" ]
            | other -> failtestf "expected Completed, got %A" other
            File.ReadAllText victim |> Expect.equal "the file is untouched" "keep me"
            Directory.Exists(Path.Combine(dir, "sub")) |> Expect.isFalse "no directory appeared"
          })
    }

    realFilterCase "WHY — NoNetwork alone still lets the work write a file, so the two policies behave as different tiers" <| fun () -> task {
      do!
        withTempDir (fun dir ->
          task {
            let target = Path.Combine(dir, "allowed.txt")
            let! outcome = runSandboxed NoNetwork (fun () -> File.WriteAllText(target, "ok"))
            outcome |> Expect.equal "completed" (Completed ())
            File.ReadAllText target |> Expect.equal "the file was written" "ok"
          })
    }

    realFilterCase "WHY — starting a process is refused on the filtered thread and works on a normal one" <| fun () -> task {
      let start () =
        let info = Diagnostics.ProcessStartInfo(Environment.ProcessPath, "--version", RedirectStandardOutput = true)
        SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply info
        use p = Diagnostics.Process.Start info
        p.WaitForExit()
      let! outcome = runSandboxed NoNetworkNoWritesNoSpawn start
      match outcome with
      | Threw _ -> ()
      | other -> failtestf "expected the spawn to throw, got %A" other
      let! control =
        (TestInfrastructure.DedicatedThread.run "sagefs-spawn-control" start).WaitAsync(TestTimeouts.patience)
        |> Async.AwaitTask
        |> Async.Catch
      match control with
      | Choice1Of2 () -> ()
      | Choice2Of2 ex -> failtestf "the same spawn failed on an ordinary thread: %O" ex
    }

    realFilterCase "WHY — an unhandled exception inside the work comes back as Threw and the process carries on" <| fun () -> task {
      let! outcome = runSandboxed NoNetwork (fun () -> raise (InvalidOperationException "getter boom") : int)
      match outcome with
      | Threw (:? InvalidOperationException as ex) -> ex.Message |> Expect.equal "the message survives" "getter boom"
      | other -> failtestf "expected Threw, got %A" other
      let! again = runSandboxed NoNetwork (fun () -> 7)
      again |> Expect.equal "a second run after the failure still works" (Completed 7)
    }

    realFilterCase "WHY — the filter does not leak to the calling thread, a pool thread or a later run's thread" <| fun () -> task {
      do!
        withLoopbackListener (fun port ->
          task {
            let! _ = runSandboxed NoNetworkNoWritesNoSpawn (fun () -> 1)
            // A pool thread, and then the very thread this test body is running on: neither has the filter.
            do! connectFromPoolThread port
            connectHere port
            do!
              withTempDir (fun dir ->
                task { File.WriteAllText(Path.Combine(dir, "after.txt"), "still allowed on this thread") })
          })
    }
  ]
