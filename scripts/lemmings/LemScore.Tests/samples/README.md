# Samples

These are what the LemScore tests read. The ones that came off a real machine are marked real.

- `list-models.txt` (real): `cmdc --list-models` on 2026-10-01, Command Code 1.73.4. Four models are marked FREE. `stealth/pixel-canary` is not in it, and `typesafe/jev` says "everything else free" in its blurb without being marked FREE, so the tests use both as refusals.
- `plain-answer.ndjson` (real): `cmdc -p "Reply with the single word OK..." --output-format json` on `stealth/space-bunny-alpha`. One turn, no tools.
- `sagefs-error-and-shell.ndjson` (real): a stop_session call SageFs refuses (the session belongs to another MCP connection), then a shell call. The refusal ends in `-> Next:` the way SageFs writes every agent-facing error.
- `parse-seed-real-run.ndjson` (real): the whole stream of the first smoke run of the harness, `space-bunny-parse-seed-01`, 20 turns and 28 tool calls. It has a tool_errored from SageFs (`run_tests` before any test was discovered), an eval that failed on the model's own code, and a `tool_input_repaired` event. Its `get_daemon_status` result is what the daemon status parser is tested on.
- `sandbox-ps.txt` (real): `ps` from inside the bubblewrap sandbox right after cmdc exited, from the same run. The only thing left over was an MSBuild node.

I have not been able to capture a provider quota error on demand, so the quota tests build their event lines in `Samples.fs` from the shapes in the cmdc 1.73 bundle: the exit-code table (5 rate limited, 10 insufficient credits, 8 turn cap) and a result line with `"subtype":"error"` and an `error` string. The old report quotes the real text, "You've reached today's limit on Ling 3.0 Flash Sante.", and the test uses it.
