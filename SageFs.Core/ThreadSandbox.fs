namespace SageFs

// Dependency-free apart from System, on purpose. This file is compiled into SageFs.Core and also embedded into
// the isolated FSI host (see the LiveValueTree entries in SageFs.Core.fsproj and FsiHost.fsproj), so it must not
// open anything of ours.

open System
open System.Runtime.InteropServices
open System.Threading

/// The CPU architectures a filter program can be written for. Aarch64 is in the type so a caller can name it,
/// but it is reported unavailable: its syscall numbers differ from x86-64 and nobody has run them on an arm64
/// machine here. A platform that cannot be verified is absent, never assumed.
type SandboxArchitecture =
  | X86_64
  | Aarch64

/// What a sandboxed thread is stopped from doing. The filter is a deny list, so everything not named here
/// (reads, mmap, futex, madvise) keeps working.
///
/// EVERY policy stops the sandboxed thread from making a thread (clone with CLONE_THREAD fails with EPERM, clone3 with ENOSYS).
/// A filter is inherited by every thread its owner creates, and the runtime makes pool workers, the pool's gate thread and its
/// timer and tiering threads on behalf of whichever thread asks, so a thread made under the filter would keep it for the life of
/// the process. Work that needs another thread gets the refusal (an OutOfMemoryException from Thread.Start), and a pool item it
/// queues is served by a worker that already exists (`SandboxPool.prepare` makes sure of one before the call).
type SandboxPolicy =
  /// socket, connect, accept, bind, listen, send* and socketpair fail with EPERM.
  | NoNetwork
  /// NoNetwork, plus: opening a file for write, create, truncate or append; unlink, rename, mkdir, rmdir,
  /// truncate, chmod, chown, symlink and link; fork, vfork, execve, execveat and every clone. All of these fail with
  /// EPERM (clone3 fails with ENOSYS, see Seccomp.denyProgram).
  | NoNetworkNoWritesNoSpawn

/// Why no filter was installed. The caller shows this, so "no I/O containment here" always comes with a reason.
type SandboxUnavailable =
  | NotLinux
  | UnverifiedArchitecture of string
  | InstallFailed of step: string * errno: int
  | LibraryNotFound of string
  /// The thread pool could not be shown to have a worker before the call. Without one, a getter that queues pool work would make
  /// the pool create a worker on the filtered thread, and that worker would keep the filter for the life of the process.
  | PoolNotReady of reason: string

/// What a sandboxed run came to.
type SandboxOutcome<'T> =
  /// The filter was installed and the work returned.
  | Completed of 'T
  /// The filter was installed and the work threw. A denied call shows up here as the exception .NET maps the
  /// errno to (SocketException for a socket, UnauthorizedAccessException for a file, Win32Exception for a spawn).
  | Threw of exn
  /// The filter could not be installed, so the work did NOT run.
  | Unavailable of SandboxUnavailable

module SandboxUnavailable =
  /// One line a person can read, for the pane that says there is no I/O containment.
  let describe (reason: SandboxUnavailable) : string =
    match reason with
    | NotLinux -> "this OS has no per-thread syscall filter, so a getter that runs here is not stopped from using the network, files or processes"
    | UnverifiedArchitecture arch -> sprintf "the syscall filter is only verified on x86-64, and this process is %s" arch
    | InstallFailed (step, errno) -> sprintf "the syscall filter could not be installed (%s failed, errno %d)" step errno
    | LibraryNotFound name -> sprintf "the syscall filter could not be installed because %s could not be loaded" name
    | PoolNotReady why -> sprintf "the getter was not run: the thread pool has no worker to serve work it queues, and the filter would be left on a thread the pool made for it (%s)" why

