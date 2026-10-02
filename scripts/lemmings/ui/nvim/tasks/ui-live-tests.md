I have a small F# project in the current directory (a library called DemoEnv and an Expecto test project, DemoEnv.Tests, next to it). I work in Neovim, and it is already open on `DemoEnv/DemoEnv.fs` in a terminal you can drive: `LEMDRIVE.md` in this directory says how. The sagefs.nvim plugin is installed in that Neovim, and its README is in `/lem/docs/`.

One test is failing: "DemoEnv.parseSeed a negative integer yields None". I want to watch it fail and then pass inside the editor, without leaving it.

Using the plugin: turn live testing on, find the failing test in the editor (a marker in the gutter, or the test panel), fix `parseSeed` in `DemoEnv/DemoEnv.fs` so a negative integer string gives `None` (zero and positive integers must still give `Some`), save, and watch the test go green. Do everything by driving the editor the way I would, with keys.

When you're done, tell me briefly what you saw before the fix and after it.
