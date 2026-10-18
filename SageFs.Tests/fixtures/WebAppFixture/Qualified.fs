/// The shape matrix's `[<RequireQualifiedAccess>]` cell, in a file of its own because the attribute belongs to the
/// file's module.
///
/// A patch binds to the compiled module with `open global.<module>`, and F# refuses to open a module marked
/// `[<RequireQualifiedAccess>]` (FS0892). A save here used to come back as `CompileFailed` on a line nobody wrote.
/// The patch is refused before it is written, and with no live state in the file the whole file is re-evaluated,
/// which compiles without the open. The handler's signature uses no type of this file, so the re-evaluated method
/// pairs with the compiled one and the running app serves the new body.
[<RequireQualifiedAccess>]
module WebAppFixture.Qualified

/// FUNCTION binding with a BCL-only signature, in a module that cannot be opened.
let handler (who: string) : string = "A" + who
