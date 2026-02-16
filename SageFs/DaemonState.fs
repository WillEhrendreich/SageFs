namespace SageFs.Server

// DaemonInfo and DaemonState are now in SageFs namespace (SageFs.Core).
// This module re-exports functions so existing code using SageFs.Server.DaemonState compiles.
module DaemonState =
  let SageFsDir = SageFs.DaemonState.SageFsDir
  let isProcessAlive = SageFs.DaemonState.isProcessAlive
  let read = SageFs.DaemonState.read
  let readOnPort = SageFs.DaemonState.readOnPort
  let requestShutdown = SageFs.DaemonState.requestShutdown
