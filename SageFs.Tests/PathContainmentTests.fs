module SageFs.Tests.PathContainmentTests

/// "Is this path inside that directory" has one definition, `PathContainment`. A text prefix
/// test says `/a/repo2/x.fs` is inside `/a/repo`; containment is about whole path segments.
///
/// The property states the rule from .NET's own `Path.GetRelativePath`, which shares no code
/// with `PathContainment`: a path is under a root exactly when its root-relative form is not
/// above the root (its first segment is not `..`) and is not on another tree (still rooted).

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.PathContainment

let private propConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private underRoot = isWithin PathCase.Sensitive

// ── the examples ────────────────────────────────────────────────────

[<Tests>]
let examples =
  testList "PathContainment examples" [

    testCase "WHY — a file inside the root is inside it" <| fun _ ->
      underRoot "/a/repo" "/a/repo/x.fs" |> Expect.isTrue "/a/repo/x.fs is under /a/repo"

    testCase "WHY — a sibling directory whose name starts with the root's name is not inside it" <| fun _ ->
      underRoot "/a/repo" "/a/repo2/x.fs" |> Expect.isFalse "/a/repo2/x.fs is a sibling of /a/repo"

    testCase "WHY — the root is inside itself, with or without a trailing separator" <| fun _ ->
      [ "/a/repo", "/a/repo"
        "/a/repo/", "/a/repo"
        "/a/repo", "/a/repo/"
        "/a/repo/", "/a/repo/" ]
      |> List.iter (fun (root, path) ->
        underRoot root path |> Expect.isTrue (sprintf "%s is %s" path root))

    testCase "WHY — a trailing separator on the root does not hide its files" <| fun _ ->
      underRoot "/a/repo/" "/a/repo/x.fs" |> Expect.isTrue "/a/repo/x.fs is under /a/repo/"

    testCase "WHY — a file that climbs out and into the sibling is the sibling's" <| fun _ ->
      underRoot "/a/repo" "/a/repo/../repo2/x.fs" |> Expect.isFalse "the .. segment leaves /a/repo"

    testCase "WHY — a .. segment that stays inside the root changes nothing" <| fun _ ->
      underRoot "/a/repo" "/a/repo/sub/../x.fs" |> Expect.isTrue "/a/repo/sub/../x.fs is /a/repo/x.fs"

    testCase "WHY — a dot-dot prefix in a name is a name, not a climb" <| fun _ ->
      underRoot "/a/repo" "/a/repo/..hidden/x.fs" |> Expect.isTrue "..hidden is an ordinary directory"

    testCase "WHY — the filesystem root contains every path" <| fun _ ->
      underRoot "/" "/a/repo/x.fs" |> Expect.isTrue "everything is under /"

    testCase "WHY — case is compared per platform: sensitive keeps /a/Repo apart, insensitive joins them" <| fun _ ->
      isWithin PathCase.Sensitive "/a/repo" "/a/Repo/x.fs" |> Expect.isFalse "sensitive"
      isWithin PathCase.Insensitive "/a/repo" "/a/Repo/x.fs" |> Expect.isTrue "insensitive"

    testCase "WHY — a directory is not strictly under itself, and a file in it is" <| fun _ ->
      isStrictlyUnder PathCase.Sensitive "/a/repo" "/a/repo" |> Expect.isFalse "the root itself"
      isStrictlyUnder PathCase.Sensitive "/a/repo" "/a/repo/" |> Expect.isFalse "the root with a separator"
      isStrictlyUnder PathCase.Sensitive "/a/repo" "/a/repo/x.fs" |> Expect.isTrue "a file inside"
      isStrictlyUnder PathCase.Sensitive "/a/repo" "/a/repo2/x.fs" |> Expect.isFalse "the sibling"

    testCase "WHY — a deeper directory is deeper whatever its trailing separator" <| fun _ ->
      depth "/a/b/" |> Expect.equal "same depth as without the separator" (depth "/a/b")
      (depth "/a/b/c" > depth "/a/b") |> Expect.isTrue "a child is deeper"
  ]

[<Tests>]
let canonicalExamples =
  testList "PathContainment canonical (repo-relative) paths" [

    testCase "WHY — a canonical file under a canonical prefix is inside it, and a sibling by name is not" <| fun _ ->
      isWithinCanonical "src/Foo" "src/Foo/x.fs" |> Expect.isTrue "inside"
      isWithinCanonical "src/Foo" "src/Foo" |> Expect.isTrue "the prefix itself"
      isWithinCanonical "src/Foo" "src/Foobar/x.fs" |> Expect.isFalse "src/Foobar is a sibling of src/Foo"

    testCase "WHY — the empty prefix is the whole repo" <| fun _ ->
      isWithinCanonical "" "src/Foo/x.fs" |> Expect.isTrue "everything is in the repo"
      isWithinCanonical "" "" |> Expect.isTrue "the root is in the repo"
  ]

// ── the property ────────────────────────────────────────────────────

/// Small alphabet on purpose: names that are prefixes of each other (`a`, `ab`, `repo`,
/// `repo2`), a climb, a no-op and a name that begins with dots, so collisions are common.
let private genSegment =
  Gen.elements [ "a"; "ab"; "repo"; "repo2"; "R"; ".."; "."; "..x" ]

let private genPath : Gen<string> =
  gen {
    let! segments = Gen.listOfLength 4 genSegment |> Gen.bind (fun all -> Gen.choose (0, 4) |> Gen.map (fun n -> List.truncate n all))
    let! trailing = Gen.elements [ ""; "/" ]
    return "/" + String.Join("/", segments) + trailing
  }

/// The rule, from .NET's relative-path arithmetic and nothing of ours.
let private oracle (root: string) (path: string) : bool =
  let relative = Path.GetRelativePath(root, path)
  let firstSegment = relative.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries) |> Array.tryHead
  not (Path.IsPathRooted relative)
  && (match firstSegment with
      | Some ".." -> false
      | Some _
      | None -> true)

[<Tests>]
let properties =
  testList "PathContainment properties" [

    testPropertyWithConfig propConfig
      "WHY — a path is under a root exactly when its root-relative form does not climb out of it"
      <| fun () ->
        Prop.forAll (Arb.fromGen (Gen.zip genPath genPath)) (fun (root, path) ->
          let case = PathCase.ofCurrentOs ()
          isWithin case root path = oracle root path)

    testPropertyWithConfig propConfig
      "WHY — the canonical (claim path) form decides exactly as the filesystem form does"
      <| fun () ->
        let genCanonical =
          Gen.elements [ "a"; "ab"; "repo"; "repo2" ]
          |> Gen.listOfLength 3
          |> Gen.bind (fun all -> Gen.choose (0, 3) |> Gen.map (fun n -> String.Join("/", List.truncate n all)))
        Prop.forAll (Arb.fromGen (Gen.zip genCanonical genCanonical)) (fun (prefix, path) ->
          isWithinCanonical prefix path = isWithin PathCase.Sensitive ("/" + prefix) ("/" + path))

    testPropertyWithConfig propConfig
      "WHY — containment is reflexive and a child of a contained path is contained"
      <| fun () ->
        Prop.forAll (Arb.fromGen (Gen.zip genPath genSegment)) (fun (root, name) ->
          let case = PathCase.ofCurrentOs ()
          match name with
          | ".."
          | "." -> isWithin case root root
          | _ -> isWithin case root root && isWithin case root (root.TrimEnd('/') + "/" + name))
  ]
