#!/bin/bash
# usage: resolve.sh <fd log> <perf map directory>
# Prints each EBADF-CLOSE / VICTIM / LOWFD event with its JIT frames named from the runtime's perf map for that pid.
log=$1; dir=$2
awk -v dir="$dir" '
function hex(s,   i, c, n, d) { n = 0; s = tolower(s); sub(/^0x/, "", s); for (i = 1; i <= length(s); i++) { c = substr(s, i, 1); d = index("0123456789abcdef", c) - 1; n = n * 16 + d } return n }
function load(pid,   f, line, a, start, size) {
  if (pid in loaded) return; loaded[pid] = 1; f = dir "/perf-" pid ".map"; cnt[pid] = 0
  while ((getline line < f) > 0) { split(line, a, " "); start = hex(a[1]); size = hex(a[2]); sub(/^[^ ]+ [^ ]+ /, "", line); n = ++cnt[pid]; S[pid, n] = start; Z[pid, n] = size; N[pid, n] = line }
  close(f)
}
function name(pid, addr,   i) { for (i = 1; i <= cnt[pid]; i++) if (addr >= S[pid, i] && addr < S[pid, i] + Z[pid, i]) return N[pid, i]; return "" }
/^(EBADF-CLOSE|VICTIM|LOWFD)/ {
  pid = ""; for (i = 1; i <= NF; i++) if ($i ~ /^pid=/) pid = substr($i, 5)
  load(pid); print $1, $2, $3, $4
  seen = 0
  for (i = 1; i <= NF; i++) if ($i == "stack:") seen = i
  for (i = seen + 1; i <= NF; i++) { f = $i; if (f ~ /=/) { print "   " f; continue } r = name(pid, hex(f)); print "   " f "  " (r == "" ? "?" : r) }
  next
}
' "$log"
