/// What the dashboard page believes about the daemon, and what it says about it.
///
/// The rule this slice exists to keep: a LATE HEARTBEAT IS A SUSPICION, NEVER A VERDICT. The server patches a
/// heartbeat signal on a fixed cadence and the page timestamps its arrival with its OWN clock. When that
/// timestamp gets old the live stream is late, and a late stream has many causes: the daemon is gone, the
/// daemon is busy, the stream dropped and Datastar is retrying, or the PAGE was the thing that stopped (a
/// background tab whose timers are throttled, a laptop that slept). Only the first one is "the daemon is not
/// running", and the page can tell it apart cheaply: the dashboard is served by the daemon, so a direct request
/// to it is an independent proof of life.
///
/// So the page tells four stories, never one:
///   - heartbeat fresh                          -> connected, no banner
///   - heartbeat late, daemon ANSWERS           -> connected, soft banner "live updates paused, reconnecting"
///   - heartbeat late, connection REFUSED       -> down at once, the OS said nothing is listening
///   - heartbeat late, request TIMES OUT        -> still connected until it has timed out
///                                                 `probeFailuresBeforeDown` times in a row (a busy daemon is
///                                                 slow, not gone)
/// and a check whose own timer was paused for much longer than its period (a suspended or throttled page)
/// gives the stream a fresh window to deliver, because the pause was the page's, not the daemon's.
///
/// Both sides of every time comparison are the BROWSER's own `Date.now()`. The server's heartbeat value is a
/// change token and is never compared with the client clock (comparing the two failed open under client clock
/// skew). The arrival stamp is written only when the token changes: an unconditional stamp was refreshed by
/// reconnect attempts against a dead daemon, so the page never went stale and the banner never appeared.
///
/// Why a server heartbeat and a client check, not a patched `window.fetch` or a `MutationObserver` on `#main`: a
/// daemon that dies MID-stream errors at the body level, which a check on the response headers never sees, and
/// `#main` legitimately stops mutating on a perfectly healthy connection (the no-change render guard), so
/// neither could tell a quiet page from a dead daemon. The heartbeat is patched on a fixed cadence whether or not
/// the rendered snapshot changed, and the page watches it through Datastar's own signal, effect and interval API.
///
/// Datastar splits an expression on top-level `;` and newlines, so each expression below is a list of
/// top-level statements whose bodies use commas, ternaries and arrow functions, never blocks.
module SageFs.Server.DashboardConnection

open System
open Falco.Markup
open Falco.Datastar
open SageFs
open SageFs.Server.DashboardTypes

/// Server-patched, absolute-clock heartbeat: a CHANGE TOKEN, never compared with the browser's clock.
[<Literal>]
let HeartbeatSignal = "dsHeartbeatAt"

/// The browser's own `Date.now()` at the moment it last saw `HeartbeatSignal` change.
[<Literal>]
let LastSeenSignal = "dsLastSeenAt"

/// The last `HeartbeatSignal` VALUE this page observed, so stamping `LastSeenSignal` is idempotent.
[<Literal>]
let LastBeatSignal = "dsLastBeatAt"

/// The browser's `Date.now()` at the previous staleness check: how a page that was paused notices it was.
[<Literal>]
let LastTickSignal = "dsLastTickAt"

/// True while the heartbeat is late. With `Signals.Connected` still true it is the soft banner.
[<Literal>]
let LateSignal = "dsLate"

/// True for the one check that found its own timer had been paused much longer than its period.
[<Literal>]
let PausedSignal = "dsPaused"

/// How many consecutive requests to the daemon timed out (not refused: a refusal is down at once).
[<Literal>]
let ProbeFailsSignal = "dsProbeFails"

/// The route the page asks "are you there?". The dashboard is served by the daemon, so any HTTP answer is
/// proof of life, whatever its status.
let probeRoute = "/api/daemon-info"

/// Timeouts in a row before a busy-looking daemon is called down. A refused connection needs no repeat.
let probeFailuresBeforeDown = 2

/// A check that fires this many periods after the last one was paused, not the daemon.
let pausedAfterPeriods = 2

let private ms (t: TimeSpan) = int64 t.TotalMilliseconds

