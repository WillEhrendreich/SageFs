module SageFs.Tests.ThreadSandboxTests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Text.RegularExpressions
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
      match writesAndSpawn with
      | false -> ExpectAllow
      | true ->
        match d.Nr with
        | nr when List.contains nr deniedOutrightNrs -> ExpectErrno eperm
        | nr when nr = nrOpen ->
          (match low32 d.Args[1] &&& writeFlagsMask with 0UL -> ExpectAllow | _ -> ExpectErrno eperm)
        | nr when nr = nrOpenat ->
          (match low32 d.Args[2] &&& writeFlagsMask with 0UL -> ExpectAllow | _ -> ExpectErrno eperm)
        | nr when nr = nrClone ->
          (match low32 d.Args[0] &&& cloneThread with 0UL -> ExpectErrno eperm | _ -> ExpectAllow)
        | nr when nr = nrClone3 -> ExpectErrno enosys
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
      1, Gen.map (fun v -> (v &&& ~~~writeFlagsMask) ||| oRdwr) genRandom64 ]

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

// ---- the platform guard for the tests that install a real filter ----

let private isLinuxX64 =
  OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture = Architecture.X64

let private requireLinuxX64 () =
  match isLinuxX64 with
  | true -> ()
  | false -> skiptest "the seccomp filter is verified on Linux x86-64 only"

/// Runs the sandbox on a pool thread with the suite's ceiling, so a stuck thread fails the test instead of the run.
let private runSandboxed (policy: SandboxPolicy) (work: unit -> 'T) : Task<SandboxOutcome<'T>> =
  Task.Run(fun () -> ThreadSandbox.run policy work).WaitAsync(TestTimeouts.patienceInProcess)

let private startLoopbackListener () =
  let listener = new TcpListener(IPAddress.Loopback, 0)
  listener.Start()
  listener, (listener.LocalEndpoint :?> IPEndPoint).Port

/// Connects from a pool thread. The kernel completes the handshake from the listen backlog, so no accept is needed.
let private connectFromPoolThread (port: int) : Task =
  task {
    use client = new TcpClient()
    do! client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TestTimeouts.patienceInProcess)
  }

let private withTempDir (body: string -> Task) : Task =
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

    testProperty "WHY — NoNetwork decides every x86-64 syscall the way the model says, so the BPF and the policy cannot drift"
    <| Prop.forAll (Arb.fromGen genData) (fun d ->
      runBpf (Seccomp.denyProgram X86_64 NoNetwork) d = verdictOf (model NoNetwork d))

    testProperty "WHY — NoNetworkNoWritesNoSpawn decides every x86-64 syscall the way the model says, flags included"
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

    testCase "WHY — clone is allowed with CLONE_THREAD and refused without it, so the runtime can make threads but not processes" <| fun _ ->
      verdictIs NoNetworkNoWritesNoSpawn nrClone [ cloneThread ] |> Expect.equal "clone with CLONE_THREAD" VAllow
      verdictIs NoNetworkNoWritesNoSpawn nrClone [ 0UL ] |> Expect.equal "clone without CLONE_THREAD" (VErrno eperm)

    testCase "WHY — clone3 answers ENOSYS, because its flags are in a struct the filter cannot read and glibc then falls back to clone" <| fun _ ->
      verdictIs NoNetworkNoWritesNoSpawn nrClone3 [ 0UL ] |> Expect.equal "clone3" (VErrno enosys)

    testCase "WHY — NoNetwork leaves file writes and process creation alone, so the two policies are different tiers" <| fun _ ->
      verdictIs NoNetwork nrOpenat [ 0UL; 0UL; oCreat ||| oWronly ] |> Expect.equal "openat O_CREAT under NoNetwork" VAllow
      verdictIs NoNetwork nrClone [ 0UL ] |> Expect.equal "clone without CLONE_THREAD under NoNetwork" VAllow
      verdictIs NoNetwork nrSocket [ 2UL; 1UL; 0UL ] |> Expect.equal "socket under NoNetwork" (VErrno eperm)

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

    testCase "WHY — off Linux, run is Unavailable NotLinux and the work does not run" <| fun _ ->
      match OperatingSystem.IsLinux() with
      | true -> skiptest "this machine is Linux"
      | false ->
        let ran = ResizeArray<string>()
        ThreadSandbox.run NoNetwork (fun () -> ran.Add "ran"; 1) |> Expect.equal "NotLinux" (Unavailable NotLinux)
        ran |> Expect.isEmpty "the work never ran"
  ]

