# Driving my Neovim

My Neovim is already open on this project, in a terminal you can reach through a small
tool. You can't type into it directly. You send it keys and read back what the screen
shows, the same way you'd sit at it.

Run the tool like this, from this directory:

```
dotnet /lem/drive/LemDrive.dll nvim <command> ...
```

| Command | What it does |
|---|---|
| `keys <keys>` | Sends keys in vim notation: `ihello<Esc>`, `:w<CR>`, `<C-w>l`, `<M-CR>` (Alt-Enter). Write `<lt>` for a literal `<`. Several arguments are joined with nothing between them. Prints the screen afterwards. |
| `nvim-type <text>` | Types the text as it is, character by character. A newline in the text is an Enter. Prints the screen afterwards. |
| `nvim-screen` | Prints the editor screen. The first line gives the mode, where the cursor is on the screen, and the status line. |
| `nvim-wait <seconds>` | Waits (at most 30 seconds), then prints the screen. Some things in the editor take a while. |
| `nvim-messages` | Shows what `:messages` shows. |
| `nvim-shell <cmd>` | Runs one `curl` (localhost only), `cat` or `ls` in a second terminal next to the editor and prints the output. No quotes, pipes, `;`, `&` or redirection. |

Quote the argument so your shell passes it through as one piece, for example
`nvim keys ':SageFsStatus<CR>'`. Single quotes are the easiest.

A few things worth knowing:

- The files in this directory are read-only for you. They change when the editor writes
  them, not otherwise. That is on purpose: I want the editor to do the work.
- The editor is a normal Neovim 0.10+ with one plugin, sagefs.nvim. If I gave you its
  documentation for this job, it is in `/lem/docs/`.
- The screen is 140 columns by 40 rows. Windows that pop up (floating windows, pickers)
  show on it like anything else. `<Esc>` or `q` usually closes them.
- Escape matters: after `<Esc>` the editor is in normal mode. If a prompt like
  `Press ENTER or type command to continue` is on the bottom line, press `<CR>`.
- Every call is saved, so I can replay what you did.
