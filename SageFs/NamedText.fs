namespace SageFs

// ── Text that means one thing ──────────────────────────────────────────
//
// A function that takes two strings can be called with them swapped, and the compiler says nothing. These
// wrappers give the strings a name in the type, so a swap is a compile error. None of them checks the text:
// each says what the string IS, and the function that takes it decides what to do with it. Pattern-match to
// read one: `(DirPath dir)` in a parameter list.

/// A directory on disk, as the caller spelled it. Not normalised: each function decides whether it must be.
type DirPath = DirPath of string

/// A file on disk, as the caller spelled it.
type FilePath = FilePath of string

/// The URI an MCP client subscribes to, such as `sagefs://sessions`.
type ResourceUri = ResourceUri of string

/// A route on the daemon's own HTTP API, such as `/exec` or `/api/sessions`.
type ApiPath = ApiPath of string

/// The dotted name of a namespace, module, type or member, as an agent asks about it.
type QualifiedName = QualifiedName of string
