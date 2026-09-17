# Project types SageFs can load — and what each one needs

What every conceivable .NET/F# project type requires to load and run in a SageFs
FSI session, grounded in the Microsoft MSBuild docs, with SageFs's current
handling. Three independent things must line up for a project to load: its
**shared framework** (managed assemblies FSI must reference), its **native
runtime packs** (`runtimes/<rid>/native/`), and its **kind** (which decides how
hot reload applies).

Sources: [Microsoft.NET.Sdk.Desktop MSBuild props](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props-desktop),
[.NET project SDK overview](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/overview),
[MSBuild props for the .NET SDK](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props).

## Axis 1 — shared frameworks (FrameworkReference → runtime pack)

FSI needs the managed assemblies of every shared framework the project uses.
These are NOT in the project's `bin/` — they come from `<dotnet-root>/shared/`.

| Shared framework | Enabled by | SageFs |
|---|---|---|
| `Microsoft.NETCore.App` | every project (base runtime) | implicit |
| `Microsoft.AspNetCore.App` | `Sdk="Microsoft.NET.Sdk.Web"`, `Sdk.Razor`, Blazor, or an explicit `FrameworkReference` | resolved (aspNetShared fallback in `ProjectLoading`) |
| `Microsoft.WindowsDesktop.App` (`.WPF`, `.WindowsForms`) | `<UseWPF>`/`<UseWindowsForms>` on a `-windows` TFM | resolved (WindowsDesktop added to the same fallback — **inert on Linux, needs Windows verification**) |

## Axis 2 — native runtime packs (`runtimes/<rid>/native/`)

P/Invoke'd native libraries the build deposits under the project output. FSI /
the worker must resolve them or the P/Invoke throws `DllNotFound` and (on an
unguarded thread) FailFasts the worker.

| Native dep | Projects | SageFs |
|---|---|---|
| `libraylib.so`, SDL2, etc. | games | resolved (`NativeResolution.fs` probes `runtimes/<rid>/native/`) |
| `libSkiaSharp.so`, `libHarfBuzzSharp.so` | Avalonia, Uno (Skia), MAUI | same resolver (Avalonia E2E verification in progress) |

## Axis 3 — ProjectKind (decides hot-reload behavior)

| Kind | Detected by | Reload |
|---|---|---|
| **Web** | `Sdk.Web`, `FrameworkReference Microsoft.AspNetCore.App`, or packages Falco/Giraffe/Saturn | method-detour + DevReload middleware + browser SSE |
| **NativeGui** | properties `UseWPF`/`UseWindowsForms`/`UseMaui`/`UseWinUI`, or packages `Microsoft.WindowsAppSDK`/`Microsoft.Maui.*`/`Avalonia`/`Uno.*`/`Raylib`/`SDL2`/`Silk.NET`/`MonoGame`/`SFML` | frame/render-loop detour; **no** WebApplication patch |
| **Console** | default | method-detour only |

Key subtlety: WPF/WinForms have **no package** — they are MSBuild properties.
`classifyProject` surfaces the active `Use*` properties as classification
markers so they still classify as NativeGui.

## The per-project-type table

| Project type | Enabled by | Shared framework | Native deps | ProjectKind | Runs on Linux? | SageFs status |
|---|---|---|---|---|---|---|
| Console / library | `Sdk` default | NETCore.App | none | Console | yes | works |
| ASP.NET Core / Falco / Giraffe / Saturn | `Sdk.Web` or AspNetCore FrameworkRef or web package | + AspNetCore.App | none | Web | yes | works (web reload proven) |
| Worker service | `Sdk.Worker` | NETCore.App | none | Console | yes | works |
| Blazor WASM | `Sdk.BlazorWebAssembly` | AspNetCore.App | none | Web | yes | loads; reload path not characterized |
| Game (Raylib/SDL/Silk.NET/MonoGame) | game package | NETCore.App | `runtimes/<rid>/native/` | NativeGui | yes | native load fixed; hosting works |
| **Avalonia / Uno (Skia)** | `Avalonia`/`Uno.*` package | NETCore.App | SkiaSharp/HarfBuzz native | NativeGui | **yes** | classify ✓; native load via resolver (E2E verifying) |
| **WPF** | `<UseWPF>` + `-windows` TFM | + WindowsDesktop.App(.WPF) | none | NativeGui | no (Windows-only) | classify ✓ (property marker); framework resolve added — **needs Windows verification** |
| **WinForms** | `<UseWindowsForms>` + `-windows` TFM | + WindowsDesktop.App(.WindowsForms) | none | NativeGui | no (Windows-only) | same as WPF |
| **WinUI / Windows App SDK** | `Microsoft.WindowsAppSDK` (+ `UseWinUI`) | NETCore.App | WinAppSDK native | NativeGui | no (Windows-only) | classify ✓; Windows-only, unverified |
| **MAUI** | `<UseMaui>` / `Microsoft.Maui.*` | platform-specific | SkiaSharp etc. | NativeGui | Win/Mac/mobile, not Linux desktop | classify ✓; not runnable here |
| Test project | test package or `IsTestProject` | matches host | none | (Test role) | yes | works (all 5 frameworks proven) |

## What still needs a Windows box to confirm

The WPF/WinForms path is correct by construction (property-based classification +
the `Microsoft.WindowsDesktop.App` shared-framework resolution mirror the proven
ASP.NET Core handling, fail-closed on non-Windows). It cannot be exercised on
Linux. On a Windows checkout, verify: a `<UseWPF>` project targeting
`net10.0-windows` creates a session that reaches Ready (the WindowsDesktop
assemblies resolve) and its `Use*` property makes it classify as `native-gui`.
`EnableWindowsTargeting=true` lets such a project *build* on Linux but it still
only *runs* on Windows.
