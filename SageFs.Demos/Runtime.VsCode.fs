/// The VS Code actor's runtime extension (demo-actors-plan.md §1.2/§2.1):
/// resolves the VS Code build + extension directory this actor needs and
/// exposes the cell binds/`Wire.VsCodeConfig` `Runtime.fs`'s `record`
/// function will consume once its `actorBinds`/`wirePlanOf` call sites are
/// extended to dispatch on `scenario.Client` (that single extra call site is
/// the one piece of `Runtime.fs`'s CORE this island does not own — see the
/// module doc below for exactly what it looks like).
///
/// Deliberate shape choice: `SageFs.Demos.Actors.VsCode.launch` (not a bash
/// `innerScript` prologue) owns spawning VS Code — see that module's own
/// doc for why (symmetry with `Actors/Dashboard.fs`, and it is the shape
/// actually proven live against a real VS Code build on Xvfb). This module
/// therefore contributes cell BINDS (the VS Code build + this extension
/// directory, both RO) rather than an `actorPrologue` bash fragment; the
/// `cellBinds` list below is exactly what `Runtime.fs`'s private `cellSpec`
/// would append to its own `actorBinds` parameter.
module SageFs.Demos.Runtime.VsCode

open System
open System.IO
open SageFs.Demos

/// Resolves the VS Code build this actor drives — the SAME resolution
/// `sagefs-vscode/test-electron/Launcher.fs` already uses for its
/// extension-host proof suite (`SAGEFS_TE_VSCODE_PATH`, else the newest
/// cached `.vscode-test/vscode-linux-x64-*` download under
/// `sagefs-vscode/`) — no second download mechanism invented here. Fails
/// loud (mirrors the `--integration-vsc` `Environment.Exit 1` precedent)
/// rather than returning a guessed path that would surface as a baffling
/// spawn failure deep inside a cell.
let resolveCodeBin (repoRoot: string) : Result<string, string> =
  let envPath = Environment.GetEnvironmentVariable "SAGEFS_TE_VSCODE_PATH"

  if not (String.IsNullOrWhiteSpace envPath) && File.Exists envPath then
    Ok envPath
  else
    let cacheDir = Path.Combine(repoRoot, "sagefs-vscode", ".vscode-test")

    if not (Directory.Exists cacheDir) then
      Error(
        sprintf
          "no VS Code build found: set SAGEFS_TE_VSCODE_PATH, or run `npx @vscode/test-electron` under sagefs-vscode/ to populate %s (mirrors the --integration-vsc fail-loud precedent — never a silent skip of the VS Code scenarios)."
          cacheDir
      )
    else
      Directory.GetDirectories(cacheDir, "vscode-linux-x64-*")
      |> Array.sortDescending
      |> Array.tryHead
      |> Option.map (fun d -> Path.Combine(d, "code"))
      |> Option.filter File.Exists
      |> function
        | Some p -> Ok p
        | None -> Error(sprintf "no usable VS Code binary under %s" cacheDir)

/// The built `sagefs-vscode` extension directory this actor loads via
/// `--extensionDevelopmentPath` — resolves the PATH only; the extension's
/// own `dist/Extension.js` must already exist (`npm run compile`, Fable +
/// esbuild), exactly like `Runtime.fs`'s own `buildSampleFromSource`
/// doctrine pre-builds a sample project on the host before a cell (which
/// has no network) ever starts.
let extensionDevPath (repoRoot: string) : string = Path.Combine(repoRoot, "sagefs-vscode")

/// Cell binds this actor needs, appended to `Runtime.fs`'s own
/// `actorBinds` parameter once `record`'s `Client.VsCode` case is wired: the
/// resolved VS Code build's OWN directory (RO — the whole directory, not
/// just the `code` binary, since Electron needs its bundled resources next
/// to it) and the built extension directory (RO). No separate `node` bind —
/// the extension host runs on VS Code's own bundled Node (confirmed
/// directly during this island's live spike: the launch used only the raw
/// `code` binary, no system `node` on its `PATH`). `xdotool` is a THIRD,
/// small dependency this actor's `tryPlaceWindow`/`Extension.fs`'s
/// `resolveOwnWindowRect` both shell out to for real window-geometry
/// queries — resolve and RO-bind it the same way `chromiumDir`/`dotnetRoot`
/// are resolved in `Runtime.fs`, or every rect this actor resolves inside
/// the cell will honestly come back `null` (as it did in this island's own
/// dev-box spike, where `xdotool` is not installed).
let cellBinds (codeBinDir: string) (extDevPath: string) : (string * string) list =
  [ codeBinDir, "/vscode-bin"
    extDevPath, "/vscode-ext" ]

/// The `Wire.VsCodeConfig` a `Client.VsCode` scenario's `ScenarioPlan`
/// carries — filled once `Runtime.fs`'s `wirePlanOf` gains a case for
/// `Client.VsCode` (today it always writes `VsCode = None`, §1.2's
/// integration step, out of this island's owned files).
let config (extDevPath: string) : Wire.VsCodeConfig = { ExtensionDevPath = Some extDevPath }

/// VS Code's own IPC socket has roughly a 107-char path-length ceiling
/// (confirmed directly: a `--user-data-dir` nested under a long cell path
/// threw `Error: listen EINVAL` with no window ever appearing). A cell's
/// own scratch tree is `/home/demo/...` (`Runtime.fs`'s `innerScript`), so
/// the VS Code actor's own `--user-data-dir` must stay short and shallow —
/// e.g. `/home/demo/vsc`, never a deeper nested path — once wired.
[<Literal>]
let RecommendedUserDataDir = "/home/demo/vsc"
