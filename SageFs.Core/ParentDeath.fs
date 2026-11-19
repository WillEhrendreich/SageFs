namespace SageFs

open System
open System.Runtime.InteropServices

/// The only correct answer to "did my owner die" is to let the kernel answer
/// it. PR_SET_PDEATHSIG asks Linux to SIGKILL this process the moment its
/// parent exits. There is no pid to compare, no start time to fence, no env
/// var, no file and no clock: one registration, one kernel event.
module ParentDeath =

  [<DllImport("libc", EntryPoint = "prctl", SetLastError = true)>]
  extern int private prctl(int option, unativeint arg2, unativeint arg3, unativeint arg4, unativeint arg5)

  [<Literal>]
  let private PrSetPdeathsig = 1

  [<Literal>]
  let private SigKill = 9

  /// Arm the kernel death signal. A no-op off Linux, where the daemon's
  /// graceful shutdown and startup sweep own worker lifetime instead.
  /// Returns false only if the registration itself failed, so a caller can
  /// refuse to run rather than silently lose the guarantee.
  let arm () : bool =
    if not (OperatingSystem.IsLinux()) then true
    else prctl(PrSetPdeathsig, unativeint SigKill, UIntPtr.Zero, UIntPtr.Zero, UIntPtr.Zero) = 0
