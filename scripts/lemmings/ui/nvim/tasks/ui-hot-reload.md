I have a small F# web app in the current directory (FalcoHello, a Falco app with a `/hello` route). I work in Neovim, and it is already open on `Program.fs` in a terminal you can drive: `LEMDRIVE.md` in this directory says how. The sagefs.nvim plugin is installed in that Neovim, and its README is in `/lem/docs/`.

I want to change what the app says without restarting it by hand. Using the plugin: get the app running from the editor, then change the response of the `/hello` route from "Hello from Falco" to "Hello from Neovim", save, and confirm the running app now serves the new text. You can check what the app serves with `nvim-shell curl http://localhost:<port>/hello` (the app picks its port, so you will need to find out which). Do everything else by driving the editor the way I would, with keys.

When you're done, tell me briefly what you did and what `/hello` returned before and after the change.
