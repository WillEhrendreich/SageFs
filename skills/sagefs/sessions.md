# Sessions and the first minute

Read this when you are starting on a repo, creating or choosing a session, checking whether the daemon is current, or a session is stuck warming up or Faulted.

## The first minute

1. **Is SageFs up?** Call `get_daemon_status` (or `list_sessions`). If the
   tools aren't there at all, SageFs isn't connected. Tell the user. Don't work
   around it silently.
2. **Is the daemon current?** Do this before you trust a single result. A stale
   daemon serves old code and gives you wrong answers that look right, and it
   is the most expensive failure in this whole document. See "a stale daemon" in troubleshooting.md
   under "Things that will bite you" for the disguises it wears.

   Read its version from `get_daemon_status`, `sagefs status`, or the dashboard's
   `/api/daemon-info`, and compare it against the code you're about to work on.
   In the SageFs repo itself that's `Directory.Build.props`; anywhere else it's
   "was this daemon started after the last build of this project?" If you can't
   tell, the cheap tell is whether a symbol you just added is visible in the
   session.

   If it's behind, **tell the user and ask them to restart it**. Don't stop,
   restart or reinstall it yourself. It's theirs, and other agents may be on
   it. If they ask you how: `dotnet tool update -g sagefs`, then restart. If
   that reports "already installed" while a newer version is on NuGet, pass
   `--version X.Y.Z` explicitly. `dotnet tool update` resolves through NuGet's
   search index, which lags the package store by a few minutes.
3. **Do you have a session for where you're working?** Sessions are tied to a
   working directory, and **a git worktree is its own routing boundary**. A
   session for the main checkout is not yours if you're in
   `.claude/worktrees/whatever`. Check `list_sessions` before creating one.
4. **Choose the session target explicitly.** Call `get_available_projects`,
   then use exactly one of these:
   - `create_project_session` for one `.fsproj`
   - `create_solution_session` for one `.sln` or `.slnx`
   - `create_bare_session` for a project-free REPL

   There is no auto-discovery on these tools. If generated build state is
   missing, SageFs takes a rebuild lease, runs the build itself, rechecks the
   generated files, and only then creates the session. You don't need a shell
   build before session creation, and you shouldn't race one against it.
5. **The create tool returns before warmup finishes.** Poll
   `get_session_status` for that exact session until it says `Ready`. Don't
   sleep in a loop. Warming responses carry elapsed time, work time, and the
   worker's last progress line. If it says `Faulted`, act on the reason rather
   than polling hoping it changes.
