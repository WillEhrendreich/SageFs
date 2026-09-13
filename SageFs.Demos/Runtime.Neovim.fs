/// Compiling stub for the Neovim actor's runtime extension (demo-actors-
/// plan.md §1.2/§2.2): the Neovim island will contribute its own cell binds
/// (kitty + nvim + the pinned sagefs.nvim plugin dir) and innerScript launch
/// prologue here, through `Runtime.fs`'s `actorBinds`/`actorPrologue`
/// extension points — never editing `Runtime.fs`'s core. Island F only
/// reserves this file's fsproj slot.
module SageFs.Demos.Runtime.Neovim
