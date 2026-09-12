/// Entry point invoked directly via `node` — NOT inside a VS Code extension
/// host. See Bindings.fs's header for why Node appears in this directory at
/// all, and why that is not the same thing as hand-written JS test logic.
///
/// Every path this launcher needs comes from an environment variable the
/// calling SageFs.Tests Expecto test sets, so this file does no path
/// joining or discovery of its own. In particular SAGEFS_TE_USER_DATA_DIR
/// must already contain a settings.json with the real daemon's mcpPort/
/// dashboardPort — the extension reads those from VS Code configuration
/// (workspace.getConfiguration("sagefs")), never from process.env, so the
/// .NET caller (which already knows the daemon's port because it started
/// the daemon) writes that file before invoking this launcher rather than
/// this launcher trying to construct VS Code settings JSON itself.
module SageFs.VscodeTestElectron.Launcher

open Fable.Core
open Fable.Core.JsInterop
open SageFs.VscodeTestElectron.Bindings

let private requireEnv (name: string) : string =
  let value = NodeProcess.getEnv name
  match box value with
  | null -> failwithf "missing required env var %s" name
  | _ -> value

let private tryEnv (name: string) : string option =
  let value = NodeProcess.getEnv name
  match box value with
  | null -> None
  | _ -> Some value

let private options () : TestOptions =
  let o = createEmpty<TestOptions>
  o.extensionDevelopmentPath <- requireEnv "SAGEFS_TE_EXT_DEV_PATH"
  o.extensionTestsPath <- requireEnv "SAGEFS_TE_TESTS_PATH"
  o.launchArgs <-
    [| requireEnv "SAGEFS_TE_WORKSPACE"
       sprintf "--user-data-dir=%s" (requireEnv "SAGEFS_TE_USER_DATA_DIR")
       "--disable-workspace-trust" |]
  o.extensionTestsEnv <- NodeProcess.env
  match tryEnv "SAGEFS_TE_VSCODE_PATH" with
  | Some path -> o.vscodeExecutablePath <- path
  | None -> () // let @vscode/test-electron download and cache its own copy
  o

let main () =
  promise {
    try
      let! exitCode = runTests (options ())
      NodeProcess.exit (int exitCode)
    with ex ->
      NodeProcess.logError (sprintf "SageFs extension-host proof suite failed: %s" ex.Message)
      NodeProcess.exit 1
  }
  |> Promise.start

main ()
