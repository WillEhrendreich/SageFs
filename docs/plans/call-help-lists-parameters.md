# `sagefs call <tool> --help` says what the tool takes

Roadmap item: `call-help-lists-parameters` (Next, Platform). Plan written 2026-11-08 against master 213fbc10.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | S |
| Branch and worktree | none yet |
| Last green commit | none yet. It starts from `SageFs.Core/McpCall.fs` and `SageFs/McpCallCommand.fs` as on master. |
| Next action | Write the RED case in "Tests": the parser sees a tool name and `--help` and asks for that tool's description, not the command's usage. |
| Decisions made | none yet |
| Surprises | none yet |

## What done looks like

`sagefs call send_fsharp_code --help` prints the tool's own sentence and what it takes, one parameter a line with its type and whether it is required, and says where the full manual is (`discover_features` with `tool`). `sagefs call --help` still prints the command's usage. A tool name the daemon does not list is refused the way a call to it already is: `ArgRefusal.UnknownTool` names the tools that caller is shown.

## What exists today

Checked by running it on 2026-11-08, then by reading the code.

- `sagefs call send_fsharp_code --help` prints the command's usage and exits 0. It says nothing about `send_fsharp_code`. A person has to run `sagefs call --list`, which shows only the first line of each description, then read `docs/mcp-tools.md`, or call the tool wrong and read the refusal.
- The wrong-call refusal already knows the answer: `ToolSpec.describeParams` prints `"<tool> takes: name (type, required), ..."`, from the input schema the daemon sends in `tools/list` (`SageFs.Core/McpCall.fs`, `ParamSpec`, `ParamKind.label`).
- `Invocation.Help` carries no tool name (`McpCall.fs`), so the parser cannot say which tool was asked about. `help` is one of the command's own options (`CommandOption.Help`) and is read wherever it stands.
- `converse` in `McpCallCommand.fs` already shakes hands and reads the tool list before it finds the tool, so a help for one tool needs no new round trip.
- A tool's description in the list is one sentence of at most 160 characters since `small-default-tool-list`. The full manual for a tool is `discover_features` with `tool`.

## Steps

1. RED: `McpCallTests` (pure): `--help` after a tool name parses to a request to describe that tool. `--help` alone still parses to `Invocation.Help`. `--list --help` stays the usage.
2. Add the case to `Invocation` (or a `Target.DescribeTool`), and a pure `ToolSpec.helpText` that joins the description, one line per parameter and the pointer to the manual. `describeParams` stays as the one line the refusals use.
3. `McpCallCommand.converse` answers it from the tool list it already read, and an unknown name goes through `Arguments.findTool`'s existing refusal.
4. `docs/mcp-tools.md` line 203 area (the `sagefs call --list` row) gets the sentence, and the usage text says `<tool> --help`.
5. `McpCallIntegrationTests`: a real daemon, a real tool, and the printed parameters match the tool's schema.

## Tests

- Pure cases in step 1, plus a property over the registered tools: for every tool, the help text names every parameter in its schema and marks the required ones.
- The integration case in step 5 is the outcome gate. The expected parameter names come from the tool's own schema, never from a literal in the test.
- A twin that prints the usage for a tool must fail the property.

## Risks and dependencies

- A tool parameter with a command option's name would be unreachable by flag, so a test already reads the registered tools against `CommandOption.all`. `--help` for a tool must not break that.
- No wire change: the daemon's answers are unchanged, so `Plugin: n/a`.

## Size

S.

## Decisions for Will

None.