/// Classic-BPF programs for seccomp, built as pure bytes so every OS can test them.
module Seccomp =

  // ---- where each number comes from ----
  // Syscall numbers: Linux x86-64 table, arch/x86/entry/syscalls/syscall_64.tbl.
  // Return values and the arch tag: include/uapi/linux/seccomp.h and include/uapi/linux/audit.h.
  // Open and clone flag bits: include/uapi/asm-generic/fcntl.h (x86-64 uses the generic values) and
  // include/uapi/linux/sched.h.
  // BPF opcodes: include/uapi/linux/bpf_common.h (BPF_LD|BPF_W|BPF_ABS and so on).
  // seccomp_data layout: struct seccomp_data in seccomp.h (nr at 0, arch at 4, instruction_pointer at 8,
  // args[6] at 16, each arg 8 bytes).

  /// AUDIT_ARCH_X86_64 = EM_X86_64 (62) | __AUDIT_ARCH_64BIT | __AUDIT_ARCH_LE.
  let private auditArchX86_64 = 0xC000003Eu

  /// SECCOMP_RET_ALLOW.
  let private retAllow = 0x7FFF0000u
  /// SECCOMP_RET_ERRNO, with the errno in the low 16 bits. KILL and TRAP are never used.
  let private retErrno = 0x00050000u
  /// errno 1.
  let private eperm = 1u
  /// errno 38. Returned for clone3 so glibc falls back to clone, which the filter can inspect.
  let private enosys = 38u

  let private bpfLdWAbs = 0x20us
  let private bpfJeqK = 0x15us
  let private bpfJgeK = 0x35us
  let private bpfJsetK = 0x45us
  let private bpfRetK = 0x06us

  let private offsetNr = 0u
  let private offsetArch = 4u
  let private offsetArgs = 16u
  let private bytesPerArg = 8u

  /// The x32 ABI marks its syscalls with this bit in the number. An x86-64 process can issue them, so a deny
  /// list on plain numbers would be bypassed unless the whole range is refused.
  let private x32SyscallBit = 0x40000000u

  /// A sock_filter is 8 bytes: u16 code, u8 jt, u8 jf, u32 k. A sock_fprog's filter pointer is 8 bytes.
  let private bytesPerInstruction = 8

  /// Largest value jt and jf can hold: they are one byte each.
  let private maxJump = 255

  // socket, connect, accept, sendto, sendmsg, bind, listen, socketpair, accept4, sendmmsg.
  let private networkSyscalls = [ 41u; 42u; 43u; 44u; 46u; 49u; 50u; 53u; 288u; 307u ]

  // truncate 76, ftruncate 77, rename 82, mkdir 83, rmdir 84, creat 85, link 86, unlink 87, symlink 88,
  // chmod 90, fchmod 91, chown 92, fchown 93, lchown 94, mknod 133, mkdirat 258, mknodat 259, fchownat 260,
  // unlinkat 263, renameat 264, linkat 265, symlinkat 266, fchmodat 268, renameat2 316,
  // openat2 437 (its flags sit in a struct the filter cannot read, so it is refused whole), fchmodat2 452.
  let private fileMutationSyscalls =
    [ 76u; 77u; 82u; 83u; 84u; 85u; 86u; 87u; 88u; 90u; 91u; 92u; 93u; 94u; 133u
      258u; 259u; 260u; 263u; 264u; 265u; 266u; 268u; 316u; 437u; 452u ]

  // fork 57, vfork 58, execve 59, execveat 322.
  let private spawnSyscalls = [ 57u; 58u; 59u; 322u ]

  let private sysOpen = 2u
  let private sysOpenat = 257u
  let private sysClone = 56u
  let private sysClone3 = 435u

  /// open(path, flags, mode): flags is argument 1. openat(dirfd, path, flags, mode): flags is argument 2.
  let private openFlagsArg = 1u
  let private openatFlagsArg = 2u
  /// clone(flags, ...): flags is argument 0.
  let private cloneFlagsArg = 0u

  // O_WRONLY 0x1, O_RDWR 0x2, O_CREAT 0x40, O_TRUNC 0x200, O_APPEND 0x400. O_TMPFILE needs O_RDWR or
  // O_WRONLY, so it is caught by the same mask.
  let private openWriteMask = 0x1u ||| 0x2u ||| 0x40u ||| 0x200u ||| 0x400u

  /// CLONE_THREAD. A clone with it makes a thread (the runtime needs that); without it, a process.
  let private cloneThread = 0x10000u

  /// Where a jump goes. Offsets are worked out when the program is assembled, so no number is counted by hand.
  type private Dest =
    | Next
    | Skip of int
    | ToAllow
    | ToEperm
    | ToEnosys

  type private Insn =
    | Load of offset: uint32
    | JumpEq of k: uint32 * ifTrue: Dest * ifFalse: Dest
    | JumpGe of k: uint32 * ifTrue: Dest * ifFalse: Dest
    | JumpSet of k: uint32 * ifTrue: Dest * ifFalse: Dest

  let private argLow (index: uint32) = offsetArgs + index * bytesPerArg

  let private writeU16 (buffer: byte[]) (at: int) (v: uint16) =
    buffer[at] <- byte (v &&& 0xFFus)
    buffer[at + 1] <- byte (v >>> 8)

  let private writeU32 (buffer: byte[]) (at: int) (v: uint32) =
    buffer[at] <- byte (v &&& 0xFFu)
    buffer[at + 1] <- byte ((v >>> 8) &&& 0xFFu)
    buffer[at + 2] <- byte ((v >>> 16) &&& 0xFFu)
    buffer[at + 3] <- byte (v >>> 24)

  /// Lays the instructions out, appends the three verdict instructions (ALLOW, ERRNO EPERM, ERRNO ENOSYS), and
  /// writes the bytes. Running off the end of the instructions lands on ALLOW.
  let private assemble (body: Insn list) : byte[] =
    let insns = Array.ofList body
    let allowAt = insns.Length
    let epermAt = allowAt + 1
    let enosysAt = allowAt + 2
    let total = insns.Length + 3
    let buffer = Array.zeroCreate<byte> (total * bytesPerInstruction)
    let put (index: int) (code: uint16) (jt: int) (jf: int) (k: uint32) =
      let at = index * bytesPerInstruction
      writeU16 buffer at code
      buffer[at + 2] <- byte jt
      buffer[at + 3] <- byte jf
      writeU32 buffer (at + 4) k
    let offsetOf (index: int) (dest: Dest) =
      let off =
        match dest with
        | Next -> 0
        | Skip n -> n
        | ToAllow -> allowAt - (index + 1)
        | ToEperm -> epermAt - (index + 1)
        | ToEnosys -> enosysAt - (index + 1)
      match off >= 0 && off <= maxJump with
      | true -> off
      | false -> invalidArg "body" (sprintf "jump at instruction %d is %d, outside 0..%d" index off maxJump)
    insns
    |> Array.iteri (fun i insn ->
      match insn with
      | Load offset -> put i bpfLdWAbs 0 0 offset
      | JumpEq (k, t, f) -> put i bpfJeqK (offsetOf i t) (offsetOf i f) k
      | JumpGe (k, t, f) -> put i bpfJgeK (offsetOf i t) (offsetOf i f) k
      | JumpSet (k, t, f) -> put i bpfJsetK (offsetOf i t) (offsetOf i f) k)
    put allowAt bpfRetK 0 0 retAllow
    put epermAt bpfRetK 0 0 (retErrno ||| eperm)
    put enosysAt bpfRetK 0 0 (retErrno ||| enosys)
    buffer

  /// Denies each listed syscall with EPERM and falls through for any other.
  let private denyEach (numbers: uint32 list) =
    numbers |> List.map (fun nr -> JumpEq (nr, ToEperm, Next))

  /// A call whose decision depends on one flag argument. `onMatch` is where the call goes when the mask hits.
  let private flagCheck (nr: uint32) (argIndex: uint32) (mask: uint32) (onHit: Dest) (onMiss: Dest) =
    // The first jump skips the load and the mask test (2 instructions) when this is a different syscall.
    [ JumpEq (nr, Next, Skip 2)
      Load (argLow argIndex)
      JumpSet (mask, onHit, onMiss) ]

  /// Whether the filtered thread may make a thread. A filter is inherited by every thread its owner creates, and the runtime
  /// makes pool workers, the pool's gate thread and its timer and tiering threads on behalf of whichever thread asks, so a thread
  /// made under the filter keeps it for the life of the process and fails the host's own writes, connections and spawns later.
  /// `Refused` is the product. `Allowed` is the behaviour before 2026-11-09, kept so a test can show the leak it had.
  [<RequireQualifiedAccess>]
  type ThreadCreation =
    | Refused
    | Allowed

  let private x86_64Body (threads: ThreadCreation) (policy: SandboxPolicy) : Insn list =
    let network = denyEach networkSyscalls
    // clone3 answers ENOSYS (its flags sit in a struct the filter cannot read), so glibc falls back to clone, which it can read.
    // The flag test leaves the argument in the accumulator, so the syscall number is loaded again after it.
    let threadCreation =
      match threads with
      | ThreadCreation.Allowed -> []
      | ThreadCreation.Refused ->
        [ JumpEq (sysClone3, ToEnosys, Next) ]
        @ flagCheck sysClone cloneFlagsArg cloneThread ToEperm Next
        @ [ Load offsetNr ]
    let writesAndSpawns =
      match policy, threads with
      | NoNetwork, _ -> []
      | NoNetworkNoWritesNoSpawn, ThreadCreation.Refused ->
        denyEach fileMutationSyscalls
        @ denyEach spawnSyscalls
        // A clone without CLONE_THREAD makes a process, which this policy refuses too.
        @ denyEach [ sysClone ]
        @ flagCheck sysOpen openFlagsArg openWriteMask ToEperm ToAllow
        @ flagCheck sysOpenat openatFlagsArg openWriteMask ToEperm ToAllow
      | NoNetworkNoWritesNoSpawn, ThreadCreation.Allowed ->
        denyEach fileMutationSyscalls
        @ denyEach spawnSyscalls
        @ [ JumpEq (sysClone3, ToEnosys, Next) ]
        @ flagCheck sysOpen openFlagsArg openWriteMask ToEperm ToAllow
        @ flagCheck sysOpenat openatFlagsArg openWriteMask ToEperm ToAllow
        @ flagCheck sysClone cloneFlagsArg cloneThread ToAllow ToEperm
    // The arch test comes first: a syscall from any other ABI (32-bit compat) is ALLOWed, never killed.
    [ Load offsetArch
      JumpEq (auditArchX86_64, Next, ToAllow)
      Load offsetNr
      JumpGe (x32SyscallBit, ToEperm, Next) ]
    @ network
    @ threadCreation
    @ writesAndSpawns

  /// The classic-BPF program (sock_filter array, little endian) that denies what the policy names and allows
  /// everything else. Every path ends in a RET that is ALLOW or ERRNO. It never returns KILL or TRAP, so a
  /// denied call fails with an errno and nothing is killed. A syscall from an architecture the program was
  /// not written for is ALLOWed.
  ///
  /// Aarch64 has no deny list: its syscall numbers are not verified, `support` reports it unavailable, and the
  /// program is one ALLOW so that no unchecked list of numbers exists to be installed by mistake.
  let denyProgramFor (threads: ThreadCreation) (arch: SandboxArchitecture) (policy: SandboxPolicy) : byte[] =
    match arch with
    | X86_64 -> assemble (x86_64Body threads policy)
    | Aarch64 -> assemble []

  /// The product's program: every policy refuses thread creation.
  let denyProgram (arch: SandboxArchitecture) (policy: SandboxPolicy) : byte[] =
    denyProgramFor ThreadCreation.Refused arch policy

  /// Whether a filter for this architecture has been verified on a real machine. Only x86-64 has.
  let support (arch: SandboxArchitecture) : Result<unit, SandboxUnavailable> =
    match arch with
    | X86_64 -> Ok ()
    | Aarch64 -> Error (UnverifiedArchitecture "Aarch64")

  /// Maps the architecture the runtime reports to one a filter can be written for. Anything this does not
  /// know is unverified, never assumed.
  let architectureOf (runtime: Architecture) : Result<SandboxArchitecture, SandboxUnavailable> =
    match runtime with
    | Architecture.X64 -> Ok X86_64
    | Architecture.Arm64 -> Ok Aarch64
    | other -> Error (UnverifiedArchitecture (string other))

