# Runner environment: `npm` is not on the self-hosted runner's PATH

> **Status on 2026-10-01: fixed, and kept as a record.** The "What is needed" section below asked for a fix on the runner. It landed in `.github/workflows/main.yml` on 2026-09-29 instead (commits `110cd4b4`, `2716bde0`, `82c258d8`): a step finds a node install that has npm, from `NUGET_NODE_BIN` or the machine's mise install tree, and puts it on the path for the extension stages, printing what it searched when it finds none. Nothing in this page is a user problem. It is a note about my own CI machine, and it would sit better under `docs/internal/`.

The `0.6.838` push failed the CI gate at **stage 5, `vscode extension compile`**,
not in a test tier:

    System.ComponentModel.Win32Exception: An error occurred trying to start process
    ── STAGE #5 vscode extension compile finished. 162ms. ──

162 ms is a process that never started, not a compile that failed. Stages 0-4 all
passed, including `build samples for integration suites` — the stage the
clean-checkout build fix repaired.

## Measured

    $ env -i /bin/sh -c 'which node npm'
      node/npm NOT on a bare PATH

    /usr/bin/node            v22.23.2     (present)
    /usr/bin/npm             MISSING
    /usr/local/bin/npm       MISSING
    mise node 26.7.0/bin/npm present      (NOT on the runner's PATH)

So `npm run compile` cannot spawn anything: the runner resolves `node` to an old
`/usr/bin/node` that has no `npm` beside it, while the `npm` that does exist lives
under `mise`, which the runner's PATH does not include.

## This is a machine regression, not a code change

- The last **successful** gate was `0.6.834` on 2026-09-26.
- `/usr/bin/node` dates from 07-29; the `mise` node tree from 09-14.
- Nothing in this release touches `sagefs-vscode`, `ci-pipeline.fsx` stage 5, or
  the runner's PATH.

## Verified locally that the stage itself is fine

    $ cd sagefs-vscode
    $ dotnet tool restore   -> Restore was successful.
    $ npm run compile       -> Fable compilation finished in 4337ms
                                ✅ esbuild: dist/Extension.js

## What is needed

A fix on the runner, not in the repo: add the `mise` node bin to the PATH the
runner uses (or set it in the runner's `.env` alongside `DOTNET_ROOT`). The
existing `main.yml` step already does this for `DOTNET_ROOT` and emits a clear
error when it is missing; `npm` needs the same treatment, and the stage should
fail with "npm not found" rather than a bare `Win32Exception`.
