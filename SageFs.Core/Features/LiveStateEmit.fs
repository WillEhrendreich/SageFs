/// The F# text a hot-reload patch needs so it can reach the running app's live
/// module state without re-declaring it.
///
/// Why text: the app runs in the isolated FSI host, which is a different
/// process from this one, and the only thing that crosses into it is F# source
/// for FSI to compile. So the reflection that finds the app's storage is
/// generated here and runs there. Everything in this module is pure.
module SageFs.Features.LiveStateEmit

open System
open SageFs.Features.ReloadPlanning

/// Every CLR name a compiled F# module at `segments` could have. A namespace
/// segment joins with '.', a module nested in a module joins with '+', and the
/// compiler adds a `Module` suffix when a type of the same name sits beside the
/// module. A `FileDecls` doesn't record which of its path segments are
/// namespaces, so every split is a candidate and the lookup takes whichever one
/// the running assembly actually has.
///
/// Linear in the path length on purpose: the suffix is only tried on the
/// module that holds the binding. Trying it on every segment is 2^n names, and
/// a property test with a long generated path took 33 GB finding that out.
let typeNameCandidates (segments: string list) : string list =
  let n = List.length segments
  [ for namespaceCount in 0 .. n - 1 do
      let ns = segments |> List.take namespaceCount
      let types = segments |> List.skip namespaceCount
      let owners = List.take (types.Length - 1) types
      let owner = List.last types
      for leaf in [ owner; owner + "Module" ] do
        let typeName = String.concat "+" (owners @ [ leaf ])
        match ns with
        | [] -> yield typeName
        | _ -> yield String.concat "." ns + "." + typeName ]
  |> List.distinct

let private fsharpString (s: string) =
  "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

/// Why live state couldn't be carried, kept, probed or reset. Every case
/// ends in the app being left alone, and `describe` says why.
[<RequireQualifiedAccess>]
type LiveStateError =
  /// The binding's own source has no initializer to read.
  | NoInitializer of binding: string
  /// FSI's output didn't carry the probe's or reset's answer.
  | NoAnswer of fsiOutput: string
  /// There was an answer and it didn't decode.
  | Garbled of detail: string
  /// The submission itself failed in FSI.
  | EvalFailed of message: string
  /// The submission ran past its budget and was abandoned.
  | TimedOut of seconds: float

module LiveStateError =
  let describe =
    function
    | LiveStateError.NoInitializer binding -> sprintf "'%s' has no initializer I can read from its source" binding
    | LiveStateError.NoAnswer output -> sprintf "the running app didn't answer (FSI said: %s)" output
    | LiveStateError.Garbled detail -> sprintf "the running app's answer didn't make sense: %s" detail
    | LiveStateError.EvalFailed message -> sprintf "FSI couldn't run it: %s" message
    | LiveStateError.TimedOut seconds -> sprintf "it didn't finish within %.0fs" seconds

/// The right-hand side of a value binding, from its own source text. `Header`
/// is everything before the `=`, so the initializer is whatever follows the
/// first `=` after it.
let initializerOf (decl: SourceDecl) : Result<string, LiveStateError> =
  match decl.Text.IndexOf(decl.Header, StringComparison.Ordinal) with
  | -1 -> Error(LiveStateError.NoInitializer decl.Name)
  | start ->
    match decl.Text.IndexOf('=', start + decl.Header.Length) with
    | -1 -> Error(LiveStateError.NoInitializer decl.Name)
    | eq ->
      match decl.Text.Substring(eq + 1).Trim() with
      | "" -> Error(LiveStateError.NoInitializer decl.Name)
      | rhs -> Ok rhs

/// Lines of `text` re-indented to start at `indent`, keeping each line's
/// indentation relative to the others so an offside-sensitive expression still
/// parses.
let private reindent (indent: string) (text: string) : string list =
  let lines = text.Replace("\r\n", "\n").Split('\n') |> Array.toList
  match lines with
  | [] -> []
  | first :: rest ->
    let leading (l: string) = l.Length - l.TrimStart().Length
    let margin =
      rest
      |> List.filter (fun l -> l.Trim() <> "")
      |> List.map leading
      |> function
        | [] -> 0
        | xs -> List.min xs
    (indent + first.Trim())
    :: (rest
        |> List.map (fun l ->
          match l.Trim() with
          | "" -> ""
          | _ -> indent + "  " + l.Substring(min margin l.Length)))

/// The identifier the stand-in's storage handle is bound to.
let private handleName (decl: SourceDecl) = sprintf "__sagefsLive_%s" decl.Name

/// The type whose static property stands in for the binding.
let private standInTypeName (decl: SourceDecl) = sprintf "SageFsLive_%s" decl.Name

