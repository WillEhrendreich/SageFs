# Changelog

All notable changes to SageFs will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.5.17] - 2026-02-19

### Fixed
- MCP session routing now prioritizes `working_directory` over cached agent session, fixing multi-project workflows where all commands routed to the first session
- Replaced named pipe transport with HTTP (Kestrel) transport, eliminating hangs when `get_fsi_status` was called during long-running evaluations

### Changed
- Worker processes now communicate over HTTP instead of named pipes
- Each worker starts a Kestrel server on an OS-assigned port, enabling concurrent request handling

## [0.5.15] - 2026-02-18

### Added
- Initial public release as a .NET global tool
- F# REPL with FSI session management
- MCP server for AI assistant integration
- TUI and Raylib GUI dual-renderer architecture
- Multi-session support with per-project isolation
- Hot reloading of F# source files
- Syntax highlighting via tree-sitter
- Expecto test runner integration
