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

/// The right-hand side of a value binding, from its own source text. `Header`
/// is everything before the `=`, so the initializer is whatever follows the
/// first `=` after it.
let initializerOf (decl: SourceDecl) : Result<string, string> =
  match decl.Text.IndexOf(decl.Header, StringComparison.Ordinal) with
  | -1 -> Error(sprintf "the declaration of '%s' doesn't contain its own header" decl.Name)
  | start ->
    match decl.Text.IndexOf('=', start + decl.Header.Length) with
    | -1 -> Error(sprintf "the declaration of '%s' has no '='" decl.Name)
    | eq ->
      match decl.Text.Substring(eq + 1).Trim() with
      | "" -> Error(sprintf "the declaration of '%s' has an empty initializer" decl.Name)
      | rhs -> Ok rhs

/// A declared type annotation on a binding's header, e.g. `int` from
/// `let mutable private hidden : int`.
let annotationOf (decl: SourceDecl) : string option =
  match decl.Header.IndexOf(':') with
  | -1 -> None
  | colon ->
    match decl.Header.Substring(colon + 1).Trim() with
    | "" -> None
    | t -> Some t

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

/// A module-level `let` that finds the app's own `PropertyInfo` for `decl` in
/// the running process. It runs when FSI evaluates the patch, so a binding that
/// can't be found fails the eval and nothing gets re-pointed at a stand-in that
/// points nowhere. Fail closed on more than one match too: two loaded copies of
/// the module means picking one is a guess, and a wrong guess is the silent
/// state loss this exists to prevent.
let private locateStorage (indent: string) (moduleSegments: string list) (decl: SourceDecl) : string list =
  let names = typeNameCandidates moduleSegments |> List.map fsharpString |> String.concat "; "
  let binding = String.concat "." (moduleSegments @ [ decl.Name ])
  [ sprintf "%slet private %s : System.Reflection.PropertyInfo =" indent (handleName decl)
    sprintf "%s  let flags = System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.NonPublic ||| System.Reflection.BindingFlags.Static" indent
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
    sprintf "%s          match t.GetProperty(%s, flags) with" indent (fsharpString decl.Name)
    sprintf "%s          | null -> None" indent
    sprintf "%s          | p -> Some p)" indent
    sprintf "%s      |> List.toArray)" indent
    sprintf "%s  match found with" indent
    sprintf "%s  | [| p |] -> p" indent
    sprintf "%s  | [||] -> failwith %s" indent (fsharpString (sprintf "SageFs hot reload: can't find the running app's storage for '%s'" binding))
    sprintf "%s  | many -> failwithf %s many.Length" indent (fsharpString (sprintf "SageFs hot reload: %%d loaded assemblies define '%s', so there's no telling which one the app is using" binding)) ]

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
let carriedStandIn (indent: string) (moduleSegments: string list) (decl: SourceDecl) : Result<string list, string> =
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
