#!/usr/bin/env bash
# Oracle for ui-live-tests: the logic is F# (ui/nvim/NvimOracle.fs); this only hands over.
exec "$(dirname "$0")/../ui/nvim/oracle.sh" ui-live-tests "$@"
