#!/usr/bin/env bash
# Oracle template. Copy to scripts/lemmings/oracles/<task>.sh and chmod +x (an oracle that is not
# executable is skipped, and a skipped oracle makes the run a HarnessError, never a Pass).
#
# The harness runs  oracles/<task>.sh <run-dir>  OUTSIDE the sandbox, after the lemming has
# finished and its sessions have been cleared from the shared daemon. Exit 0 means the lemming
# did the job; anything else is a failure, and what you print is kept as out/oracle.out and
# quoted in summary.json. The oracle is the verdict. The model's own account never is.
#
# What you have:
#   <run-dir>/w                  the lemming's working directory (a git repo; tag lem-baseline is
#                                the state it started from)
#   <run-dir>/out/events.ndjson  cmdc's event stream
#   lem_sandbox_exec <run-dir> <seconds> cmd args...
#                                runs a command in the lemming's sandbox with no network and no
#                                credentials. Use it for ANYTHING that executes code the lemming
#                                wrote (a test run). Compiling is fine outside it.
#   lem_tool expect --file F --pattern RE ...   checks a written answer file
#
# Logic beyond a few lines goes in F# (a small project beside this script, see RingBufferOracle),
# not in here.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
source "$HERE/../lib-cmd.sh"
RUN=${1:?run-dir}
W=$RUN/w

echo "template oracle: replace me"
exit 1
