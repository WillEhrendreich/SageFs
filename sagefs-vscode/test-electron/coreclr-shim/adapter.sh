#!/bin/sh
# The debug adapter the shim contributes: netcoredbg speaking the Debug Adapter Protocol on stdio.
exec netcoredbg --interpreter=vscode "$@"