[<Tests>]
let realThreadTests =
  testList "ThreadSandbox.run (real seccomp filter, Linux x86-64)" [

    testTask "WHY — a connect on the filtered thread fails with Permission denied while another thread reaches the same listener before and after" {
      requireLinuxX64 ()
      let listener, port = startLoopbackListener ()
      use _listener = listener
      do! connectFromPoolThread port
      let! outcome =
        runSandboxed NoNetwork (fun () ->
          use client = new TcpClient()
          client.Connect(IPAddress.Loopback, port))
      match outcome with
      | Threw (:? SocketException as se) -> se.SocketErrorCode |> Expect.equal "EPERM is AccessDenied" SocketError.AccessDenied
      | other -> failtestf "expected a SocketException, got %A" other
      do! connectFromPoolThread port
    }

    testTask "WHY — the filtered thread still allocates, collects garbage, makes threads and JITs fresh code, so the runtime keeps working under the filter" {
      requireLinuxX64 ()
      let! outcome =
        runSandboxed NoNetworkNoWritesNoSpawn (fun () ->
          let sum = Seq.sum (seq { for i in 1 .. 10_000 -> i })
          GC.Collect()
          GC.WaitForPendingFinalizers()
          let inner = ref 0
          let t = Thread((fun () -> inner.Value <- sum), IsBackground = true)
          t.Start()
          t.Join()
          let compiled = Regex("a+b", RegexOptions.Compiled).IsMatch "caaab"
          sum, inner.Value, compiled)
      match outcome with
      | Completed (sum, fromThread, compiled) ->
        sum |> Expect.equal "ordinary work" 50_005_000
        fromThread |> Expect.equal "a thread started from the filtered one ran" 50_005_000
        compiled |> Expect.isTrue "a compiled regex JITs under the filter"
      | other -> failtestf "expected Completed, got %A" other
    }

    testTask "WHY — creating a file is refused under NoNetworkNoWritesNoSpawn while reading one still works" {
      requireLinuxX64 ()
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

    testTask "WHY — mkdir, delete, rename and truncate are refused too" {
      requireLinuxX64 ()
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

    testTask "WHY — NoNetwork alone still lets the work write a file, so the two policies behave as different tiers" {
      requireLinuxX64 ()
      do!
        withTempDir (fun dir ->
          task {
            let target = Path.Combine(dir, "allowed.txt")
            let! outcome = runSandboxed NoNetwork (fun () -> File.WriteAllText(target, "ok"))
            outcome |> Expect.equal "completed" (Completed ())
            File.ReadAllText target |> Expect.equal "the file was written" "ok"
          })
    }

    testTask "WHY — starting a process is refused on the filtered thread and works on a normal one" {
      requireLinuxX64 ()
      let start () =
        let info = Diagnostics.ProcessStartInfo(Environment.ProcessPath, "--version", RedirectStandardOutput = true)
        use p = Diagnostics.Process.Start info
        p.WaitForExit()
      let! outcome = runSandboxed NoNetworkNoWritesNoSpawn start
      match outcome with
      | Threw _ -> ()
      | other -> failtestf "expected the spawn to throw, got %A" other
      let! control = Task.Run(start).WaitAsync(TestTimeouts.patience) |> Async.AwaitTask |> Async.Catch
      match control with
      | Choice1Of2 () -> ()
      | Choice2Of2 ex -> failtestf "the same spawn failed on an ordinary thread: %O" ex
    }

    testTask "WHY — an unhandled exception inside the work comes back as Threw and the process carries on" {
      requireLinuxX64 ()
      let! outcome = runSandboxed NoNetwork (fun () -> raise (InvalidOperationException "getter boom") : int)
      match outcome with
      | Threw (:? InvalidOperationException as ex) -> ex.Message |> Expect.equal "the message survives" "getter boom"
      | other -> failtestf "expected Threw, got %A" other
      let! again = runSandboxed NoNetwork (fun () -> 7)
      again |> Expect.equal "a second run after the failure still works" (Completed 7)
    }

    testTask "WHY — the filter does not leak to the calling thread, a pool thread or a later run's thread" {
      requireLinuxX64 ()
      let listener, port = startLoopbackListener ()
      use _listener = listener
      let! _ = runSandboxed NoNetworkNoWritesNoSpawn (fun () -> 1)
      // The calling thread of the test body, a pool thread, and a second sandbox run that does not need the network.
      use client = new TcpClient()
      do! client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TestTimeouts.patienceInProcess)
      do! connectFromPoolThread port
      do!
        withTempDir (fun dir ->
          task { File.WriteAllText(Path.Combine(dir, "after.txt"), "still allowed on this thread") })
    }
  ]
