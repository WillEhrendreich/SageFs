/// The one place a dashboard renderer puts a computed value into an attribute or a Datastar expression.
///
/// Falco.Markup writes an attribute value as it is given, inside double quotes, and Falco.Datastar writes a string signal
/// as `'value'` and an expression as it is given. Nothing is escaped, so a `"` in an agent name ends the attribute and a `'`
/// in a saved setting ends the signal's string, and the page's origin may POST `/dashboard/eval`: script that gets into the
/// page runs F# as the user. Every sink here encodes for the place it writes, and `DashboardSinkSourceTests` fails when a
/// renderer calls `Attr.create`, `Ds.signal`, `Ds.onEvent`, `Ds.onClick` or `Ds.show` with anything but literals.
///
/// The rules:
///   - an attribute value is HTML-attribute-encoded (`Sink.attr`);
///   - a string signal's value is JS-string-encoded, then attribute-encoded (`Sink.signal`);
///   - text that goes inside an expression is a JS string literal (`Sink.jsArg`), never spliced between quotes by hand;
///   - an expression is attribute-encoded as a whole (`Sink.onEvent`, `Sink.onClick`, `Sink.show`, `Sink.classWhen`,
///     `Sink.attrExpr`), which the browser undoes before Datastar reads it, so a constant expression is unchanged. Script
///     gets `Encode.attrValue` (`&` and `"` only), which keeps an expression readable in the page source.
module SageFs.Server.DashboardSinks

open System
open Falco.Markup
open Falco.Datastar

/// Text that came from outside the page's own code: a session name, a path, an agent name, a saved setting, a search.
/// The sinks take it in this type, so a raw string has to be named as text before it reaches one.
type UserText = private UserText of string

module UserText =
  /// The text, with a null read as empty.
  let ofString (text: string) : UserText =
    match isNull text with
    | true -> UserText ""
    | false -> UserText text

  let value (UserText text) : string = text

/// The two encodings. Pure, and tested in `DashboardSinkTests`.
module Encode =
  /// Text for an HTML attribute value or a text node: the five characters HTML gives meaning to become entities.
  let html (text: string) : string =
    match String.IsNullOrEmpty text || text.IndexOfAny([| '&'; '<'; '>'; '"'; '\'' |]) < 0 with
    | true -> (match isNull text with | true -> "" | false -> text)
    | false ->
      let sb = System.Text.StringBuilder(text.Length + 16)
      for c in text do
        match c with
        | '&' -> sb.Append("&amp;") |> ignore
        | '<' -> sb.Append("&lt;") |> ignore
        | '>' -> sb.Append("&gt;") |> ignore
        | '"' -> sb.Append("&quot;") |> ignore
        | '\'' -> sb.Append("&#39;") |> ignore
        | c -> sb.Append(c) |> ignore
      sb.ToString()

  /// Text for a double-quoted attribute value with nothing changed that need not be: inside double quotes only `"` ends the
  /// value, and only an `&` followed by a letter, a digit or `#` can start a character reference. Those become `&quot;`
  /// and `&amp;`, and a `<` that opens a tag's shape becomes `&lt;`; `&&`, `<=` and `'` stay as written. Used for script,
  /// so an expression reads in the page source as written.
  let attrValue (text: string) : string =
    match isNull text with
    | true -> ""
    | false ->
      match text.IndexOfAny([| '&'; '"'; '<' |]) < 0 with
      | true -> text
      | false ->
        let next (i: int) = match i + 1 < text.Length with | true -> text.[i + 1] | false -> ' '
        let sb = System.Text.StringBuilder(text.Length + 16)
        for i in 0 .. text.Length - 1 do
          match text.[i] with
          | '"' -> sb.Append("&quot;") |> ignore
          | '&' when Char.IsLetterOrDigit(next i) || next i = '#' -> sb.Append("&amp;") |> ignore
          // Harmless inside the quotes, but a page reader should never see a tag's shape in an attribute either.
          | '<' when Char.IsLetter(next i) || next i = '/' || next i = '!' || next i = '?' -> sb.Append("&lt;") |> ignore
          | other -> sb.Append(other) |> ignore
        sb.ToString()

  /// The inside of a single-quoted JS string literal holding `text`. A backslash, the single quote and the line ends JS
  /// knows (including U+2028 and U+2029) are escaped, so the literal is one line, ends where it should, and holds exactly
  /// the text. It is for an attribute, which the sink encodes; a literal inside a `<script>` element would also need `<`.
  let jsInner (text: string) : string =
    match isNull text with
    | true -> ""
    | false ->
      let sb = System.Text.StringBuilder(text.Length + 8)
      for c in text do
        match c with
        | '\\' -> sb.Append("\\\\") |> ignore
        | '\'' -> sb.Append("\\'") |> ignore
        | '\n' -> sb.Append("\\n") |> ignore
        | '\r' -> sb.Append("\\r") |> ignore
        | '\t' -> sb.Append("\\t") |> ignore
        | ' ' -> sb.Append("\\u2028") |> ignore
        | ' ' -> sb.Append("\\u2029") |> ignore
        | c -> sb.Append(c) |> ignore
      sb.ToString()

  /// A single-quoted JS string literal holding `text`.
  let jsString (text: string) : string = "'" + jsInner text + "'"

module Sink =
  /// An attribute whose value is outside text.
  let attr (name: string) (value: UserText) : XmlAttribute =
    Attr.create name (Encode.html (UserText.value value))

  /// A string signal whose first value is outside text.
  let signal (name: string) (value: UserText) : XmlAttribute =
    Ds.signal (name, Encode.attrValue (Encode.jsInner (UserText.value value)))

  /// A number signal. A number cannot leave its place.
  let signalNumber (name: string) (value: int64) : XmlAttribute = Ds.signal (name, value)

  /// A true/false signal.
  let signalFlag (name: string) (value: bool) : XmlAttribute = Ds.signal (name, value)

  /// Outside text as a JS string literal, for building an expression: `sprintf "f(%s)" (Sink.jsArg text)`.
  let jsArg (value: UserText) : string = Encode.jsString (UserText.value value)

  /// Datastar's `@post` to `url`, with the URL as a JS string literal. `Ds.post` puts the URL between quotes as it is.
  /// A value in the URL's path or query is still the caller's to escape for the URL (`Uri.EscapeDataString`).
  let post (url: string) : string = "@post(" + Encode.jsString url + ")"

  /// An event handler. The expression is attribute-encoded whole; any outside text in it went in through `jsArg`.
  let onEvent (event: string, expr: string) : XmlAttribute = Ds.onEvent (event, Encode.attrValue expr)

  /// An event handler with Datastar's modifiers (a debounce, say).
  let onEventWith (event: string, expr: string, modifiers: OnEventModifier list) : XmlAttribute =
    Ds.onEvent (event, Encode.attrValue expr, modifiers)

  /// A click handler, as `onEvent ("click", expr)`.
  let onClick (expr: string) : XmlAttribute = Ds.onClick (Encode.attrValue expr)

  /// Show the element while `expr` is true.
  let show (expr: string) : XmlAttribute = Ds.show (Encode.attrValue expr)

  /// Set the class `name` while `expr` is true.
  let classWhen (name: string, expr: string) : XmlAttribute = Ds.class' (name, Encode.attrValue expr)

  /// Keep the attribute `name` equal to `expr`.
  let attrExpr (name: string, expr: string) : XmlAttribute = Ds.attr' (name, Encode.attrValue expr)
