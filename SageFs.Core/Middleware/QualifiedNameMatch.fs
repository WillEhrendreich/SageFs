/// When one qualified name stands for another, and when a name stands for more than one.
///
/// A detour pairs an older copy of a function with the new definition by qualified name. The copies do not
/// always carry the same prefix: FSI evaluates a namespace-less copy (`Util.helper`) of a function the project
/// compiled as `App.Util.helper`, and those two are the same function. But `MyUtil.helper` is NOT `Util.helper`
/// just because the letters match at the end, so a name only stands for a longer one when the longer one
/// ends at a dot: `App.Util.helper` ends at `.Util.helper`.
///
/// That still leaves the case where two different modules both end in the new name (`A.Util.helper` and
/// `B.Util.helper`, new `Util.helper`). Nothing in the name says which one the user edited, so neither is
/// re-pointed and both are named.
///
/// Pure and BCL only: it is compiled into the isolated FSI host with HotReloadCore.
module SageFs.Middleware.QualifiedNameMatch

open System

/// True when `longer` is `shorter`, or `longer` ends with a dot and `shorter`.
let endsAtSegment (longer: string) (shorter: string) : bool =
  String.Equals(longer, shorter, StringComparison.Ordinal)
  || (longer.Length > shorter.Length
      && longer.EndsWith("." + shorter, StringComparison.Ordinal))

/// A new definition that more than one differently-named older definition would be re-pointed onto.
type Ambiguity = {
  /// The qualified name of the new definition.
  NewName: string
  /// The qualified names of the older definitions that all end at a segment boundary in it, sorted.
  OlderNames: string list
}

/// Splits planned (older, newer) pairs into the pairs that are safe and the new definitions whose pairing is
/// ambiguous. A new definition is ambiguous when two or more DIFFERENT older names, each longer than the new
/// name, are paired with it. Older copies with the new definition's own name never count: an earlier FSI copy
/// and the compiled copy are one function, so both still move. Every pair of an ambiguous new definition is
/// dropped, so the answer fails closed.
let separateAmbiguous
  (fullName: 'm -> string)
  (identity: 'm -> 'id)
  (pairs: ('m * 'm) list)
  : ('m * 'm) list * Ambiguity list =
  let ambiguous =
    pairs
    |> List.groupBy (snd >> identity)
    |> List.choose (fun (id, group) ->
      let newName = fullName (snd group.Head)
      let longer =
        group
        |> List.map (fst >> fullName)
        |> List.filter (fun name -> name <> newName)
        |> List.distinct
        |> List.sort
      match longer with
      | _ :: _ :: _ -> Some(id, { NewName = newName; OlderNames = longer })
      | _ -> None)
  let blocked = ambiguous |> List.map fst
  pairs |> List.filter (fun (_, newer) -> not (List.contains (identity newer) blocked)),
  ambiguous |> List.map snd

module Ambiguity =
  /// What the user is told: the new name, every definition that could have been meant, and that nothing moved.
  let describe (ambiguity: Ambiguity) : string =
    sprintf
      "'%s' was not re-pointed: more than one definition ends in that name (%s), so SageFs cannot tell which one you edited"
      ambiguity.NewName
      (String.Join(", ", ambiguity.OlderNames))
