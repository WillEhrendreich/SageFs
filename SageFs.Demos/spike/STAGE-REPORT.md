# Phase-0 Foundation Spike — Stage Report

Ran on this machine (Arch Linux, Hyprland/Wayland desktop, `bwrap` unprivileged
userns, 16 cores / 62 GB RAM) against the tree at `03eaafe8` (fast-forwarded
from `master` at the start of this session; see git log). Grounded in
`demo-gif-plan.md` §0, §2, §4.1, §4.3, §4.5, §10.

**Result: all four stages PASS. The two riskiest unknowns (sandbox
control/data-plane boundary, and the libXtst input edge) are proven, and the
full vertical slice — sealed cell → real SageFs daemon → real Chromium →
XTEST click → Playwright-observed session card → StepLog back across the
boundary — works end to end.**

No process was left running, no port was left listening, and no core dump was
produced by the final, working versions of these scripts (see §Cleanup
verification and §Notes on debugging below — three *earlier, broken*
iterations of the scripts did genuinely crash while the right bwrap/Xvfb/build
flags were being found, which is exactly what a foundation spike is for; each
crash is root-caused below, not hidden).

## Correction to demo-gif-plan.md §2

The plan states `libXtst` is **not installed** on this machine and must be
bundled/extracted per-RID. That is stale: `ldconfig -p | grep -i xtst` shows
`/usr/lib/libXtst.so.6` present today, alongside `libX11.so.6`. Stage 3 below
RO-binds the system libraries directly rather than extracting a package. The
P/Invoke edge itself (`XOpenDisplay` / `XTestFakeMotionEvent` /
`XTestFakeButtonEvent` / `XFlush`) is identical either way — this only removes
one bundling step from Phase 4, it does not change the architecture.

---

## Stage 1 — Sandbox + capture — **PASS**

**Claim:** a `bwrap` cell with a private Xvfb, captured by `ffmpeg x11grab`,
with the output file landing on the host via an RW bind mount.

**Script:** `spike/stage1-sandbox-capture.sh`

**Command shape:**
```
bwrap --unshare-user --unshare-net --unshare-pid --unshare-ipc --uid 0 --gid 0 \
  --tmpfs /tmp --tmpfs /home/demo --ro-bind /usr /usr \
  --symlink usr/bin /bin --symlink usr/bin /sbin --symlink usr/lib /lib --symlink usr/lib /lib64 \
  --ro-bind /etc/fonts /etc/fonts --proc /proc --dev /dev \
  --bind <hostout> /out --die-with-parent --clearenv \
  --setenv HOME /home/demo --setenv PATH /usr/bin \
  --setenv LIBGL_ALWAYS_SOFTWARE 1 --setenv __EGL_VENDOR_LIBRARY_FILENAMES .../50_mesa.json \
  -- /bin/sh -c '
    mkdir -m 1777 -p /tmp/.X11-unix
    Xvfb :99 -screen 0 1280x720x24 -nocursor &
    ffmpeg -f x11grab -framerate 15 -video_size 1280x720 -i :99 -t 2 \
      -c:v libx264 -preset ultrafast -qp 0 /out/stage1.mkv
  '
```

**Evidence:**
- `/tmp/sagefs-demo-spike/cells/stage1/out/stage1.mkv` — 133,160 bytes.
- `ffprobe`: `codec_type=video width=1280 height=720 duration=2.000000`.
- Host-side file appeared at the bind-mount path the instant the cell wrote
  it — proves the RW bind-mount data plane from §4.1 works exactly as
  described: "a bind mount is just a window onto a host directory."

**Two real bugs found and fixed here (both are genuine Arch/bwrap/Xvfb
findings, not typos):**
1. `_XSERVTransmkdir: ERROR: euid != 0, directory /tmp/.X11-unix will not be
   created.` — Xvfb refuses to `mkdir /tmp/.X11-unix` itself unless euid==0.
   Fix: pre-create `/tmp/.X11-unix` (mode 1777) *before* starting Xvfb, so
   Xvfb only has to create the socket file inside an already-correct
   directory.
