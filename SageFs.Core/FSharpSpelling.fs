namespace SageFs

open System
open System.Globalization

/// A type's name as a person who writes F# spells it. The live-bindings pane shows the type of every value, and the runtime's
/// own names (`Double`, `Int32`, `FSharpList`1`) are not what anyone typed. `runtimeName` reads a `Type` into the runtime's short
/// name with its generic arguments kept (`FSharpList`1[Int32]`); `display` turns that text into the F# spelling (`int list`).
/// The runtime name is what travels on the wire and what a tooltip shows. The F# spelling is only ever drawn.
module FSharpTypeName =

  /// The runtime's short name of a type, with the generic arguments that `Type.Name` leaves out:
  /// `Int32`, `FSharpList`1[Int32]`, `Tuple`2[Int32,String]`, `Int32[]`, `Int32[,]`. A generic definition with no arguments keeps
  /// its arity (`FSharpList`1`), because it has no arguments to show.
  let rec runtimeName (t: Type) : string =
    match t.IsArray with
    | true ->
      let suffix =
        match t.GetArrayRank() with
        | 1 -> "[]"
        | rank -> "[" + String(',', rank - 1) + "]"
      runtimeName (t.GetElementType()) + suffix
    | false ->
      match t.IsGenericType && not t.IsGenericTypeDefinition with
      | true -> sprintf "%s[%s]" t.Name (t.GetGenericArguments() |> Array.map runtimeName |> String.concat ",")
      | false -> t.Name

  /// A type as the runtime name spells it, taken apart.
  type Shape =
    | Named of name: string * args: Shape list
    | ArrayOf of element: Shape * rank: int

  let isIdentChar (c: char) : bool = c <> '[' && c <> ']' && c <> ','

  /// Where the name that starts at `start` ends.
  let rec identEnd (text: string) (start: int) : int =
    match start < text.Length && isIdentChar text.[start] with
    | true -> identEnd text (start + 1)
    | false -> start

  /// An opening bracket that starts a list of generic arguments, not an array suffix (`[]`, `[,]`).
  let opensArguments (text: string) (at: int) : bool =
    at + 1 < text.Length && text.[at] = '[' && text.[at + 1] <> ']' && text.[at + 1] <> ','

  /// The rank of an array suffix that starts at `at` (`[]` is 1, `[,]` is 2), and where it ends.
  let arraySuffix (text: string) (at: int) : (int * int) option =
    match at < text.Length && text.[at] = '[' with
    | false -> None
    | true ->
      let rec commasEnd (from: int) : int =
        match from < text.Length && text.[from] = ',' with
        | true -> commasEnd (from + 1)
        | false -> from
      let close = commasEnd (at + 1)
      match close < text.Length && text.[close] = ']' with
      | true -> Some(close - at, close + 1)
      | false -> None

  /// Read one type at `start`: its name, its generic arguments, and any array suffixes. `None` for text that is not shaped like that.
  let rec parseType (text: string) (start: int) : (Shape * int) option =
    let stop = identEnd text start
    match stop > start with
    | false -> None
    | true ->
      let name = text.Substring(start, stop - start)
      let argsAndNext =
        match opensArguments text stop with
        | true -> parseArguments text (stop + 1) []
        | false -> Some([], stop)
      argsAndNext
      |> Option.map (fun (args, next) ->
        let rec suffixes (shape: Shape) (at: int) : Shape * int =
          match arraySuffix text at with
          | Some(rank, after) -> suffixes (ArrayOf(shape, rank)) after
          | None -> shape, at
        suffixes (Named(name, args)) next)

  /// The comma-separated arguments up to the closing bracket, from `at`.
  and parseArguments (text: string) (at: int) (found: Shape list) : (Shape list * int) option =
    match parseType text at with
    | None -> None
    | Some(shape, next) ->
      let collected = found @ [ shape ]
      match next < text.Length with
      | false -> None
      | true ->
        match text.[next] with
        | ',' -> parseArguments text (next + 1) collected
        | ']' -> Some(collected, next + 1)
        | _ -> None

  /// How tightly a spelled type binds, so a tuple inside a list is parenthesised and a name is not.
  type Binding =
    | Atom
    | TupleLike
    | ArrowLike

  let primitiveAliases : (string * string) list =
    [ "Boolean", "bool"
      "Byte", "byte"
      "SByte", "sbyte"
      "Int16", "int16"
      "UInt16", "uint16"
      "Int32", "int"
      "UInt32", "uint32"
      "Int64", "int64"
      "UInt64", "uint64"
      "Single", "float32"
      "Double", "float"
      "Decimal", "decimal"
      "Char", "char"
      "String", "string"
      "Object", "obj"
      "Unit", "unit"
      "Void", "unit"
      "IntPtr", "nativeint"
      "UIntPtr", "unativeint"
      "BigInteger", "bigint"
      "Exception", "exn" ]

  let aliasOf : Map<string, string> = Map.ofList primitiveAliases

  /// The runtime's name without its arity (`FSharpList`1` is `FSharpList`), and that arity.
  let splitArity (name: string) : string * int =
    match name.IndexOf '`' with
    | -1 -> name, 0
    | tick ->
      let digits = name.Substring(tick + 1) |> Seq.takeWhile Char.IsDigit |> Seq.toArray |> String
      let arity =
        match Int32.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, parsed -> parsed
        | false, _ -> 0
      name.Substring(0, tick), arity

  /// F# library names lose their `FSharp` prefix when spelled as a generic (`FSharpMap` is `Map`).
  let spelledBase (baseName: string) : string =
    match baseName.StartsWith "FSharp" && baseName.Length > 6 && Char.IsUpper baseName.[6] with
    | true -> baseName.Substring 6
    | false -> baseName

  let rec spell (shape: Shape) : string * Binding =
    let operand (inner: Shape) : string =
      match spell inner with
      | text, Atom -> text
      | text, (TupleLike | ArrowLike) -> sprintf "(%s)" text
    match shape with
    | ArrayOf(element, rank) -> operand element + (match rank with | 1 -> "[]" | n -> "[" + String(',', n - 1) + "]"), Atom
    | Named(name, args) ->
      let baseName, arity = splitArity name
      // A generic definition with no arguments (`FSharpList`1`) is spelled with a blank for each one it would take.
      let args = match args, arity with | [], arity when arity > 0 -> List.replicate arity (Named("_", [])) | _ -> args
      let generic () : string * Binding =
        let inner = args |> List.map (fun a -> fst (spell a)) |> String.concat ", "
        sprintf "%s<%s>" (spelledBase baseName) inner, Atom
      match baseName, args with
      | "FSharpList", [ a ] -> operand a + " list", Atom
      | "FSharpOption", [ a ] -> operand a + " option", Atom
      | "FSharpValueOption", [ a ] -> operand a + " voption", Atom
      | "FSharpRef", [ a ] -> operand a + " ref", Atom
      | "IEnumerable", [ a ] -> sprintf "seq<%s>" (fst (spell a)), Atom
      | "List", [ a ] -> sprintf "ResizeArray<%s>" (fst (spell a)), Atom
      | "FSharpFunc", [ a; b ] ->
        let left =
          match spell a with
          | text, ArrowLike -> sprintf "(%s)" text
          | text, (Atom | TupleLike) -> text
        sprintf "%s -> %s" left (fst (spell b)), ArrowLike
      | "Tuple", (_ :: _ :: _) -> args |> List.map operand |> String.concat " * ", TupleLike
      | "ValueTuple", (_ :: _ :: _) -> sprintf "struct (%s)" (args |> List.map operand |> String.concat " * "), Atom
      | _, [] ->
        match Map.tryFind baseName aliasOf with
        | Some alias -> alias, Atom
        | None -> baseName, Atom
      | _ -> generic ()

  /// What to show when the name is not shaped like a runtime type name: it, without a backtick, or a word when nothing is left.
  let fallback (text: string) : string =
    match text.Replace("`", "").Trim() with
    | "" -> "unknown"
    | kept -> kept

  /// The F# spelling of a runtime type name (`Double` is `float`, `FSharpList`1[Int32]` is `int list`, `Tuple`2[Int32,String]` is
  /// `int * string`). Never empty and never carries a backtick, whatever it is given: text that is not a runtime type name comes
  /// back as it was, minus any backtick.
  let display (runtimeName: string) : string =
    match parseType runtimeName 0 with
    | Some(shape, next) when next = runtimeName.Length -> fallback (fst (spell shape))
    | Some _
    | None -> fallback runtimeName

  /// The F# spelling of a type.
  let ofType (t: Type) : string = display (runtimeName t)

/// A float as F# source spells it: `80.0`, not `80`, so a value reads the same wherever it is drawn. The shortest text that reads
/// back as the same number (`R`), with a `.0` when it would otherwise look like an integer.
module FSharpFloat =
  let text (value: float) : string =
    let shortest = value.ToString("R", CultureInfo.InvariantCulture).Replace('E', 'e')
    match shortest |> Seq.forall (fun c -> Char.IsDigit c || c = '-') with
    | true -> shortest + ".0"
    | false -> shortest