module private SandboxNative =
  /// glibc by its versioned name: plain "libc" does not resolve on this machine. On a libc that is not glibc
  /// (musl) this is a DllNotFoundException, which becomes LibraryNotFound.
  [<Literal>]
  let LibcName = "libc.so.6"

  [<DllImport(LibcName, SetLastError = true)>]
  extern int prctl(int option, uint64 arg2, uint64 arg3, uint64 arg4, uint64 arg5)


/// Whether the thread pool can serve work a getter queues without making a worker on the filtered thread.
[<RequireQualifiedAccess>]
type PoolReadiness =
  | Ready
  | NoWorker

/// The pool's limits, the four numbers `ThreadPool` lets a process set.
type PoolSettings =
  { MinWorkers: int
    MaxWorkers: int
    MinIo: int
    MaxIo: int }

/// Whether the pool's limits are lowered for sandbox calls. Each stretch of overlapping calls is an epoch, so a call that
/// outlived its hold cannot end a later one.
[<RequireQualifiedAccess>]
type PoolHold =
  | Free of epoch: int
  /// `holders` calls of this epoch are running, and `restore` is what the pool's limits were before the first of them.
  | Held of epoch: int * holders: int * restore: PoolSettings

/// What to do to the pool's limits when a call starts or ends.
[<RequireQualifiedAccess>]
type PoolMove =
  | Keep
  /// Lower the minimum, then the maximum (the pool refuses a maximum under its minimum).
  | Pin of PoolSettings
  /// Raise the maximum, then the minimum.
  | Restore of PoolSettings

