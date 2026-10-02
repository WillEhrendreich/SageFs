// scripts/lemmings/ui/nvim/run-nvim-tour.fsx <tour-file> [fixture] [open-file] [run-id]
// Run with: dotnet fsi scripts/lemmings/ui/nvim/run-nvim-tour.fsx -- <args>
//
// Runs a tour (see tours/ and NvimTour.fs) against a fresh Neovim on a fresh copy of a fixture, with no
// lemming and no model: the same editor sandbox, the same shared SageFs daemon on 37749, numbered shots in
// <run-dir>/out/shots. A design reviewer reads those. The run directory is named tour-<tour>-<nn> under
// $LEM_ROOT, so its sessions are recognisable in the dashboard, and it is pruned to out/ when it ends.
//
//   fixture     a directory under scripts/lemmings/fixtures (default demoenv)
//   open-file   the file Neovim opens on, relative to the fixture (default DemoEnv/DemoEnv.fs)
//
// The daemon is never started, stopped or reconfigured here. Sessions the tour created are read from the
// daemon first and then stopped by exact id, only those under this run directory. The exit code is the
// tour's own. The logic is LemRun's NvimRun.fs.
#load "../../launch.fsx"
Launch.run "LemRun" [ "nvim-tour" ]
