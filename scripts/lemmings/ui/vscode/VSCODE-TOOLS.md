# Driving VS Code

VS Code is open on this folder in a window you cannot see. These commands act on that
window. Each one prints what it did and then a short brief of the window: which part has
focus, an open picker or dialog, notifications, and the status bar. Everything is text.

| Command | What it does |
|---|---|
| `vsc-snapshot` | The whole window as text: tabs, the editor lines with their line numbers (code lenses show as `[lens]`, text drawn after a line as `[deco]`), the side bar and its views with their rows, the panel, notifications and the status bar. |
| `vsc-click <text-or-label>` | Click the button, row, tab or icon whose text or accessibility label matches. |
| `vsc-key <chord> [<chord> ...]` | Press keys, for example `vsc-key ctrl+shift+p`, `vsc-key alt+enter`, `vsc-key escape`. |
| `vsc-type <text>` | Type text into whatever has focus. |
| `vsc-palette <command text>` | Open the command palette, type the text and run the first match. |
| `vsc-open <relative path>` | Open a file from this folder with quick open. |
| `vsc-wait <seconds>` | Wait 1 to 30 seconds, then show the brief. |
| `vsc-shot` | Save a screenshot and print the PNG path. |

Notes:

- Click first, type second: text goes where the keyboard focus is. The brief says where
  that is.
- A command that finds nothing says so and shows the brief. Run `vsc-snapshot` to see
  the names that are on screen.
- There is no way to run script in the window, and a few commands that would change what
  other people are using are refused on purpose.
- Slow things (starting up, running tests) finish in the background. `vsc-wait` and
  `vsc-snapshot` again.