2. Even with the directory pre-created, Xvfb then reported
   `_XSERVTransmkdir: Owner of /tmp/.X11-unix should be set to root` and
   **segfaulted** in `libnvidia-egl-gbm.so.1` → `libEGL_nvidia.so.0` →
   `swrast_dri.so` on its error path (this machine has an NVIDIA GPU; Xvfb's
   glamor/EGL probing picked the NVIDIA EGL vendor JSON, which crashes when
   it can't find a working GBM device inside the sandbox's minimal `/dev`).
   Fix: two changes. (a) `bwrap --uid 0 --gid 0` — an unprivileged user
   namespace lets you map your own real uid to a uid of your choosing inside
   the namespace (this is the standard "rootless container" trick), and
   mapping to 0 satisfies Xvfb's ownership check. (b) Force Mesa's software
   EGL instead of the NVIDIA vendor: `LIBGL_ALWAYS_SOFTWARE=1` plus
   `__EGL_VENDOR_LIBRARY_FILENAMES=/usr/share/glvnd/egl_vendor.d/50_mesa.json`
   (this machine has both `10_nvidia.json` and `50_mesa.json`; glvnd picks the
   lowest-numbered file first, i.e. NVIDIA, unless overridden).

Neither of these appears in demo-gif-plan.md §4.1/§4.3 — they're Phase-0
findings this spike exists to surface: the same two flags will be needed
wherever the real tool spawns Xvfb in a cell.

---

## Stage 2 — GUI in the cell + capture — **PASS**

**Claim:** a real headed Chromium (the bundled Playwright browser) inside the
cell, rendering a local `file://` page, captured, with proof the page
actually painted.

