/// Pure decisions behind the VS Code context keys that gate the command
/// palette (`contributes.menus.commandPalette` in package.json) and the
/// per-view welcome content (`contributes.viewsWelcome`).
///
/// WHY — measured in a real VS Code instance: typing "SageFs" offered
/// `Enable Live Testing` and `Disable Live Testing` simultaneously, plus
/// every daemon-only command, in every session state, because
/// `commandPalette` was absent and the one context key the package.json DID
/// reference (`sagefs:daemonRunning`) was never set anywhere. Extension.fs
/// calls `setContext` with the results of these functions whenever the
/// underlying state changes; keeping the decisions here (rather than inline
/// at each call site) means each one is total and independently pinned
/// under `dotnet fsi` (tests/ContextKeysContractTests.fsx), so a future new
/// case in `VscLiveTestingEnabled` fails to compile here instead of quietly
/// leaving a toggle visible in the wrong state.
///
/// No Fable dependency.
module SageFs.Vscode.ContextKeysPure

open SageFs.Vscode.LiveTestingTypes

/// `sagefs:liveTestingEnabled` from the daemon's own `EnabledChanged`
/// transition event (`VscStateChange.EnabledChanged`). Exhaustive: a future
/// third case fails to compile here rather than leaving both palette
/// entries — or neither — visible.
let liveTestingEnabledContext (enabled: VscLiveTestingEnabled) : bool =
  match enabled with
  | VscLiveTestingEnabled.LiveTestingOn -> true
  | VscLiveTestingEnabled.LiveTestingOff -> false

/// The same key, derived from a `VscTestSummary.DiscoveryState` instead of a
/// transition event — needed because a transition event only fires on a
/// change, but the context key must also be correct for the RESTING state
/// the very first time a summary arrives after connecting (or reconnecting),
/// before any transition has been observed this session. "disabled" is the
/// daemon's own authoritative word for "off" regardless of `Total`
/// (see `VscTestSummary.statusBarView`'s `legacyText`); an empty string is a
/// daemon too old to send one, and is treated as "off" — fail closed, since
/// a false "on" would show a Disable action that does nothing.
let liveTestingEnabledFromDiscoveryState (discoveryState: string) : bool =
  match discoveryState with
  | "disabled" | "" -> false
  | _ -> true

/// `sagefs:hasSession` — backs the Sessions view's "no session yet" welcome
/// content. True the moment the daemon reports at least one session, whether
/// or not it is the currently-selected one.
let hasSessionContext (sessionCount: int) : bool =
  sessionCount > 0

/// `sagefs:hasFsharpProject` — backs the "no F# project in this workspace"
/// welcome content. True the moment a workspace scan finds at least one
/// `.fsproj`/`.sln`/`.slnx` candidate.
let hasFsharpProjectContext (candidateCount: int) : bool =
  candidateCount > 0
