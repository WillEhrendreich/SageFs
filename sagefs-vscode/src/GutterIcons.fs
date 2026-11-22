module SageFs.Vscode.GutterIcons

open Vscode

/// A gutter icon is painted in the glyph margin at a fixed column. Inline `before`
/// text is laid out in the text flow and pushes the source to the right, which is
/// what made the test marks look misaligned with the code. `extensionPath` is the
/// installed extension folder, handed over by the context at activation.
let icon (extensionPath: string) (name: string) : obj =
  uriFile (sprintf "%s/media/gutter/%s.svg" extensionPath name) :> obj
