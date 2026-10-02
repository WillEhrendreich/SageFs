/// The editor sandbox for a Neovim lemming: tmux and Neovim run in it, and a shell inside Neovim
/// (`:!`, `:terminal`, `:lua os.execute`, a filter like `!!sh`) can reach only what is mounted here.
/// A key list cannot be allow-listed down to "edits only", so the sandbox, not the key parser, is
/// what decides what such a shell can reach. Pure: a description in, bubblewrap arguments out, so
/// the walls can be read, and tested with a real bubblewrap, without starting an editor.
///
///   writable   the workspace, and the tmux socket directory
///   read-only  the workspace's .git (a repository names programs to run: hooks, fsmonitor,
///              textconv), the Neovim install, the plugin, init.lua and the F# parser, the system
///   absent     everything else under the run directory: bin/ (the driver the harness later runs
///              OUTSIDE every sandbox), out/ (the evidence), dotnethome, cmdchome; and a tmpfs
///              HOME, so no credential, cache or dotfile of the user's is in reach
///
/// What it does NOT cut is the network: the plugin must reach the shared daemon on localhost, and a
/// tour or task curls the app the daemon runs on another localhost port. The daemon is the product
/// and runs F# for whoever asks it; that is accepted for this harness and written down in README.md.
/// The point of the mounts is that nothing the editor writes is code the harness runs later.
module LemDrive.EditorSandbox

open System.IO

/// What the harness puts in front of every `git` it runs on a workspace the editor could write to. The
/// editor's .git is mounted read-only, and a repository is also a list of programs to run (hooks,
/// fsmonitor, a pager, an external diff), so those are pinned off with `-c`, which beats every config file.
let harnessGitPins : string list =
  [ "-c"; "core.fsmonitor=false"
    "-c"; "core.hooksPath=/dev/null"
    "-c"; "core.pager=cat"
    "-c"; "diff.external=" ]

/// What the editor sandbox is built from.
type Editor =
  { Home: string
    Workspace: string
    /// The run's out/ directory (the evidence), hidden except for the tmux socket and the editor's own files.
    Out: string
    NvimBin: string
    PluginDir: string
    /// The shared daemon's MCP port, which the plugin reads from the environment.
    DaemonPort: int
    /// The workspace has a .git, which is then laid over read-only.
    HasGit: bool }

let private roBind (p: string) = [ "--ro-bind"; p; p ]
let private bind (p: string) = [ "--bind"; p; p ]
let private setenv (name: string) (value: string) = [ "--setenv"; name; value ]

/// The system, an empty tmpfs HOME and its own pid, ipc and uts namespaces.
let baseArgs (home: string) : string list =
  [ [ "--die-with-parent"; "--unshare-pid"; "--unshare-ipc"; "--unshare-uts"; "--clearenv" ]
    roBind "/usr" @ roBind "/etc"
    [ "--symlink"; "usr/bin"; "/bin"; "--symlink"; "usr/sbin"; "/sbin"; "--symlink"; "usr/lib"; "/lib"; "--symlink"; "usr/lib64"; "/lib64" ]
    roBind "/run/systemd/resolve"
    [ "--proc"; "/proc"; "--dev"; "/dev"; "--tmpfs"; "/tmp"; "--tmpfs"; home ] ]
  |> List.concat

/// The directory a Neovim install lives in: two levels above the binary (bob/nvim-bin/nvim).
let installDir (nvimBin: string) : string =
  match Path.GetDirectoryName nvimBin with
  | null -> nvimBin
  | bin -> (match Path.GetDirectoryName bin with null -> bin | d -> d)

/// The editor sandbox's bubblewrap arguments. Later mounts win, which is how .git becomes read-only
/// inside a writable workspace and how out/ becomes empty apart from its two exceptions.
let args (e: Editor) : string list =
  let install = installDir e.NvimBin
  let ui = Path.Combine(e.Out, "ui")
  let tmux = Path.Combine(e.Out, "tmux")
  [ baseArgs e.Home
    roBind install
    roBind e.PluginDir
    bind e.Workspace
    (match e.HasGit with
     | true -> roBind (Path.Combine(e.Workspace, ".git"))
     | false -> [])
    [ "--tmpfs"; e.Out ]
    bind tmux
    roBind ui
    [ "--chdir"; e.Workspace ]
    setenv "HOME" e.Home
    setenv "PATH" (sprintf "%s/nightly/bin:%s/nvim-bin:/usr/bin:/bin" install install)
    setenv "LANG" "C.UTF-8" @ setenv "LC_ALL" "C.UTF-8" @ setenv "TERM" "tmux-256color"
    setenv "TMUX_TMPDIR" tmux
    setenv "SAGEFS_NVIM_DIR" e.PluginDir
    setenv "LEM_TS_DIR" (Path.Combine(ui, "ts"))
    setenv "SAGEFS_MCP_PORT" (string e.DaemonPort) ]
  |> List.concat

/// What a lemming (cmdc) is given on top of the core lemming sandbox, so the editor is reachable
/// only through the driver: the workspace is read-only (a file changes only through the editor),
/// its MCP file is overlaid with an empty server list (a UI lemming drives the editor, it does not
/// call MCP), and it sees the driver's unix socket, the driver tool and, when the task allows, the
/// plugin README. Later mounts win over the core sandbox's.
let lemmingExtras (e: Editor) (driveDir: string) : string list * (string * string) list =
  let ipc = Path.Combine(e.Out, "ipc")
  [ [ "--ro-bind"; e.Workspace; e.Workspace ]
    [ "--ro-bind"; Path.Combine(e.Out, "ui", "empty-mcp.json"); Path.Combine(e.Workspace, ".mcp.json") ]
    [ "--bind"; ipc; ipc ]
    [ "--ro-bind"; driveDir; "/lem/drive" ]
    [ "--ro-bind"; Path.Combine(e.Out, "docs"); "/lem/docs" ] ]
  |> List.concat,
  [ "LEM_DRIVE_SOCKET", Path.Combine(ipc, "drive.sock") ]
