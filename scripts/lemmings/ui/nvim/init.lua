-- The Neovim config every Neovim lemming starts with. It is what a person gets from the
-- sagefs.nvim README and nothing else: the plugin on the runtimepath, the F# filetype,
-- the F# tree-sitter parser, and a status line that includes the plugin's own component.
-- This file is configuration, not logic. The harness copies it to a path the sandbox can
-- read and fills the three environment variables below.

-- A plugin manager creates the data directory on first run. This config has no plugin
-- manager, and sagefs.nvim's setup() writes a marker file there and fails without it.
vim.fn.mkdir(vim.fn.stdpath("data"), "p")

vim.opt.runtimepath:prepend(vim.env.SAGEFS_NVIM_DIR)
vim.opt.runtimepath:append(vim.env.LEM_TS_DIR)

vim.filetype.add({ extension = { fs = "fsharp", fsx = "fsharp", fsi = "fsharp", fsproj = "xml" } })

-- Not what the README gives a person, and on purpose: on a shared daemon the plugin prints other
-- sessions' "[SageFs] Creating FSI session..." / "Warming up:" lines and a two-line version
-- warning, and a message taller than the command line is a "Press ENTER" prompt that eats a
-- turn of a free model's budget (run space-bunny-ui-eval-04 spent about 8 of 40 turns on <CR>).
-- Neovim's own message UI (ui2) shows messages in a window instead of stacking them on the
-- command line, so there is no prompt to dismiss; the layout the driver reads (status line second
-- from the bottom, mode on the last row) is unchanged. A Neovim without ui2 gets the shortmess
-- flags only, which shorten file and search chatter but do not remove the prompt.
vim.opt.shortmess:append("TFIcWs")
pcall(function() require("vim._core.ui2").enable({}) end)

vim.o.number = true
vim.o.signcolumn = "yes"
vim.o.laststatus = 2
vim.o.statusline = "%f %y %m%r %l:%c %{%v:lua.require('sagefs').statusline()%}"
vim.o.expandtab = true
vim.o.shiftwidth = 2

vim.api.nvim_create_autocmd("FileType", {
  pattern = "fsharp",
  callback = function() pcall(vim.treesitter.start) end,
})

require("sagefs").setup({})
