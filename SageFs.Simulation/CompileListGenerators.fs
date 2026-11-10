namespace SageFs.Simulation

open System
open SageFs.Simulation.CompileListSim

/// Seeded generators for `CompileListSim` scenarios. Chaos is data: `fromSeed n` replays identically forever. Every project is
/// valid by construction: a file mentions only names that files before it declare, so the project compiles in the order given.
module CompileListGenerators =

  let nameOf (index: int) : string = sprintf "m%d" index

  /// A project of `count` files. Each file declares one name, or is opaque, and mentions a seeded subset of the names declared above it.
  let project (rnd: Random) (count: int) : SimFile list =
    [ 0 .. count - 1 ]
    |> List.fold (fun (acc: SimFile list) i ->
      let declared = acc |> List.collect (fun file -> Set.toList file.Declares)
      let mentions =
        declared
        |> List.filter (fun _ -> rnd.Next 4 = 0)
        |> Set.ofList
      let opaque = rnd.Next 10 = 0
      let file =
        { Name = sprintf "f%d.fs" i
          Content = rnd.Next 1000
          Declares = (match opaque with true -> Set.empty | false -> Set.singleton (nameOf i))
          Opaque = opaque
          Mentions = mentions }
      acc @ [ file ]) []

  /// A new file placed at `index`: it mentions names declared above that point, and declares a fresh name or (one time in four) one
  /// that an existing file declares, so it shadows it for the files that name it.
  let newFile (rnd: Random) (project: SimFile list) (index: int) (serial: int) : SimFile =
    let above = project |> List.truncate index
    let declaredAbove = above |> List.collect (fun file -> Set.toList file.Declares)
    let declaredAnywhere = project |> List.collect (fun file -> Set.toList file.Declares)
    let opaque = rnd.Next 12 = 0
    let declares =
      match opaque, declaredAnywhere with
      | true, _ -> Set.empty
      | false, existing when not existing.IsEmpty && rnd.Next 4 = 0 -> Set.singleton (List.item (rnd.Next existing.Length) existing)
      | false, _ -> Set.singleton (sprintf "new%d" serial)
    { Name = sprintf "new%d.fs" serial
      Content = rnd.Next 1000
      Declares = declares
      Opaque = opaque
      Mentions = declaredAbove |> List.filter (fun _ -> rnd.Next 4 = 0) |> Set.ofList }

  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let before = project rnd (rnd.Next(3, 41))
    let edit =
      match rnd.Next 5 with
      | 0 ->
        // Append, the common case.
        Edit.Insert [ for k in 0 .. rnd.Next 3 -> before.Length + k, newFile rnd before before.Length (seed * 10 + k) ]
      | 1 | 2 ->
        // Insert in the middle.
        let index = rnd.Next (before.Length + 1)
        Edit.Insert [ index, newFile rnd before index seed ]
      | 3 -> Edit.Remove [ (List.item (rnd.Next before.Length) before).Name ]
      | _ -> Edit.Move (rnd.Next before.Length, rnd.Next before.Length)
    { Seed = seed; Before = before; Edit = edit }

  /// A hand-made case where the transitive step matters: `g` names `util`, `util` is shadowed by a new file placed before `util`, and
  /// `h` names only `g`. Nothing in `h` changed and `h` cannot see the new file, yet `h` sits on a signature that did.
  let transitiveChain : Scenario =
    let file name declares mentions =
      { Name = name; Content = hash name; Declares = Set.ofList declares; Opaque = false; Mentions = Set.ofList mentions }
    let before = [ file "a.fs" [ "A" ] []; file "util.fs" [ "Util" ] []; file "g.fs" [ "G" ] [ "Util" ]; file "h.fs" [ "H" ] [ "G" ] ]
    { Seed = 7; Before = before; Edit = Edit.Insert [ 1, file "shadow.fs" [ "Util" ] [] ] }

  /// A hand-made case for the headline claim: a plain new file nobody names, put in the middle of a long list.
  let unnamedInsertion : Scenario =
    let rnd = Random(11)
    let before = project rnd 30
    { Seed = 11
      Before = before
      Edit = Edit.Insert [ 10, { Name = "unnamed.fs"; Content = 1; Declares = Set.singleton "Unnamed"; Opaque = false; Mentions = Set.empty } ] }
