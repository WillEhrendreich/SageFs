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
/// (reads, mmap, futex, madvise, thread creation) keeps working.
type SandboxPolicy =
  /// socket, connect, accept, bind, listen, send* and socketpair fail with EPERM.
  | NoNetwork
  /// NoNetwork, plus: opening a file for write, create, truncate or append; unlink, rename, mkdir, rmdir,
  /// truncate, chmod, chown, symlink and link; fork, vfork, execve, execveat and any clone that does not make
  /// a thread. All of these fail with EPERM (clone3 fails with ENOSYS, see Seccomp.denyProgram).
  | NoNetworkNoWritesNoSpawn

/// Why no filter was installed. The caller shows this, so "no I/O containment here" always comes with a reason.
type SandboxUnavailable =
  | NotLinux
  | UnverifiedArchitecture of string
  | InstallFailed of step: string * errno: int
  | LibraryNotFound of string

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

  let private x86_64Body (policy: SandboxPolicy) : Insn list =
    let network = denyEach networkSyscalls
    let writesAndSpawns =
      match policy with
      | NoNetwork -> []
      | NoNetworkNoWritesNoSpawn ->
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
    @ writesAndSpawns

  /// The classic-BPF program (sock_filter array, little endian) that denies what the policy names and allows
  /// everything else. Every path ends in a RET that is ALLOW or ERRNO. It never returns KILL or TRAP, so a
  /// denied call fails with an errno and nothing is killed. A syscall from an architecture the program was
  /// not written for is ALLOWed.
  ///
  /// Aarch64 has no deny list: its syscall numbers are not verified, `support` reports it unavailable, and the
  /// program is one ALLOW so that no unchecked list of numbers exists to be installed by mistake.
  let denyProgram (arch: SandboxArchitecture) (policy: SandboxPolicy) : byte[] =
    match arch with
    | X86_64 -> assemble (x86_64Body policy)
    | Aarch64 -> assemble []

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
/// A thread the work starts INHERITS the filter, so work cannot escape it by starting a thread. A thread that
/// is already running (a pool thread the work hands a task to) does not have it, so a task queued from the
/// work and run elsewhere is not contained.
///
/// There is no deadline here: the CALLER owns the deadline. If the work never returns, `run` never returns, so
/// call it from a thread you can abandon and stop waiting on your own timer. An abandoned thread keeps running
/// and keeps its filter until it ends; the filter dies with the thread and never touches any other thread.
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

  /// Runs `work` on a fresh dedicated background thread that installs the policy's filter first, waits for it,
  /// and returns what happened. If the filter cannot be installed the work does not run and the result is
  /// Unavailable, with the reason. Nothing the work throws escapes.
  let run (policy: SandboxPolicy) (work: unit -> 'T) : SandboxOutcome<'T> =
    match availability () with
    | Error reason -> Unavailable reason
    | Ok arch -> runWithInstaller (fun () -> installOnThisThread (Seccomp.denyProgram arch policy)) work
