# SageFs and `dotnet watch`

People keep asking how this differs from `dotnet watch`, and "watch mode, but faster" is a fair way to put the question. The short version is that `dotnet watch` rebuilds when you save, and SageFs keeps your project loaded in a session, so it can answer before a build would finish. What follows says where each one wins. Every claim links to the page or the test it comes from.

## Re-running tests

| | `dotnet watch test` | SageFs live testing |
|:---|:---|:---|
| What triggers it | A file save | A save, or a keystroke before you save, in VS Code and Neovim (VS Code waits 300 ms after your last edit, then sends the whole buffer) |
| What runs | The test command you gave it, so every test unless you filter | The tests the edit can reach, worked out from the symbols the test and the code share and from per-test coverage. When it cannot tell, it runs all of them rather than none ([how the choice is made](how-live-testing-works.md)) |
| What it runs against | A build of your saved files | Code evaluated in the live session, and a real build of the saved text confirms or contradicts it once you stop typing for two seconds. Each result says which one it ran against |
| Speed | I have not timed it | Keystroke to verdict p50 712 ms, save to all green p50 542 ms. That is 11 tests on one machine, so read it as "fast on a small project". I have no figure for a large solution ([the numbers and their limits](how-live-testing-works.md)) |
| Where you see it | The terminal | Marks in your editor, the web dashboard, or an MCP client. There is no plain-terminal pass/fail printout yet |

A type error in the buffer means nothing is evaluated and nothing runs. The last results stay up, marked as blocked by compile errors ([live-testing-as-you-type.md](live-testing-as-you-type.md)).

## Updating the running app

| | `dotnet watch run` | SageFs hot reload |
|:---|:---|:---|
| C# | Applies the change to the running process | SageFs's hot reload is built for F# |
| F# | Restarts the app. The SDK tracks this in [sdk#55464](https://github.com/dotnet/sdk/issues/55464) and the F# compiler team has [work in progress](https://github.com/dotnet/fsharp/issues/19941) | Re-points the changed methods in the process that is already running and keeps its state. It says when a change needs a restart and why ([how hot reload works](how-hot-reload-works.md)) |

The F# row is as of 2026-10-01 and will go stale when Microsoft ships F# support. The comparison in [how-hot-reload-works.md](how-hot-reload-works.md) is the one I keep current.

## When `dotnet watch` is the better pick

- Your app is small, a rebuild takes a second or two, and there is no state worth keeping.
- It is a C# project, where `dotnet watch` hot reload is first-party and works.
- You want nothing extra on the machine. `dotnet watch` ships in the SDK. SageFs is a daemon plus a client, and it runs your code, so it asks for some trust.

## When SageFs earns its place

- The suite is big enough that re-running all of it on each save is the slow part.
- Restarting the app costs you state: a logged-in session, a loaded dataset, a game in progress.
- You want an agent in the same session you are looking at, running the same tests you see.

Something here wrong or out of date? Tell me in an [issue](https://github.com/WillEhrendreich/SageFs/issues) or in the F# Discord.
