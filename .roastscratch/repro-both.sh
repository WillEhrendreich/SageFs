#!/usr/bin/env bash
cd /home/will/Work/SageFs
DLL=SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll
timeout 400 dotnet "$DLL" --integration-host \
  --filter-test-case "list_tests, explain_test_failure and diagnose report what live testing actually found" \
  --summary 2>&1 | sed 's/\x1b\[[0-9;]*m//g' \
  | grep -E 'TRUST|EXPECTO!|baseline run should pass' | head -20