module PoolHold =

  /// The limits that keep the pool from ever making a worker: its goal cannot exceed the maximum, and a worker is made only when
  /// every existing one is busy and the goal is above how many exist, so with a maximum of the threads that exist the pool wakes
  /// an idle worker or waits for a busy one and never starts a thread. Never above what the host had set, never under one.
  let pinnedFor (current: PoolSettings) (existing: int) : PoolSettings =
    { current with
        MinWorkers = 1
        MaxWorkers = max 1 (min existing current.MaxWorkers) }

  /// A call joins the hold, starting one when none is held. The epoch it gets is the one it must leave.
  let enter (hold: PoolHold) (current: PoolSettings) (existing: int) : PoolHold * PoolMove * int =
    match hold with
    | PoolHold.Free epoch -> PoolHold.Held (epoch + 1, 1, current), PoolMove.Pin (pinnedFor current existing), epoch + 1
    | PoolHold.Held (epoch, holders, restore) -> PoolHold.Held (epoch, holders + 1, restore), PoolMove.Keep, epoch

  /// A call leaves the epoch it entered; the last one out puts the limits back. A call of an epoch that already ended (expired)
  /// has nothing to undo, so it cannot raise the limits above what the host set, or end the next epoch's hold.
  let leave (hold: PoolHold) (epoch: int) : PoolHold * PoolMove =
    match hold with
    | PoolHold.Held (current, 1, restore) when current = epoch -> PoolHold.Free current, PoolMove.Restore restore
    | PoolHold.Held (current, holders, restore) when current = epoch -> PoolHold.Held (current, holders - 1, restore), PoolMove.Keep
    | PoolHold.Held _
    | PoolHold.Free _ -> hold, PoolMove.Keep

  /// The epoch has run past its ceiling: whatever is still in it is abandoned, and the limits go back.
  let expire (hold: PoolHold) (epoch: int) : PoolHold * PoolMove =
    match hold with
    | PoolHold.Held (current, _, restore) when current = epoch -> PoolHold.Free current, PoolMove.Restore restore
    | PoolHold.Held _
    | PoolHold.Free _ -> hold, PoolMove.Keep

