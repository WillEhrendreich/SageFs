/// The App co-actor's runtime extension (demo-actors-plan.md §1.2/§2.3): the
/// pure translation between the rich `Domain.AppKind` and the primitive
/// `Wire.AppConfig`/cell-extension values `Runtime.fs`'s extension points
/// (`cellSpec`'s `actorBinds`/`actorPrologue`, `wirePlanOf`'s `Wire.App`)
/// take — mirroring `Runtime.Core`'s own `clientToken`. This file is called
/// FROM `Runtime.fs`'s extension points once the main thread splices the App
/// co-actor into a joint hot-reload scenario's plan (§7: "the merge order is
/// F → App → editors' hot-reload scenarios"); it never edits `Runtime.fs`
/// itself.
module SageFs.Demos.Runtime.App

open SageFs.Demos
open SageFs.Demos.Domain

/// The wire token for a `Domain.AppKind` (§1.2, §2.3) — the one place the
/// rich `AppKind` DU is flattened to the primitive `Wire.AppConfig.Kind`
/// string that crosses the sandbox wall. `NoApp` carries no token: a
/// `NoApp` scenario places no App actor at all (`Layout.rects` never emits
/// an `ActorId.App` entry for it), so there is nothing to configure.
let appConfigOf (appKind: AppKind) : Wire.AppConfig =
  let token =
    match appKind with
    | AppKind.Web -> Some "web"
    | AppKind.Raylib -> Some "raylib"
    | AppKind.Console -> Some "console"
    | AppKind.NoApp -> None

  { Wire.Kind = token }

/// Extra RO binds the App co-actor's cell needs beyond `Runtime.Core.cellSpec`'s
/// fixed set (§2.3 external-deps doctrine: fail loud per kind if a real
/// dependency is absent — but every dependency the three `AppKind`s need
/// TODAY is already reachable inside the cell without an extra bind: the
/// repo's own sample projects and their restored NuGet packages are covered
/// by the fixed `repoRoot`/`nugetPackagesDir` binds, and the system
/// libraries a Raylib window's software-GL path needs (X11, Mesa/libGL) live
/// under `/usr`, which `Sandbox.args` already RO-binds whole for every cell.
/// Kept as a real function — not a bare `[]` literal — so a future kind that
/// DOES need one (e.g. a bundled terminal binary for `Console`, if the
/// "render captured stdout" alternative in §2.3 is chosen instead) is a
/// one-line addition here, never a new extension point on `Runtime.fs`.
let actorBinds (_appKind: AppKind) : (string * string) list = []

/// The App co-actor starts nothing of its own before the cell-agent runs
/// (§2.3: "the App actor only finds, places, and observes the window the
/// session spawned; it does not start it") — the second window this actor
/// captures is the RESULT of a `WireStep` the primary actor drives DURING
/// recording (a dashboard/editor "Run App" click reaching the daemon's own
/// run-app endpoint), not a cell-boot-time concern, so there is no bash
/// fragment to splice into `innerScript` ahead of the cell-agent for any
/// `AppKind`.
let actorPrologue (_appKind: AppKind) : string list = []

/// Resolves the `Actors.App.LaunchConfig.AppUrl` for `AppKind.Web` from the
/// daemon's run-app response port, once the cell-agent has one to hand over
/// (§2.3 — the App actor never resolves this itself). `None` for
/// `Raylib`/`Console` (they have no URL, only a display window) and for
/// `NoApp` (no window at all).
let resolveAppUrl (appKind: AppKind) (workerBaseUrl: string) : string option =
  match appKind with
  | AppKind.Web -> Some workerBaseUrl
  | AppKind.Raylib
  | AppKind.Console
  | AppKind.NoApp -> None
