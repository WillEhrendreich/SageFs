#!/usr/bin/env bash
# Runs scripts/machine-bench.fsx once per emulated machine tier on THIS Linux machine, to
# get a curve rather than one point. Each tier is a systemd user scope (cpu quota, memory
# cap, no swap) plus a taskset CPU list. Nothing needs root. Everything the benchmark
# starts inherits the limits.
#
#   scripts/machine-bench-tiers.sh <dir holding SageFs.dll> <output dir> [runs] [cold-runs] [mode]
#
# mode is `defaults` (the daemon's own timeouts, the default) or `generous` (every wait for
# the machine raised far past any tier, to see what the machine can do when no timeout binds).
#
# What this CAN emulate: fewer cores, less CPU time per core (a quota, which stalls all
# threads together, so it is a harsher shape than a slower clock), less memory with a hard
# OOM instead of swap. What it CANNOT: a slower clock, a slower disk, a cold page cache
# (dropping caches needs root). The real slow machine is the benchmark run on that machine.
#
# One tier's output is <output dir>/<tier>.txt (the table) and <tier>.json (raw samples).
# SMT siblings are skipped: on a CPU whose logical cpu N+8 shares a core with N, "4 cores"
# means cpus 0-3. Check `lscpu -e` and edit TIERS if yours is laid out differently.

set -u
SAGEFS_DIR="$1"
OUT_DIR="$2"
RUNS="${3:-5}"
COLD="${4:-3}"
MODE="${5:-defaults}"
ENV_ARGS=()
if [ "$MODE" = "generous" ]; then
  ENV_ARGS=(--env SAGEFS_WARMUP_INACTIVITY_SECONDS=1200 --env SAGEFS_WARMUP_MAX_MINUTES=60
            --env SAGEFS_FSI_HOST_STARTUP_SECONDS=1200 --env SAGEFS_DOTNET_SDK_QUERY_SECONDS=300
            --env SAGEFS_REBUILD_READY_SECONDS=1200 --env SAGEFS_WORKER_HTTP_READ_SECONDS=600
            --env SAGEFS_STOP_GRACEFUL_SECONDS=300 --env SAGEFS_SUPERVISOR_WEDGE_SECONDS=600
            --env SAGEFS_HOST_BUILD_MINUTES=30 --env SAGEFS_WARMUP_READY_POLL_SECONDS=1200
            --env SAGEFS_STOP_SESSION_TIMEOUT_SECONDS=300)
fi
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$OUT_DIR"

# name | taskset cpu list | CPUQuota ("" = none) | MemoryMax ("" = none)
TIERS=(
  "t16|0-15||"
  "t8|0-7||"
  "t4|0-3||"
  "t4q50|0-3|200%|"
  "t2|0-1||"
  "t2q50|0-1|100%|"
  "t1q50|0|50%|"
  "t4m8g|0-3||8G"
  "t4m4g|0-3||4G"
  "t4m2g|0-3||2G"
)

for spec in "${TIERS[@]}"; do
  IFS='|' read -r name cpus quota mem <<<"$spec"
  props=()
  [ -n "$quota" ] && props+=(-p "CPUQuota=$quota")
  [ -n "$mem" ] && props+=(-p "MemoryMax=$mem" -p MemorySwapMax=0)
  echo "== $name: cpus $cpus quota '${quota}' memory '${mem}'" >&2
  systemd-run --user --scope -q "${props[@]}" \
    taskset -c "$cpus" \
    dotnet fsi "$HERE/machine-bench.fsx" --sagefs "$SAGEFS_DIR" --runs "$RUNS" --cold-runs "$COLD" \
      --ready-cap 300 --label "$name-$MODE" --out "$OUT_DIR/$name-$MODE.json" "${ENV_ARGS[@]}" \
    >"$OUT_DIR/$name-$MODE.txt" 2>"$OUT_DIR/$name-$MODE.err"
done
