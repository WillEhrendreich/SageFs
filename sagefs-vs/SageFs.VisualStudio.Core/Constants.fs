namespace SageFs.VisualStudio.Core

/// Shared constants for the SageFs VS extension.
/// Single source of truth for port numbers and other fixed configuration.
module Constants =

  /// Default port for the SageFs MCP / eval server.
  /// Clients that read %LOCALAPPDATA%\SageFs\daemon.json should use that value;
  /// this constant is the hardcoded fallback when no daemon.json is present.
  let DefaultMcpPort = 37749

  /// Default port for the SageFs HTTP dashboard / REST API (McpPort + 1).
  let DefaultDashboardPort = DefaultMcpPort + 1

  /// Expected apiVersion from the /version endpoint.
  /// Used by CheckVersionAsync to detect incompatible daemon builds.
  let ExpectedApiVersion = 1
