namespace SageFs.Server

// DaemonInfo and DaemonState are now in SageFs namespace (SageFs.Core).
// This module re-exports functions so existing code using SageFs.Server.DaemonState compiles.
module DaemonState =
  let SageFsDir = SageFs.DaemonState.SageFsDir
  let daemonJsonPath = SageFs.DaemonState.daemonJsonPath
  let isProcessAlive = SageFs.DaemonState.isProcessAlive
  let serializeInfo = SageFs.DaemonState.serializeInfo
  let deserializeInfo = SageFs.DaemonState.deserializeInfo
  let writeToPath = SageFs.DaemonState.writeToPath
  let clearPath = SageFs.DaemonState.clearPath
  let readFromPath = SageFs.DaemonState.readFromPath
  let write = SageFs.DaemonState.write
  let read = SageFs.DaemonState.read
  let clear = SageFs.DaemonState.clear
