# SageFs CI/CD Plan

## Current State

Three workflow files exist but are **partially broken**:

| File | Status | Issues |
|------|--------|--------|
| `main.yml` | ⚠️ Partial | Uses `dotnet test --no-build` (Expecto needs `dotnet run`); no integration tests; no caching |
| `publish.yml` | ❌ Broken | References `SageFs.Cli/`, `SageFs.Cli.Web/`, `SageFs.Daemon/` — projects that no longer exist; changelog reader expects `CHANGELOG.md` which doesn't exist |
| `copilot-setup-steps.yml` | ⚠️ Partial | Has `npx run build` (should be `dotnet build`); good Playwright install step |

### Native dependency gap

Only `runtimes/win-x64/native/tree-sitter-fsharp.dll` exists. Linux/macOS CI runners cannot run syntax highlighting tests until cross-platform natives are added.

---

## Recommended Architecture

### Two workflows, three jobs

```
┌──────────────────────────────────────────────────────────┐
│  ci.yml  (push to master, all PRs)                       │
│                                                           │
│  ┌────────────────────┐    ┌───────────────────────────┐  │
│  │  build-and-test     │    │  integration              │  │
│  │  (matrix: win+linux)│───▶│  (ubuntu only, needs      │  │
│  │  restore, compile,  │    │   Docker for Testcontainers│  │
│  │  unit tests, pack)  │    │   + Playwright browser)    │  │
│  └────────────────────┘    └───────────────────────────┘  │
└──────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────┐
│  publish.yml  (tag v*.*.*)                                │
│                                                           │
│  ┌────────────┐  ┌─────────────┐  ┌────────────────────┐ │
│  │ build+test  │─▶│  pack+sign  │─▶│  publish to NuGet  │ │
│  │             │  │  (Release)  │  │  + GitHub Packages  │ │
│  │             │  │             │  │  + GitHub Release    │ │
│  └────────────┘  └─────────────┘  └────────────────────┘ │
└──────────────────────────────────────────────────────────┘
```

---

## Design Decisions

### 1. CI Platform: **GitHub Actions**

Already on GitHub, excellent .NET support, free minutes for public repos, OIDC NuGet publishing already in use.

### 2. OS Matrix: **Windows + Ubuntu + macOS**

- **Windows**: Full test suite including tree-sitter syntax highlighting
- **Ubuntu**: Unit tests (skip tree-sitter-dependent), integration tests (Testcontainers + Playwright)
- **macOS**: Unit tests (skip tree-sitter-dependent until native is built)
- Repo is **public** so all runner minutes are free — no reason to skip macOS

### 3. Test runner: **`dotnet run --project SageFs.Tests`** (not `dotnet test`)

Expecto uses custom `--all`/`--integration` filtering via `[<Tests>]` attributes. `dotnet test` with TestSdk adapter works for discovery but doesn't support the custom filter flags. Use `dotnet run` for full control.

### 4. Postgres in CI: **Testcontainers** (not docker-compose)

Already implemented — each test gets isolated, disposable Postgres. Docker pre-installed on `ubuntu-latest`. Zero CI-specific config needed.

### 5. Playwright browser tests in CI

Both F# Playwright (.NET) and TS Playwright tests need a running SageFs daemon:
1. Pack and install SageFs as global tool
2. Start daemon in background (`sagefs --daemon &`)
3. Health-check `http://localhost:37750/api/daemon-info`
4. Run F# integration tests (Expecto `--all`)
5. Run TS Playwright tests (`npx playwright test`)

### 6. Publishing: **NuGet.org + GitHub Packages (dual)**

- NuGet.org for public discovery (`dotnet tool install --global SageFs`)
- GitHub Packages for pre-release/organizational use
- OIDC via `NuGet/login@v1` (no long-lived API keys)

### 7. Versioning: **Manual in .fsproj + tag-triggered publish**

- Source of truth: `SageFs/SageFs.fsproj` `<Version>` element
- Release: bump version → commit → `git tag v0.5.3` → push tag → CI publishes
- No GitVersion/semantic-release — overkill for current cadence
- CI preview builds: append `-ci.{run_number}` suffix for non-tag builds

### 8. GUI distribution: **Bundled (no separate artifact yet)**

