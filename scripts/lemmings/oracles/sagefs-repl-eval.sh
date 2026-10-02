#!/usr/bin/env bash
# Oracle for sagefs-repl-eval (fixture sagefs-copy): ANSWER.md exists with the four right values,
# and no source file was edited. The expected values come from running SageFs.RingBuffer for
# real: capacity 4, pushes 10..60 leave [60; 50; 40; 30], tryGet 2 is Some 40, evictedCount is 2,
# and the session host runs on .NET 10 or 11.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
source "$HERE/../lib-cmd.sh"
RUN=${1:?run-dir}
W=$RUN/w

edited=$(git -C "$W" diff --name-only lem-baseline || true)
if [ -n "$edited" ]; then
  echo "the task said not to change source files, but these changed:"
  echo "$edited"
  exit 1
fi

lem_tool expect --file "$W/ANSWER.md" \
  --pattern '^\W*toList\W*=.*60\D+50\D+40\D+30\D*$' \
  --pattern '^\W*tryGet 2\W*=\W*(Some\W*)?40\W*$' \
  --pattern '^\W*evictedCount\W*=\W*2\w?\W*$' \
  --pattern '^\W*runtimeMajor\W*=\W*(10|11)\W*$'
