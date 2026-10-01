#!/usr/bin/env bash
# Oracle for sagefs-small-fix (fixture sagefs-copy). Two facts, both measured here:
#   1. the diff against the starting commit touches ONLY SageFs.Core/RingBuffer.fs
#   2. the RingBuffer test list in SageFs.Tests/RingBufferTests.fs passes against the lemming's
#      RingBuffer.fs (compiled beside it by oracles/RingBufferOracle, run without network)
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
source "$HERE/../lib-cmd.sh"
RUN=${1:?run-dir}
W=$RUN/w
NAMED=SageFs.Core/RingBuffer.fs

changed=$( { git -C "$W" diff --name-only lem-baseline; git -C "$W" ls-files --others --exclude-standard; } | sort -u)
if [ "$changed" != "$NAMED" ]; then
  echo "expected the diff to touch only $NAMED, but it touches:"
  echo "${changed:-(nothing)}"
  exit 1
fi

ART=$RUN/oracle-art
if ! build=$(dotnet build "$HERE/RingBufferOracle/RingBufferOracle.fsproj" -c Release -nologo -v quiet \
      --artifacts-path "$ART" -p:CopyRoot="$W" 2>&1); then
  echo "the lemming's RingBuffer.fs does not compile with the tests:"
  echo "$build" | tail -n 20
  exit 1
fi
DLL=$(find "$ART/bin" -name RingBufferOracle.dll -print -quit)
[ -n "$DLL" ] || { echo "oracle build produced no dll"; exit 1; }

LEM_EXTRA_BWRAP=(--ro-bind "$ART" "$ART")
raw=$(lem_sandbox_exec "$RUN" 300 dotnet "$DLL" --summary 2>&1) && rc=0 || rc=$?
out=$(printf '%s' "$raw" | lem_strip_ansi)
echo "$out" | tail -n 12
[ "$rc" -eq 0 ] || { echo "the RingBuffer tests exited $rc"; exit 1; }
echo "$out" | grep -Eq '[1-9][0-9]* passed, [0-9]+ ignored, 0 failed, 0 errored' \
  || { echo "exit 0 but no passing summary line, so nothing is proven"; exit 1; }
echo "RingBuffer tests pass and only $NAMED changed"
