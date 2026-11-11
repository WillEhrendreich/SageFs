/// Whether SageFs claims Ionide's FSI send command ids, and what it says when a claim is refused.
///
/// WHY this module exists: Alt+Enter support registered `fsi.SendSelection`, `fsi.SendLine` and
/// `fsi.SendFile` on every activation. VS Code throws when an id is taken, and the throw was
/// swallowed at debug level, so whichever extension activated second lost its registration with
/// no message from either side. There was no setting to stop it either (roast-5 finding 6).
///
/// No Fable dependency; tested under `dotnet fsi` (tests/IonideTakeoverContractTests.fsx).
module SageFs.Vscode.IonideTakeoverPure

/// The key under `sagefs.`, declared in package.json.
let settingKey = "takeOverIonideSendCommands"

type Plan =
  | TakeOver
  | LeaveToIonide

let plan (enabled: bool) : Plan =
  if enabled then TakeOver else LeaveToIonide

/// The command ids Ionide registers for "send to FSI".
let commandIds : string list =
  [ "fsi.SendSelection"; "fsi.SendLine"; "fsi.SendFile" ]

/// The line written to the SageFs output channel when VS Code refuses a registration.
let refusedNotice (commandId: string) : string =
  sprintf
    "SageFs could not register %s: another extension (Ionide, usually) already owns it. Alt+Enter still works through SageFs's own keybinding. Set sagefs.%s to false to stop SageFs claiming these commands."
    commandId
    settingKey
