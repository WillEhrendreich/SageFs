#!/usr/bin/env bash
# Oracle for parse-seed (fixture demoenv): the project's own Expecto suite must pass, run in the
# lemming's sandbox without network, and the lemming must not have touched the tests.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
source "$HERE/../lib-cmd.sh"
RUN=${1:?run-dir}
W=$RUN/w

touched=$( { git -C "$W" diff --name-only lem-baseline; git -C "$W" ls-files --others --exclude-standard; } | grep '^DemoEnv.Tests/' || true)
if [ -n "$touched" ]; then
  echo "the lemming changed the tests instead of the code:"
  echo "$touched"
  exit 1
fi

raw=$(lem_sandbox_exec "$RUN" 600 dotnet run --project DemoEnv.Tests 2>&1) && rc=0 || rc=$?
out=$(printf '%s' "$raw" | lem_strip_ansi)
echo "$out" | tail -n 15
[ "$rc" -eq 0 ] || { echo "the test suite exited $rc"; exit 1; }
echo "$out" | grep -Eq '[1-9][0-9]* passed, [0-9]+ ignored, 0 failed, 0 errored' \
  || { echo "the suite exited 0 but no passing summary line was found, so nothing is proven"; exit 1; }
echo "the DemoEnv suite passes"
