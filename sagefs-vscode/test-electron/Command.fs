/// The VS Code commands SageFs's extension-host proof suite invokes, as a
/// closed set — no call site may pass an arbitrary string. This is invoked
/// via `vscode.commands.executeCommand`, which (unlike the command palette
/// in the old, retired CDP-driven journeys) dispatches by the command's
/// literal id declared in package.json's `contributes.commands`, never its
/// display title.
///
/// This file has NO Fable dependency on purpose: it is loaded two ways —
/// compiled by Fable into the extension-host proof bundle
/// (ExtensionHostSuite.fs), and loaded directly by `dotnet fsi` in
/// tests/CommandContractTests.fsx, which checks every id here against
/// package.json's contributes.commands so a rename in one place can never
/// silently desync from the other.
module SageFs.VscodeTestElectron.Command

type Command =
  | EnableLiveTesting
  | DisableLiveTesting
  | HotReloadWatchAll
  | HotReloadUnwatchAll

/// The command id declared in package.json's contributes.commands — the
/// exact string `vscode.commands.executeCommand` dispatches on.
let id =
  function
  | EnableLiveTesting -> "sagefs.enableLiveTesting"
  | DisableLiveTesting -> "sagefs.disableLiveTesting"
  | HotReloadWatchAll -> "sagefs.hotReloadWatchAll"
  | HotReloadUnwatchAll -> "sagefs.hotReloadUnwatchAll"