/// What the sandbox needs from the thread pool before it filters a thread. The filter refuses thread creation (see
/// `Seccomp.ThreadCreation`), and the pool is not ready for that on its own: it makes a worker inside `QueueUserWorkItem`, on
/// the calling thread, whenever every existing worker is busy, and it counts the worker (as existing and as processing work)
/// BEFORE it makes it and never takes the count back when the system refuses. A refusal therefore leaves the pool believing in
/// a thread that does not exist, and after enough of them it makes no more workers at all. The gate thread is worse: failing to
/// make it ends the process. So the filtered thread must never be the one that asks the pool for a thread, which takes two
/// things. The pool has a worker and a gate thread before the filter goes on (`prepare`, on the caller's own unfiltered thread),
/// and for the length of the call its limits are held at the threads it has, so it never decides to make another
/// (`PoolHold`, applied under a lock and counted across calls that overlap).
module SandboxPool =

  /// Ready when the pool has at least one worker thread, whatever it is doing: with none there would be nothing to serve what a
  /// getter queues, and the pool would have to make one.
  let readinessOf (workerThreads: int) : PoolReadiness =
    match workerThreads >= 1 with
    | true -> PoolReadiness.Ready
    | false -> PoolReadiness.NoWorker

  let private current () : PoolSettings =
    let minWorkers, minIo = ThreadPool.GetMinThreads()
    let maxWorkers, maxIo = ThreadPool.GetMaxThreads()
    { MinWorkers = minWorkers; MaxWorkers = maxWorkers; MinIo = minIo; MaxIo = maxIo }

  let private refused (what: string) : Result<unit, SandboxUnavailable> =
    Error (PoolNotReady (sprintf "the pool did not accept %s" what))

  let private apply (move: PoolMove) : Result<unit, SandboxUnavailable> =
    match move with
    | PoolMove.Keep -> Ok ()
    | PoolMove.Pin settings ->
      match ThreadPool.SetMinThreads(settings.MinWorkers, settings.MinIo) with
      | false -> refused "a lower minimum"
      | true ->
        match ThreadPool.SetMaxThreads(settings.MaxWorkers, settings.MaxIo) with
        | true -> Ok ()
        | false -> refused "a lower maximum"
    | PoolMove.Restore settings ->
      match ThreadPool.SetMaxThreads(settings.MaxWorkers, settings.MaxIo) with
      | false -> refused "its maximum back"
      | true ->
        match ThreadPool.SetMinThreads(settings.MinWorkers, settings.MinIo) with
        | true -> Ok ()
        | false -> refused "its minimum back"

  /// Holds the pool's limits for the calls that are running. One lock covers reading the limits, deciding and applying, so two
  /// calls that overlap cannot each save the other's lowered limits as the ones to restore. A hold that outlives `ceiling` is
  /// ended by a timer, so a getter that never returns cannot keep the host's pool from growing. `applied` is told each move
  /// after it was made (the product passes `ignore`; a test waits on it instead of polling).
  type Guard(ceiling: TimeSpan, applied: PoolMove -> unit) =
    let sync = obj ()
    let mutable hold = PoolHold.Free 0
    let mutable expiry : Timer option = None

    let stopExpiry () =
      match expiry with
      | Some timer ->
        timer.Dispose()
        expiry <- None
      | None -> ()

    let expire (epoch: int) =
      lock sync (fun () ->
        let next, move = PoolHold.expire hold epoch
        hold <- next
        stopExpiry ()
        apply move |> ignore
        applied move)

    /// Lowers the limits for this call and returns the epoch it must leave, or says why the pool would not let them be lowered
    /// (the call is then not run).
    member _.Enter() : Result<int, SandboxUnavailable> =
      lock sync (fun () ->
        let before = current ()
        let next, move, epoch = PoolHold.enter hold before ThreadPool.ThreadCount
        match apply move with
        | Ok () ->
          hold <- next
          applied move
          (match move with
           | PoolMove.Pin _ -> expiry <- Some (new Timer(TimerCallback(fun _ -> expire epoch), null, ceiling, Timeout.InfiniteTimeSpan))
           | PoolMove.Keep
           | PoolMove.Restore _ -> ())
          Ok epoch
        | Error reason ->
          // The pool took the lower minimum and then refused the lower maximum: put back what was taken.
          apply (PoolMove.Restore before) |> ignore
          Error reason)

    /// Ends this call's part in its epoch; the last one out puts the limits back as they were before the first one in.
    member _.Leave(epoch: int) : unit =
      lock sync (fun () ->
        let next, move = PoolHold.leave hold epoch
        hold <- next
        (match hold with
         | PoolHold.Free _ -> stopExpiry ()
         | PoolHold.Held _ -> ())
        apply move |> ignore
        applied move)

  /// The one guard of this process: the pool's limits are the process's.
  let heldDuringCalls = Guard(Timeouts.sandboxPoolHoldCeiling, ignore)

  /// Makes sure the pool has a worker and its helper threads, from the calling thread, the way a getter's own queued work would.
  /// The caller is not filtered, so what the pool makes here is not filtered either. Does not wait for the pool: the worker
  /// exists when the call returns, because the pool makes it synchronously. The timer thread is made here too, because a getter
  /// that sets a timer (a delay, a timeout) would otherwise ask for it.
  let prepare () : Result<unit, SandboxUnavailable> =
    try
      for _ in 1 .. 2 do
        ThreadPool.UnsafeQueueUserWorkItem(WaitCallback(fun _ -> ()), null) |> ignore
      use timer = new Timer(TimerCallback(fun _ -> ()), null, 1, Timeout.Infinite)
      match readinessOf ThreadPool.ThreadCount with
      | PoolReadiness.Ready -> Ok ()
      | PoolReadiness.NoWorker -> Error (PoolNotReady "no worker thread exists after the pool was asked for one")
    with
    // What the pool throws when the operating system will not give it a thread.
    | :? OutOfMemoryException
    | :? ThreadStartException as e -> Error (PoolNotReady (sprintf "%s: %s" (e.GetType().Name) e.Message))

