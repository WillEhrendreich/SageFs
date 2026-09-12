/// Typed model of the one piece of daemon state this proof suite asserts
/// against, and the one boundary (a raw JSON HTTP response) where a bare
/// bool is allowed to exist — for exactly as long as it takes to convert it
/// into `LiveTestingState` below. Nothing downstream of that conversion
/// ever touches a raw bool.
///
/// Also has NO Fable dependency: the DU and the conversion function are
/// plain F#, loadable by `dotnet fsi` as well as compiled by Fable — only
/// the actual HTTP fetch + JSON.parse (which must produce a
/// `LiveTestingStatusResponse`) lives in Bindings.fs, which does need Fable.
module SageFs.VscodeTestElectron.DaemonContract

/// The daemon's live-testing enablement, once decoded off the wire. A DU
/// rather than a bool because "is it on" is domain state, not a flag.
type LiveTestingState =
  | Enabled
  | Disabled

/// Shape of the raw JSON body from `GET /api/live-testing/status` — only
/// the field this suite actually reads. `Enabled` is a bare bool here
/// because that's what's on the wire; `ofResponse` converts it immediately.
type LiveTestingStatusResponse =
  abstract Enabled: bool

let ofResponse (response: LiveTestingStatusResponse) : LiveTestingState =
  match response.Enabled with
  | true -> Enabled
  | false -> Disabled

/// The daemon's hot-reload watch state for one session, once decoded off
/// the wire — never a bare watchedCount int downstream of `ofHotReloadResponse`.
type HotReloadWatchState =
  | NoFilesWatched
  | SomeFilesWatched of count: int

/// Shape of the raw JSON body from `GET /api/sessions/{sid}/hotreload` —
/// only the field this suite actually reads.
type HotReloadStatusResponse =
  abstract watchedCount: int

let ofHotReloadResponse (response: HotReloadStatusResponse) : HotReloadWatchState =
  match response.watchedCount with
  | 0 -> NoFilesWatched
  | n -> SomeFilesWatched n

/// What a proof attempt actually established — never a bare bool/exception,
/// so a caller can report exactly why an interaction wasn't proven.
type ProofOutcome =
  | Proven
  | DisprovenBy of reason: string
