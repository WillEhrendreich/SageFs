I have a small F# project in the current directory (a library called DemoEnv and a test project next to it). I work in Neovim, and it is already open on `DemoEnv/DemoEnv.fs` in a terminal you can drive: `LEMDRIVE.md` in this directory says how. The sagefs.nvim plugin is installed in that Neovim, and its README is in `/lem/docs/`.

`parseSeed` should return `None` for a negative integer string like "-1", while "0" and positive integer strings still return `Some`. Right now "-1" gives `Some -1`. Please do not edit the tests.

Using the plugin, first evaluate `parseSeed (Some "-1")` in the editor and see what it shows. Then change `parseSeed` in the file so a negative integer gives `None`, save, evaluate the same call again, and confirm the editor now shows `None`. Do everything by driving the editor the way I would, with keys.

When you're done, tell me briefly what you changed and what the editor showed before and after.