/// An expression that finds the app's own `PropertyInfo` for `decl` in the
/// running process. It throws when the binding can't be found, so the eval
/// fails and nothing gets pointed at storage that isn't there. It throws on
/// more than one match too: two loaded copies of the module means picking one
/// is a guess, and a wrong guess is the silent state loss this exists to
/// prevent.
let private locateStorageExpr (indent: string) (moduleSegments: string list) (name: string) : string list =
  let names = typeNameCandidates moduleSegments |> List.map fsharpString |> String.concat "; "
  let binding = String.concat "." (moduleSegments @ [ name ])
  [ sprintf "%s  let flags = System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.NonPublic ||| System.Reflection.BindingFlags.Static" indent
    sprintf "%s  let names = [ %s ]" indent names
    sprintf "%s  let found =" indent
    sprintf "%s    System.AppDomain.CurrentDomain.GetAssemblies()" indent
    sprintf "%s    |> Array.filter (fun a -> not a.IsDynamic)" indent
    sprintf "%s    |> Array.collect (fun a ->" indent
    sprintf "%s      names" indent
    sprintf "%s      |> List.choose (fun n ->" indent
    sprintf "%s        match a.GetType(n, false) with" indent
    sprintf "%s        | null -> None" indent
    sprintf "%s        | t ->" indent
    sprintf "%s          match t.GetProperty(%s, flags) with" indent (fsharpString name)
    sprintf "%s          | null -> None" indent
    sprintf "%s          | p -> Some p)" indent
    sprintf "%s      |> List.toArray)" indent
    sprintf "%s  match found with" indent
    sprintf "%s  | [| p |] -> p" indent
    sprintf "%s  | [||] -> failwith %s" indent (fsharpString (sprintf "SageFs hot reload: can't find the running app's storage for '%s'" binding))
    sprintf "%s  | many -> failwithf %s many.Length" indent (fsharpString (sprintf "SageFs hot reload: %%d loaded assemblies define '%s', so there's no telling which one the app is using" binding)) ]

/// The same lookup bound to a module-level `let`, which runs when FSI
/// evaluates the patch.
let private locateStorage (indent: string) (moduleSegments: string list) (decl: SourceDecl) : string list =
  sprintf "%slet private %s : System.Reflection.PropertyInfo =" indent (handleName decl)
  :: locateStorageExpr indent moduleSegments decl.Name

/// The stand-in for an unedited non-public `let mutable` a patch uses: a
/// private type with a static property of the SAME name, opened with
/// `open type`, so `hidden` and `hidden <- v` in the patched function compile
/// unchanged and read and write the app's own field through reflection.
///
/// The property needs the binding's type. A declared annotation is used as is.
/// Without one, the getter is `if true then <live value> else (<initializer>)`:
/// F# infers the type from the initializer, and the initializer never runs.
/// That matters, because re-running it is exactly what rule 1 forbids (and an
/// initializer can have side effects, like opening a connection).
let carriedStandIn (indent: string) (moduleSegments: string list) (decl: SourceDecl) : Result<string list, LiveStateError> =
  let typed body =
    let handle = handleName decl
    locateStorage indent moduleSegments decl
    @ [ sprintf "%stype private %s() =" indent (standInTypeName decl)
        sprintf "%s  static member %s" indent decl.Name ]
    @ body handle
    // The `if true then v else <the property>` is only there for the type: it
    // makes the setter's value the property's own type (a bare `box v` would
    // infer `obj`, and a property's getter and setter must agree).
    @ [ sprintf "%s    and set (v) = %s.SetValue(null, box (if true then v else %s.%s))" indent handle (standInTypeName decl) decl.Name
        sprintf "%sopen type %s" indent (standInTypeName decl) ]
  match annotationOf decl with
  | Some annotation ->
    Ok(typed (fun handle -> [ sprintf "%s    with get () : %s = unbox (%s.GetValue(null))" indent annotation handle ]))
  | None ->
    initializerOf decl
    |> Result.map (fun init ->
      typed (fun handle ->
        [ sprintf "%s    with get () =" indent
          sprintf "%s      if true then unbox (%s.GetValue(null))" indent handle
          sprintf "%s      else" indent ]
        @ reindent (indent + "        ") ("(" + init + ")")))

// ── Kept state (rule 3): the probe at save time and the reset later ─────────

/// What the probe found about a kept binding in the running app.
[<RequireQualifiedAccess>]
type ProbeReading =
  /// Same type as the edited initializer, so the live value stays. The preview
  /// is `%A` of the live value, cut short.
  | Keeps of preview: string
  /// The edited initializer has a different type from the live value, so
  /// there's nothing safe to keep.
  | Retyped of was: string * now: string

/// The longest preview a notice carries. A kept `Dictionary` with a thousand
/// entries is still one line on the dashboard.
[<Literal>]
let PreviewLimit = 120

let private initFunctionName (decl: SourceDecl) = sprintf "__sagefsInit_%s" decl.Name

