#!/usr/bin/env bash
cd /home/will/Work/SageFs
DLL=SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll
timeout 400 dotnet "$DLL" --integration-host \
  --filter-test-case "editing a compiled F# file reruns tests against rebuilt output without an explicit rerun" \
  --summary 2>&1 | sed 's/\x1b\[[0-9;]*m//g' \
  | grep -E 'TRUST|EXPECTO!|baseline run should pass|Queued' | head -20
