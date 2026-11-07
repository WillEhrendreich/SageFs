namespace SageFs

/// Whether the daemon's hygiene sweep tidies what is provably safe on its own. The sweep always records what it sees and
/// saves uncommitted worktree files to a salvage branch (nothing is lost by doing that), so this switch turns off only the
/// removing half. On by default: a pile that nothing tidies is what started the work (docs/plans/hygiene-that-runs-itself.md).
///
/// A process-wide value behind a lock, like the per-test timeout: the Settings page applies it live, the sweep reads it each
/// time it runs.
module HygieneSwitch =

  let private gate = obj ()
  let mutable private autoTidy : Toggle = On

  /// Whether the sweep tidies by itself right now.
  let autoTidyNow () : Toggle = lock gate (fun () -> autoTidy)

  let setAutoTidy (value: Toggle) : unit = lock gate (fun () -> autoTidy <- value)
