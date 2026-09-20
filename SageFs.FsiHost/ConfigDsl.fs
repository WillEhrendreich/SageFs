namespace SageFs

/// The `DirectoryConfig.empty` that existing `.SageFs/config.fsx` files are written against
/// (`{ DirectoryConfig.empty with AutoOpenNamespaces = false }`). Compiled ONLY into the isolated FSI host, where a
/// config script is evaluated; the daemon has its own `DirectoryConfig` module for the file operations. Both point
/// at the one definition in DirectoryConfigDefaults, so the script's view of "the default" cannot drift.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module DirectoryConfig =
  let empty = DirectoryConfigDefaults.empty
