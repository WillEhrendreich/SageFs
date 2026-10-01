#!/usr/bin/env bash
# Oracle for the smoke task: the stream must show a get_daemon_status call that completed.
# (The task asks for one MCP call and a one-sentence answer; whether the call really happened is
# a fact of the stream, not of what the model says.)
set -euo pipefail
RUN=${1:?run-dir}
EVENTS=$RUN/out/events.ndjson
if grep -q '"type":"tool_completed".*"toolName":"mcp__sagefs__get_daemon_status"' "$EVENTS"; then
  echo "get_daemon_status completed"
  exit 0
fi
echo "no completed get_daemon_status call in the stream"
exit 1
