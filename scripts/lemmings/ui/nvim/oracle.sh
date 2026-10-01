#!/usr/bin/env bash
# scripts/lemmings/ui/nvim/oracle.sh <task> <run-dir>
#
# The shared body of the ui-* oracles. Runs OUTSIDE the sandbox after the lemming has finished.
# For tasks that change code, the project's own Expecto suite runs first, in the lemming's
# sandbox without network (lem_sandbox_exec). Then the F# oracle (LemDrive nvim oracle) checks
# the editor evidence. Exit 0 only when every check passes.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
source "$HERE/../../lib-cmd.sh"
TASK=${1:?task} RUN=${2:?run-dir}
DRIVE=$RUN/bin/lemdrive/LemDrive.dll
PLUGIN=$(cat "$RUN/out/ui/plugin-dir" 2>/dev/null || echo "$HOME/Work/sagefs.nvim")

case $TASK in
  ui-edit-reeval|ui-live-tests)
    out=$(lem_sandbox_exec "$RUN" 600 dotnet run --project DemoEnv.Tests 2>&1) && rc=0 || rc=$?
    echo "$out" | tail -n 8
    [ "$rc" -eq 0 ] || { echo "FAIL  the DemoEnv suite exited $rc"; exit 1; }
    echo "$out" | grep -Eq '[1-9][0-9]* passed, [0-9]+ ignored, 0 failed, 0 errored' \
      || { echo "FAIL  the suite exited 0 but printed no passing summary, so nothing is proven"; exit 1; }
    echo "PASS  the DemoEnv suite passes"
    ;;
esac

DOTNET_NOLOGO=1 dotnet "$DRIVE" nvim oracle "$TASK" --run "$RUN" --plugin "$PLUGIN"