**Script:** `spike/stage2-gui-capture.sh`, page fixture:
`spike/fixtures/stage2.html` (a big green "Quick Start"-styled button at a
known position, `data-testid="quick-start"` — same test id the real dashboard
uses, so this fixture doubles as a rehearsal for Stage 4's target).

**Command shape:** same bwrap flags as Stage 1, plus `--ro-bind` of
`~/.cache/ms-playwright/chromium-1208/chrome-linux64` → `/chrome` and the
fixtures dir → `/work`, then inside the cell:
```
/chrome/chrome --no-sandbox --disable-gpu --ozone-platform=x11 \
  --window-position=0,0 --window-size=1280,720 --user-data-dir=/home/demo/chrome-profile \
  --no-first-run --disable-features=Translate --disable-extensions \
  --disable-infobars --no-default-browser-check \
  --app="file:///work/stage2.html"
```

**Evidence:**
- `stage2.mkv` — 137,183 bytes, 1280×720, 2s (via `ffprobe`).
- `stage2.png` — an **in-cell X11 screenshot** (`import -window root -display
  :99`, ImageMagick), not a Playwright screenshot as the plan's fallback
  phrasing allows — pixel-checked at the button's known centre (240,140):
  `srgb(0,200,60)`, exactly the authored colour. This is a *pixel-level*, not
  eyeballed, proof the page rendered.

**One real bug found and fixed:** the first attempt launched Chrome with a
bare URL (no `--app=`), which opens the normal browser chrome (tabs, address
bar, an "only for automated testing" infobar) — those UI elements occupy the
top ~140px, so the pixel probe at the button's *page* coordinates actually
sampled the browser's own chrome and read near-white, not the button. Fix:
`--app=<url>` (kiosk-style, no browser UI), after which page coordinates and
screen coordinates coincide, which Stage 3/4's target-resolution math depends
on. This directly confirms the plan's own architecture note (§4.4): the
dashboard actor is specified to use `--app=`, and this is exactly why.

---

## Stage 3 — Fake input via libXtst — **PASS**

**Claim:** a .NET/F# process P/Invokes `libX11`/`libXtst` to move the pointer
and click a button in the real Chromium page from Stage 2, and the click is
proven to have registered by reading the page back through Playwright
(`document.title` changed), not by inspecting pixels.

**Source:** `spike/xtest-app/{XTestApp.fsproj,Program.fs}` — a standalone F#
console app (kept isolated from the repo's solution/build; see §Notes on
repo-tree interactions below for why an explicit `ManagePackageVersionsCentrally=false`
and an explicit `FSharp.Core` package reference were required). It:
1. `XOpenDisplay(null)` against `$DISPLAY` (the cell's `:99`).
2. Launches the bundled Chromium via `Microsoft.Playwright`
   (`LaunchPersistentContextAsync`, `--app=<url>`, matching Stage 2).
3. Delivers the click via `XTestFakeMotionEvent` → `XFlush` → sleep →
   `XTestFakeButtonEvent(press)` → `XFlush` → sleep →
   `XTestFakeButtonEvent(release)` → `XFlush` — **not** Playwright's own
   `.ClickAsync()`.
4. Reads `page.TitleAsync()` before and after; the fixture's `onclick` sets
   `document.title = 'clicked'`.

**Script:** `spike/stage3-xtest-input.sh` — publishes the app self-contained
(`dotnet publish -r linux-x64 --self-contained true`, so the cell needs no
dotnet SDK/runtime bound in at all) and runs it inside the same bwrap shape as
Stage 2, plus a 4s `ffmpeg` recording for evidence.

**Evidence:**
- Cell exit code 0; `xtestapp.log`:
  ```
  OK: XOpenDisplay succeeded, default screen=0
  OK: page loaded, title before click =
  OK: XTestFakeButtonEvent delivered at (240,140)
  OK: page title after click = clicked
  PASS: XTest-delivered click registered with the real Chromium page
  ```
- `stage3.mkv` — 276,378 bytes.
- `xtestapp.stderr.log` — empty (no exceptions).

**Bugs found and fixed (all in the build/packaging path, none in the X11
logic itself once the FSharp.Core issue was found):**
1. **`-p:PublishSingleFile=true` silently drops FSharp.Core.** The first
   self-contained single-file publish produced a binary that `SIGABRT`ed on
   startup with `System.IO.FileNotFoundException: Could not load ... FSharp.Core`.
   The single-file bundler under this SDK/RID combination does not include
   it. Fix: publish as a normal self-contained **folder** (no
   `PublishSingleFile`) — `FSharp.Core.dll` is then present alongside the
   apphost. This is a real, reproducible SDK behavior worth knowing before
   Phase 4 designs the tool's actual distribution format around single-file
   publishing.
2. **Central Package Management (repo-tree interaction).** Once the spike's
   source lived under `SageFs.Demos/spike/` inside the repo working tree
   (rather than a scratch `/tmp` directory), `dotnet publish` picked up the
   repo's `global.json` (pins SDK `10.0.100`, `rollForward: latestFeature` →
   resolves to the installed `10.0.401`) and `Directory.Packages.props`
   (central package management, which pins `FSharp.Core` to
   `11.0.101-preview7...` for the repo's own net10-targeting projects). With
   `ManagePackageVersionsCentrally` turned off locally for the spike project
   (needed so `Microsoft.Playwright`'s version could be pinned without
   touching the shared `Directory.Packages.props`), the F# SDK's *implicit*
   FSharp.Core reference logic ends up with an empty
   `$(FSharpCoreImplicitPackageVersion)` — the CPM import happens before the
   project's own opt-out property takes effect — and the self-contained
   publish shipped with **no** `FSharp.Core.dll` at all (confirmed via
   `project.assets.json` having zero FSharp.Core entries), which is the *same*
   `FileNotFoundException`/`SIGABRT` as bug #1, but from a different root
   cause. Fix: add an explicit
   `<PackageReference Include="FSharp.Core" Version="10.1.401" />` to the
   spike's `.fsproj`, sidestepping the implicit-reference path entirely. This
   is purely a spike-isolation artifact (a real Phase 1+ scenario-planner
   project, wired into the actual solution, would just use the repo's normal
   CPM-managed FSharp.Core and never hit this) but it cost real debugging time
   and is worth remembering: **a throwaway project nested under the repo tree
   inherits `global.json`/CPM whether or not it's referenced by the `.slnx`.**
3. **`abort()` on unhandled exceptions.** While chasing bug #2, an unhandled
   exception in `main` (wrapped only in an `async {}` computation, not a
   top-level `try/with`) caused the CLR to call `abort()` — SIGABRT + a core
   dump — rather than exit cleanly. This is now fixed at the source (see
   §Crash-safety below): `main` wraps `run argv` in `try/with` and reports a
   `FAIL:`/non-zero exit instead of ever letting an exception reach the
   runtime's default unhandled-exception path.

---

## Stage 4 — The real thing — **PASS**

**Claim:** a real SageFs daemon runs inside the cell; its real dashboard is
opened in the in-cell Chromium; a libXtst click on the *actual*
`data-testid="quick-start"` control (the real one, `SageFs/DashboardFragments.fs:542`)
creates a session, observed via Playwright waiting for
`data-testid="session-card"` to appear; the whole exchange is recorded; and
the control/data boundary from §4.1 is exercised literally — a tiny runner
pipes a one-line `ScenarioPlan` JSON into the cell-agent's stdin and reads a
`StepLog` JSON back on its stdout, with nothing else crossing the sandbox
wall except the `/out` bind mount for the video file.

**Source:** `spike/cell-agent/{CellAgent.fsproj,Program.fs}` — the one process
`bwrap` runs as the sandbox's pid 1. It reads exactly one line of
`ScenarioPlan` JSON from `Console.In`, does the same
XOpenDisplay→Playwright-launch→resolve-target→XTest-click→Playwright-observe
sequence as Stage 3 (generalized: the click target is resolved via
`page.Locator(selector).BoundingBoxAsync()` — a real semantic-target-to-
screen-rect resolution, not a hardcoded coordinate, matching §4.3's
`Targets`/`ResolvedTarget` design), and writes exactly one line of `StepLog`
JSON to `Console.Out`.

**Script:** `spike/stage4-real-thing.sh`. Key shape:
```bash
# Runner side (outside every namespace):
DOTNET_DIR="$(dirname "$(readlink -f "$(command -v dotnet)")")"
bwrap ... \
  --ro-bind <chromium> /chrome-bin \
  --ro-bind <cellagent publish dir> /cellagent-bin \
  --ro-bind <SageFs/bin/Release/net10.0> /sagefs-bin \
  --ro-bind "$DOTNET_DIR" /dotnet-root \
  --bind <hostout> /out \
  -- /bin/sh -c '
    Xvfb :99 ... &
    SAGEFS_DATA_DIR=/home/demo/.sagefs SAGEFS_BIND_HOST=127.0.0.1 DOTNET_ROOT=/dotnet-root \
      /dotnet-root/dotnet /sagefs-bin/SageFs.dll --mcp-port 47749 --no-watch --no-resume &
    # poll /health, then:
    ffmpeg -f x11grab ... -t 6 /out/stage4.mkv &
    /cellagent-bin/cellagent          # reads stdin, writes stdout - THE BOUNDARY
  ' \
  < ScenarioPlan.json \
  > "$HOSTOUT/steplog.json"
```

**Evidence:**
- `steplog.json` (the exact bytes that crossed the boundary back out):
  ```json
  {"scenarioId":"hello-dashboard","outcome":"Passed","clickedAt":[251,317],"startedMs":0,"endedMs":2937,"message":"clicked (251,317), '[data-testid=session-card]' appeared"}
  ```
- `stage4.mkv` — 639,618 bytes, 6s. A frame at ~1.4s
  (`ffmpeg -vf select=eq(n\,20)`) shows the real dashboard's "Start a Session"
  picker rendered inside the cell with the cursor approaching the Quick Start
  card — i.e. the actual product UI, served by a daemon that was built from
  this worktree's current source and is running inside the sandbox, not a
  fixture.
- `daemon.log` (tail) shows a normal warmup/shutdown cycle:
  `[watcher] Registered file watcher...`, `Saved test cache to
  /home/demo/.sagefs/cache/....sagetc`, `Shutdown manifest save: saved session
  manifest to /home/demo/.sagefs/daemon.sagefm`, `Daemon stopped` — all paths
  are under the cell's own tmpfs `/home/demo/.sagefs`, never the host's real
  `~/.SageFs`.
- The click landed at `(251,317)`, which `BoundingBoxAsync()` resolved from
  the live page — this was **not** hand-typed; it fell out of the same
  Quick-Start card position visible in the frame above.
- Cell exit code 0; `[ "$ok" = "1" ]` gate in the script passed both the
  StepLog-Passed check and the video-file-exists check.

**One real bug found and fixed, not X11/input-related — a genuine
architectural finding for Phase 1+:**

- **`page.WaitForLoadStateAsync(LoadState.NetworkIdle)` never resolves against
  this dashboard, and times out at 30s.** Root cause: the dashboard is a
  Datastar/SSE application by design — `Dashboard.fs` keeps a long-lived
  `EventSource` connection to `/dashboard/stream` open for the life of the
  page, so Chromium's network activity is *never* idle. `NetworkIdle` is
  fundamentally the wrong load-state to wait for on any SSE-driven page. Fix:
  wait for `LoadState.Load` instead (fires once, on the initial document
  load, unaffected by the long-lived stream). **This is a real fact about
  demo-gif-plan.md's own target application that Phase 1's `Actor` design
  needs to encode as a rule, not a one-off fix**: every actor that drives a
  Datastar-based SageFs surface (the dashboard is the only one today, but the
  pattern will repeat) must never wait on `NetworkIdle`.

**One version-skew red herring, worth recording so it isn't re-debugged:**
early attempts pointed the cell-agent at the **globally-installed** `sagefs`
dotnet tool (`~/.dotnet/tools/sagefs`, version `0.6.483-local4`, a locally
packed build from *before* this session's `data-testid` commit
(`03eaafe8`) was merged in). Its dashboard HTML genuinely contains
`Quick Start` text but **zero** `data-testid` attributes anywhere in the DOM
— confirmed live via `page.EvaluateAsync("document.getElementById('main').outerHTML.indexOf('data-testid')")`
returning `-1` for over 4 seconds while the button was fully visible on
screen. This produced a confusing "the element visibly exists but the
Locator can never find it" symptom that looked like a Playwright/X11
oddity but was pure version skew between the installed tool and the current
worktree. Fix: build the daemon from source
(`dotnet build SageFs/SageFs.fsproj -c Release`, ~43s) and run
`SageFs/bin/Release/net10.0/SageFs.dll` directly instead of the stale global
tool. **Lesson for Phase 1+:** the demo tool must always build/run the daemon
from the checkout under test, never assume the globally-installed `sagefs`
is current — which the plan's own §8 packaging section already implies
(`sagefs-demos` is its own tool, independent of `sagefs`) but this spike is
the first place that assumption actually mattered.

---

## Cleanup verification

After every stage and again at the end of the whole run:
```
ps aux | grep -E "Xvfb|bwrap|chrome|ffmpeg|SageFs.dll|cellagent|xtestapp"
ss -tlnp | grep -E "4774|4775|3774|3775"
coredumpctl list --since="10 minutes ago"
curl -s http://127.0.0.1:37749/health   # the user's REAL daemon
```
Results: no Xvfb/bwrap/chrome/ffmpeg/SageFs/cellagent/xtestapp process from
this spike survives any stage (only pre-existing, unrelated processes —
Steam's own bwrap sandbox, the user's browser/Slack — ever match these
greps); no port in the `47xxx` range is listening; zero core dumps in the
final, working runs; and the user's real daemon on `37749`/`37750` was
queried only read-only (`/health`) and remained untouched throughout,
still healthy and serving its pre-existing session at the end.

### Notes on debugging (the 3 crashes that *did* happen, and why that's fine)

Per the operator's correction mid-session: crash visibility was **not**
suppressed. Three real crashes happened while finding the right flags (Xvfb's
EGL segfault in Stage 1 before `LIBGL_ALWAYS_SOFTWARE`/`__EGL_VENDOR_LIBRARY_FILENAMES`
were added; `xtestapp`'s `FileNotFoundException`→`abort()` in Stage 3 before
the FSharp.Core fixes; one dotnet SIGABRT from an unrelated concurrent
process in this same multi-agent session). Each is root-caused above, not
patched over with `ulimit -c 0` or `DOTNET_DbgEnableMiniDump=0` — those
suppressions were added, then explicitly reverted, per the correction. The
lasting fix in this codebase is `Program.fs`'s top-level `try/with` in both
`xtest-app` and `cell-agent`: from that point on, every remaining run in this
report exited cleanly (0 or 1) with a `FAIL:`/`PASS:` line, never an
uncaught-exception `abort()`.

---

## Verdict

**Go.** All four Phase-0 stages pass, including Stage 4 (nothing stopped at
"if this cannot stand up in ~a day, stop and rethink" — the whole chain
stood up in one session, well inside that budget). The two riskiest
unknowns named in §10 — the sandbox control/data-plane boundary, and the
libXtst input edge — are both proven, not just individually but composed
together against the real product (a real daemon, the real dashboard
markup, the real `data-testid` contract) rather than only the throwaway
fixture page. Concretely de-risked for Phase 1+:

- `bwrap --uid 0 --gid 0` + pre-created `/tmp/.X11-unix` + forced Mesa EGL is
  the complete, working Xvfb-in-bwrap recipe on this machine (Arch,
  NVIDIA GPU) — codify these exact flags in `Isolation.Bubblewrap`, they are
  not optional extras.
- `--app=<url>` Chromium is required for the page-coordinate-equals-screen-
  coordinate assumption Stage 3/4's target resolution depends on — already
  in the plan's §4.4, now empirically confirmed necessary.
- `libXtst` P/Invoke works exactly as designed against the *system* library
  on this machine; the bundling machinery in §8 is still worth building for
  portability to machines that lack it, but is not gating Phase 0/1 here.
- Self-contained folder publish (not single-file) is the correct packaging
  shape for anything P/Invoking native libraries in this toolchain; revisit
  before Phase 4 commits to a distribution format.
- `LoadState.NetworkIdle` must never be used against SageFs's Datastar/SSE
  surfaces — codify as a rule for every `Actor` in Phase 1's design, not a
  per-scenario workaround.
- The demo tool must always drive a daemon built from the checkout under
  test, never the globally-installed `sagefs`.

Nothing here suggests the cell/actor/boundary architecture in §4 needs
rethinking. The friction encountered was entirely in flag-tuning and
build/packaging edges — exactly the class of thing a foundation spike exists
to surface cheaply, before Phase 1 builds real planners on top of it.
