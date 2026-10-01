#!/usr/bin/env bash
# Task setup, run by run-lemming-cmd on the fresh copy BEFORE the baseline commit, so the
# lemming's own diff is only its work. Seeds one off-by-one into RingBuffer.tryGet: the existing
# test "age beyond count returns None" (SageFs.Tests/RingBufferTests.fs) then fails, and the
# fix is one character in the one named file. The checkout it is applied to is never touched:
# this only ever runs on the disposable copy under /tmp/lem/<run-id>/w.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
source "$HERE/../lib-cmd.sh"
WORKDIR=${1:?workdir}
lem_tool replace-exact --file "$WORKDIR/SageFs.Core/RingBuffer.fs" \
  --find 'match age >= 0 && age < buf.Count with' \
  --replace 'match age >= 0 && age <= buf.Count with'