let private b64Marker = "SAGEFS_LIVE_STATE:"

/// The part of a probe or reset that FSI evaluates as a module: the edited
/// initializer as a function, in the file's own module path with its opens and
/// `open global.<compiled module>`, so it compiles exactly like the source did.
/// Wrapping it in a function is the point: defining it runs nothing.
let private initializerModule (decls: FileDecls) (decl: SourceDecl) : Result<string list, LiveStateError> =
  initializerOf decl
  |> Result.map (fun init ->
    let path = decls.ModulePath @ decl.Container
    let pad depth = String.replicate depth "  "
    let headers = path |> List.mapi (fun depth part -> sprintf "%smodule %s =" (pad depth) part)
    let indent = pad path.Length
    headers
    @ (decls.Opens |> List.map (sprintf "%sopen %s" indent))
    @ [ sprintf "%sopen global.%s" indent (String.concat "." path)
        sprintf "%slet %s () =" indent (initFunctionName decl) ]
    @ reindent (indent + "  ") ("(" + init + ")"))

let private qualifiedInit (decls: FileDecls) (decl: SourceDecl) =
  String.concat "." (decls.ModulePath @ decl.Container @ [ initFunctionName decl ])

/// A top-level expression, so FSI reports it as `it` and the detour matcher
/// leaves it alone. Its value is base64 behind a marker, so no preview text,
/// however odd, can confuse the parse on the way back.
let private reportExpression (body: string list) (result: string) : string list =
  [ "("
    yield! body
    sprintf "  %s + System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(string (%s)))" (fsharpString b64Marker) result
    ")" ]

let private previewOf (value: string) =
  [ sprintf "  let preview (v: obj) ="
    sprintf "    let s = sprintf \"%%A\" v"
    sprintf "    if s.Length > %d then s.Substring(0, %d) + \"...\" else s" PreviewLimit (PreviewLimit - 3)
    sprintf "  let previewText = preview (%s)" value ]

/// The FSI submission that checks a kept binding at save time: is the edited
/// initializer the same type as the live value, and what IS the live value.
/// It never runs the initializer and never writes the storage.
let probeCode (decls: FileDecls) (decl: SourceDecl) : Result<string, LiveStateError> =
  initializerModule decls decl
  |> Result.map (fun moduleLines ->
    let segments = decls.ModulePath @ decl.Container
    let body =
      [ "  let storage ="
        yield! locateStorageExpr "  " segments decl.Name
        "  let typeOfResult (_: unit -> 'T) = typeof<'T>"
        sprintf "  let newType = typeOfResult %s" (qualifiedInit decls decl)
        yield! previewOf "storage.GetValue(null)"
        "  let answer ="
        "    match storage.PropertyType = newType with"
        "    | true -> \"keeps\\n\" + previewText"
        "    | false -> \"retyped\\n\" + storage.PropertyType.Name + \"\\n\" + newType.Name" ]
    moduleLines @ reportExpression body "answer" |> String.concat "\n")

/// The FSI submission behind a reset: run ONLY this binding's new initializer
/// and store the result in the app's own field. Answers with the new value's
/// preview.
let resetCode (decls: FileDecls) (decl: SourceDecl) : Result<string, LiveStateError> =
  initializerModule decls decl
  |> Result.map (fun moduleLines ->
    let segments = decls.ModulePath @ decl.Container
    let body =
      [ "  let storage ="
        yield! locateStorageExpr "  " segments decl.Name
        sprintf "  let fresh = %s ()" (qualifiedInit decls decl)
        "  storage.SetValue(null, box fresh)"
        yield! previewOf "storage.GetValue(null)" ]
    moduleLines @ reportExpression body "previewText" |> String.concat "\n")

/// The payload a probe or reset submission reported, out of FSI's echo of it.
let private reportedText (evalOutput: string) : Result<string, LiveStateError> =
  let m = System.Text.RegularExpressions.Regex.Match(evalOutput, System.Text.RegularExpressions.Regex.Escape b64Marker + "([A-Za-z0-9+/=]*)")
  match m.Success with
  | false -> Error(LiveStateError.NoAnswer evalOutput)
  | true ->
    try Ok(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String m.Groups.[1].Value))
    with ex -> Error(LiveStateError.Garbled ex.Message)

let parseProbe (evalOutput: string) : Result<ProbeReading, LiveStateError> =
  reportedText evalOutput
  |> Result.bind (fun text ->
    match text.Split('\n') |> Array.toList with
    | "keeps" :: preview -> Ok(ProbeReading.Keeps(String.concat "\n" preview))
    | [ "retyped"; was; now ] -> Ok(ProbeReading.Retyped(was, now))
    | _ -> Error(LiveStateError.Garbled text))

/// The new value's preview after a reset.
let parseReset (evalOutput: string) : Result<string, LiveStateError> = reportedText evalOutput