SageFs.Gui is already a ProjectReference in SageFs.fsproj — `sagefs --gui` launches from the same tool. Separate self-contained executables are a future option when needed.

### 9. Docker image: **Not yet**

Users are .NET devs with the SDK. The daemon needs host filesystem access. Add Docker when remote/team daemon mode is requested.

### 10. Caching

- NuGet package cache (`~/.nuget/packages`) via `actions/cache`
- Node modules cache via `actions/setup-node` with `cache: npm`

---

## Implementation Workplan

### Phase 1: Fix `ci.yml` (P0)

Replace the broken `main.yml` with a working pipeline:

```yaml
name: CI

on:
  push:
    branches: [master]
  pull_request:

concurrency:
  group: ci-${{ github.ref }}
  cancel-in-progress: true

jobs:
  build-and-test:
    strategy:
      fail-fast: false
      matrix:
        include:
          - os: windows-latest
          - os: ubuntu-latest
          - os: macos-latest

    runs-on: ${{ matrix.os }}

    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x

      - name: Restore
        run: dotnet restore

      - name: Build
        run: dotnet build --no-restore

      - name: Unit tests
        run: dotnet run --project SageFs.Tests --no-build -- --summary

      - name: Pack (verify nupkg)
        run: dotnet pack SageFs -c Release -o nupkg --no-restore
        if: matrix.os == 'ubuntu-latest'

      - name: Upload nupkg
        uses: actions/upload-artifact@v4
        with:
          name: nupkg
          path: nupkg/*.nupkg
        if: matrix.os == 'ubuntu-latest'

  integration:
    needs: build-and-test
    runs-on: ubuntu-latest

    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x

      - uses: actions/setup-node@v4
        with:
          node-version: lts/*
          cache: npm

      - name: Build
        run: dotnet build

      - name: Install as global tool
        run: |
          dotnet pack SageFs -c Release -o nupkg
          dotnet tool install --global SageFs --add-source ./nupkg --no-cache

      - name: Start daemon
        run: |
          sagefs --daemon --proj SageFs.Tests/SageFs.Tests.fsproj &
          for i in $(seq 1 30); do
            curl -sf http://localhost:37750/api/daemon-info && break
            sleep 2
          done

      - name: Integration tests (Expecto + Playwright .NET)
        run: dotnet run --project SageFs.Tests --no-build -- --all --summary

      - name: Install Playwright browsers
        run: npx playwright install chromium --with-deps

      - name: npm ci
        run: npm ci

      - name: TypeScript Playwright tests
        run: npx playwright test
        env:
          CI: true

      - name: Upload Playwright report
        uses: actions/upload-artifact@v4
        if: failure()
        with:
          name: playwright-report
          path: playwright-report/
```

### Phase 2: Fix `publish.yml` (P0)

Fix broken artifact paths and remove non-existent project references:

- `SageFs.Cli/nupkg/` → `SageFs/nupkg/`
- Remove `SageFs.Cli.Web/nupkg/` and `SageFs.Daemon/nupkg/`
- Remove changelog reader (no `CHANGELOG.md` exists) — use `generate_release_notes: true` instead
- Add build+test step before publish

### Phase 3: Fix `copilot-setup-steps.yml` (P1)

- Replace `npx run build` with `dotnet build`
- Add `dotnet restore` step
- Add `setup-dotnet` with 10.0.x

### Phase 4: Cross-platform tree-sitter (P3 — future)

Build `tree-sitter-fsharp` native libs for linux-x64 and osx-x64, add to `runtimes/` directory. Until then, syntax highlighting tests are Windows-only in CI.

---

## What NOT to Do

- ❌ GitVersion / semantic-release — overkill for current release cadence
- ❌ Docker image — users are .NET devs with SDK access
- ❌ docker-compose in CI — Testcontainers is superior
- ❌ Separate GUI artifact — already bundled in the tool

---

## Priority Summary

| Priority | Action | Effort |
|----------|--------|--------|
| P0 | Rewrite `ci.yml` with working build+test+integration | Medium |
| P0 | Fix `publish.yml` broken paths and missing changelog | Small |
| P1 | Fix `copilot-setup-steps.yml` | Small |
| P2 | Add NuGet/dotnet restore caching | Small |
| P3 | Cross-platform tree-sitter natives | Medium |