/// Stamps `LastSeenSignal` with the BROWSER's own `Date.now()`, but only when `HeartbeatSignal` holds a value
/// this page has not seen before.
let heartbeatArrivalEffectExpr () =
  sprintf
    "$%s !== $%s && ($%s = $%s, $%s = Date.now())"
    LastBeatSignal HeartbeatSignal
    LastBeatSignal HeartbeatSignal
    LastSeenSignal

/// The check run on every heartbeat period. Reads like the stories in the module doc, one statement each.
let connectionCheckExpr () =
  let period = ms Timeouts.dashboardHeartbeat
  let staleAfter = ms Timeouts.dashboardStaleAfter
  let probeBudget = ms Timeouts.dashboardProbe
  let connected = Signals.Connected
  [ // Was this timer itself paused? Then the pause was the page's: give the stream a fresh window.
    sprintf "$%s = Date.now() - $%s > %d" PausedSignal LastTickSignal (period * int64 pausedAfterPeriods)
    sprintf "$%s = Date.now()" LastTickSignal
    sprintf "$%s && ($%s = Date.now())" PausedSignal LastSeenSignal
    // Is the live stream late?
    sprintf "$%s = Date.now() - $%s >= %d" LateSignal LastSeenSignal staleAfter
    // Fresh: connected, nothing suspicious.
    sprintf "!$%s && ($%s = true, $%s = 0)" LateSignal connected ProbeFailsSignal
    // Late: ask the daemon directly. Any answer proves it is running. A refusal means nothing is listening.
    // A timeout is ambiguous, so it takes several in a row.
    sprintf
      "$%s && fetch('%s', { cache: 'no-store', signal: AbortSignal.timeout(%d) }).then(() => ($%s = 0, $%s = true)).catch(e => e.name === 'TimeoutError' ? ($%s = $%s + 1, $%s = $%s < %d) : ($%s = false))"
      LateSignal probeRoute probeBudget
      ProbeFailsSignal connected
      ProbeFailsSignal ProbeFailsSignal connected ProbeFailsSignal probeFailuresBeforeDown
      connected
    // Mirror the belief onto the body for tests and scripts that read it.
    sprintf "document.body.setAttribute('data-connected', $%s ? 'true' : 'false')" connected ]
  |> String.concat "; "

/// What the monitor element carries: the signals, the arrival effect and the periodic check.
///
/// Only the heartbeat token is seeded from the server's clock (a change token, never compared with the
/// browser's). The two browser-clock stamps are seeded 0 on purpose: the arrival effect stamps `LastSeenSignal`
/// when the page loads, and a first check that finds `LastTickSignal` at 0 reads as "my timer was paused" and
/// gives the stream a fresh window, so the very first check never reads as late. A seed from the clock here
/// would also make the shell's snapshot differ on every render.
let monitorAttributes () : XmlAttribute list =
  [ Ds.signal (HeartbeatSignal, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
    Ds.signal (LastSeenSignal, 0L)
    Ds.signal (LastBeatSignal, 0L)
    Ds.signal (LastTickSignal, 0L)
    Ds.signal (LateSignal, false)
    Ds.signal (ProbeFailsSignal, 0)
    Ds.signal (PausedSignal, false)
    Ds.effect (heartbeatArrivalEffectExpr ())
    Ds.onInterval (connectionCheckExpr (), int (ms Timeouts.dashboardHeartbeat)) ]

/// The daemon cannot be reached: nothing is listening, or it has stopped answering for several tries in a row.
let hardBanner () : XmlNode =
  Elem.div
    [ Attr.id DomIds.ServerStatus
      Attr.class' "conn-banner conn-disconnected"
      Attr.style "display:none"
      Ds.show (sprintf "!$%s" Signals.Connected) ]
    [ Text.raw "❌ Daemon not running — start SageFs to continue" ]

/// The daemon answers, and the live stream is catching up. Says what is known, and claims nothing more.
let softBanner () : XmlNode =
  Elem.div
    [ Attr.id DomIds.ServerStale
      Attr.class' "conn-banner conn-reconnecting"
      Attr.style "display:none"
      Ds.show (sprintf "$%s && $%s" LateSignal Signals.Connected) ]
    [ Text.raw "⏳ Live updates paused — the daemon answers, this page is reconnecting" ]