/// Runs one piece of work on a dedicated thread under a seccomp filter, so evaluating a user's property getter
/// cannot use the network, change files or start processes.
///
/// What this stops: the syscalls the policy names, on that one thread, with an errno.
///
/// What this does NOT stop, and a caller must say so wherever it shows a result:
///  - a spin or an infinite loop (the thread burns a core until the caller gives up on it);
///  - a stack overflow (that kills the process, filter or not);
///  - in-memory effects (a getter that bumps a counter or fills a cache still does);
///  - writes through a file descriptor that was already open (stdout, a log file, an existing socket);
///  - a getter that deliberately issues 32-bit compat syscalls (int 0x80), because a syscall from an
///    architecture the program was not written for is allowed rather than killed.
///
/// The filtered thread cannot make a thread (the filter refuses it), so no thread but the sandbox's own ever carries the filter,
/// and work cannot escape it by starting a thread: `Thread.Start` inside the work throws. A pool thread that exists already
/// does not have the filter, so a task queued from the work and run there is not contained. The pool is held at the threads it
/// has while the call runs, so it cannot grow to serve a getter's work, and a getter that needs more parallelism than the pool
/// has simply gets less of it.
///
/// There is no deadline here: the CALLER owns the deadline. If the work never returns, `run` never returns, so
/// call it from a thread you can abandon and stop waiting on your own timer. An abandoned thread keeps running
/// and keeps its filter until it ends; the filter dies with the thread and never touches any other thread. The pool's limits
/// are let go after `Timeouts.sandboxPoolHoldCeiling` whether or not the call has ended.
module ThreadSandbox =

  let private prSetNoNewPrivs = 38 // PR_SET_NO_NEW_PRIVS, include/uapi/linux/prctl.h
  let private prSetSeccomp = 22 // PR_SET_SECCOMP, include/uapi/linux/prctl.h
  let private seccompModeFilter = 2UL // SECCOMP_MODE_FILTER, include/uapi/linux/seccomp.h

  /// A sock_fprog is a u16 length, 6 bytes of padding, and an 8-byte pointer: 16 bytes.
  let private sockFprogSize = 16
  let private sockFprogPointerAt = 8
  let private instructionBytes = 8

  /// The two steps of an install, so a failure names which one and the names live in one place.
  type private InstallStep =
    | SetNoNewPrivs
    | SetSeccompFilter

  let private stepName (step: InstallStep) =
    match step with
    | SetNoNewPrivs -> "prctl(PR_SET_NO_NEW_PRIVS)"
    | SetSeccompFilter -> "prctl(PR_SET_SECCOMP)"

  /// Installs the program on the CALLING thread. The filter cannot be removed afterwards, which is why the
  /// caller is always a thread that exists only for this.
  let private installOnThisThread (program: byte[]) : Result<unit, SandboxUnavailable> =
    try
      match SandboxNative.prctl (prSetNoNewPrivs, 1UL, 0UL, 0UL, 0UL) with
      | 0 ->
        let filter = Marshal.AllocHGlobal program.Length
        let fprog = Marshal.AllocHGlobal sockFprogSize
        try
          Marshal.Copy(program, 0, filter, program.Length)
          Marshal.WriteInt64(fprog, 0, 0L)
          Marshal.WriteInt64(fprog, sockFprogPointerAt, 0L)
          Marshal.WriteInt16(fprog, 0, int16 (program.Length / instructionBytes))
          Marshal.WriteIntPtr(fprog, sockFprogPointerAt, filter)
          match SandboxNative.prctl (prSetSeccomp, seccompModeFilter, uint64 (fprog.ToInt64()), 0UL, 0UL) with
          | 0 -> Ok ()
          | _ -> Error (InstallFailed (stepName SetSeccompFilter, Marshal.GetLastPInvokeError()))
        finally
          Marshal.FreeHGlobal fprog
          Marshal.FreeHGlobal filter
      | _ -> Error (InstallFailed (stepName SetNoNewPrivs, Marshal.GetLastPInvokeError()))
    with
    | :? DllNotFoundException
    | :? EntryPointNotFoundException -> Error (LibraryNotFound SandboxNative.LibcName)

  /// Whether this process can be given a filter at all, without installing one. The pane uses it to say, before
  /// anything runs, that there is no I/O containment here and why.
  let availability () : Result<SandboxArchitecture, SandboxUnavailable> =
    match OperatingSystem.IsLinux() with
    | false -> Error NotLinux
    | true ->
      Seccomp.architectureOf RuntimeInformation.ProcessArchitecture
      |> Result.bind (fun arch -> Seccomp.support arch |> Result.map (fun () -> arch))

  /// `run` with the install step supplied, so the fail-closed path can be tested on every OS and the install can
  /// be faked. The work runs only after `install` returns Ok, on the same thread, once.
  let runWithInstaller (install: unit -> Result<unit, SandboxUnavailable>) (work: unit -> 'T) : SandboxOutcome<'T> =
    let outcome : SandboxOutcome<'T> ref =
      ref (Threw (InvalidOperationException "the sandbox thread ended without a result"))
    let installed () =
      try
        install ()
      with
      | :? DllNotFoundException as e -> Error (LibraryNotFound e.Message)
      | e -> Error (InstallFailed (sprintf "installer raised %s" (e.GetType().Name), 0))
    // An unhandled exception on a background thread aborts the whole process, so nothing escapes this body.
    let body () =
      try
        match installed () with
        | Error reason -> outcome.Value <- Unavailable reason
        | Ok () ->
          outcome.Value <-
            (try
              Completed (work ())
             with e -> Threw e)
      with e -> outcome.Value <- Threw e
    try
      let thread = Thread(ThreadStart body, IsBackground = true, Name = "sagefs-thread-sandbox")
      thread.Start()
      thread.Join()
      outcome.Value
    with _ -> Unavailable (InstallFailed ("start sandbox thread", 0))

  /// The install step for a program with the given thread-creation rule. `run` uses `Refused`; a test uses `Allowed` to show the
  /// leak the product had before.
  let installerFor (threads: Seccomp.ThreadCreation) (arch: SandboxArchitecture) (policy: SandboxPolicy) : unit -> Result<unit, SandboxUnavailable> =
    fun () -> installOnThisThread (Seccomp.denyProgramFor threads arch policy)

  /// Runs `work` on a fresh dedicated background thread that installs the policy's filter first, waits for it,
  /// and returns what happened. If the filter cannot be installed, or the pool cannot be shown to have a worker, the work does
  /// not run and the result is Unavailable, with the reason. Nothing the work throws escapes. The filtered thread cannot make a
  /// thread, so no thread of the process can end up carrying the filter but the sandbox's own.
  let run (policy: SandboxPolicy) (work: unit -> 'T) : SandboxOutcome<'T> =
    match availability () with
    | Error reason -> Unavailable reason
    | Ok arch ->
      match SandboxPool.prepare () with
      | Error reason -> Unavailable reason
      | Ok () ->
        match SandboxPool.heldDuringCalls.Enter() with
        | Error reason -> Unavailable reason
        | Ok epoch ->
          try
            runWithInstaller (installerFor Seccomp.ThreadCreation.Refused arch policy) work
          finally
            SandboxPool.heldDuringCalls.Leave epoch
