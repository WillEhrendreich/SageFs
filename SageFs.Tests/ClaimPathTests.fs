module SageFs.Tests.ClaimPathTests

/// Claim paths are canonicalized before anything compares them. Until now
/// `ClaimScope.normalize` only swapped `\` for `/`, so `file:src/Foo/../Bar/x.fs`
/// did not overlap `file:src/Bar/x.fs`: a claim could be held twice, and a
/// scope-prefix check done with `StartsWith` would let `src/Foo/../Bar` through.
/// (nehemiah-cohort-requests-response-2026-10-02.md, B1: "Claim paths are not
/// canonicalized, so `..` bypasses exclusivity and any prefix scope".)

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Cohort
open SageFs.Tests.SharedGenerators

let private canonical (raw: string) : Result<string, PathRefusal> = ClaimPath.tryCanonical raw

/// A path built from names, `.`, `..`, empty segments (`//`) and either separator.
let private genSegment =
  Gen.frequency [
    6, Gen.elements [ "src"; "Foo"; "Bar"; "x.fs"; "SageFs.Core"; "a" ]
    2, Gen.constant ".."
    2, Gen.constant "."
    1, Gen.constant ""
  ]

let private genSeparator = Gen.elements [ "/"; "\\" ]

let private genRawPath =
  gen {
    let! count = Gen.choose (1, 7)
    let! segments = Gen.listOfLength count genSegment
    let! separators = Gen.listOfLength count genSeparator
    let! trailing = Gen.elements [ ""; "/" ]
    return (List.zip segments separators |> List.map (fun (s, sep) -> s + sep) |> String.concat "") + trailing
  }

type private PathGenerators =
  static member Raw() = Arb.fromGen genRawPath

let private pathConfig = { propConfig with arbitrary = [ typeof<PathGenerators> ] }

/// The spec: walk the segments with a stack, `..` pops, and a pop of nothing escapes.
let private specCanonical (raw: string) : Result<string, string> =
  let slashed = raw.Replace('\\', '/')
  let segments = slashed.Split('/') |> Array.filter (fun s -> s <> "" && s <> ".")
  let walk () =
    segments
    |> Array.fold
      (fun acc segment ->
        match acc, segment with
        | Error e, _ -> Error e
        | Ok stack, ".." ->
          match stack with
          | [] -> Error "escapes"
          | _ :: rest -> Ok rest
        | Ok stack, name -> Ok(name :: stack))
      (Ok [])
  let folded = if slashed.StartsWith "/" then Error "absolute" else walk ()
  folded |> Result.map (fun stack -> stack |> List.rev |> String.concat "/")

[<Tests>]
let tests =
  testList "Claim path canonicalization" [

    testCase "WHY - the detour through a sibling directory is the sibling" <| fun () ->
      canonical "src/Foo/../Bar/x.fs" |> Expect.equal "src/Foo/../Bar/x.fs is src/Bar/x.fs" (Ok "src/Bar/x.fs")

    testCase "WHY - separators, dots and doubled slashes all collapse" <| fun () ->
      canonical ".\\src//Foo/./x.fs" |> Expect.equal "one canonical spelling" (Ok "src/Foo/x.fs")

    testCase "WHY - a trailing slash does not change the directory" <| fun () ->
      canonical "src/Foo/" |> Expect.equal "src/Foo/ is src/Foo" (Ok "src/Foo")

    testCase "WHY - climbing out of the repo is refused, not clamped to the root" <| fun () ->
      canonical "../x.fs" |> Expect.equal "leading .." (Error(PathRefusal.EscapesRoot "../x.fs"))
      canonical "src/../../x.fs" |> Expect.equal "one .. too many" (Error(PathRefusal.EscapesRoot "src/../../x.fs"))

    testCase "WHY - a rooted path is not repo-relative" <| fun () ->
      canonical "/etc/passwd" |> Expect.equal "unix root" (Error(PathRefusal.Absolute "/etc/passwd"))
      canonical "C:\\Users\\x.fs" |> Expect.equal "drive letter" (Error(PathRefusal.Absolute "C:\\Users\\x.fs"))
      canonical "\\\\host\\share\\x.fs" |> Expect.equal "UNC" (Error(PathRefusal.Absolute "\\\\host\\share\\x.fs"))

    testCase "WHY - a path that goes down and comes back is the repo root" <| fun () ->
      canonical "src/.." |> Expect.equal "src/.. is the root" (Ok "")

    testPropertyWithConfig pathConfig "canonicalization agrees with the stack-walk spec on any path" <| fun (raw: string) ->
      match canonical raw, specCanonical raw with
      | Ok actual, Ok expected -> actual = expected
      | Error(PathRefusal.EscapesRoot _), Error "escapes" -> true
      | Error(PathRefusal.Absolute _), Error "absolute" -> true
      | _ -> false

    testPropertyWithConfig pathConfig "a canonical path has no dot segments, no doubled or trailing slash and no backslash" <| fun (raw: string) ->
      match canonical raw with
      | Error _ -> true
      | Ok path ->
        let segments = path.Split('/')
        not (path.Contains "\\")
        && not (path.StartsWith "/")
        && not (path.EndsWith "/")
        && (path = "" || segments |> Array.forall (fun s -> s <> "" && s <> "." && s <> ".."))

    testPropertyWithConfig pathConfig "canonicalization is idempotent" <| fun (raw: string) ->
      match canonical raw with
      | Error _ -> true
      | Ok path -> canonical path = Ok path

    testCase "WHY - the detour no longer hides a claim: File overlaps File across a .." <| fun () ->
      ClaimScope.overlaps (ClaimScope.File "src/Foo/../Bar/x.fs") (ClaimScope.File "src/Bar/x.fs")
      |> Expect.isTrue "both name src/Bar/x.fs"

    testCase "WHY - a detour into a project directory is a claim on the project" <| fun () ->
      ClaimScope.overlaps (ClaimScope.File "src/Foo/../Bar/x.fs") (ClaimScope.Project "src/Bar/Bar.fsproj")
      |> Expect.isTrue "the file is under src/Bar"
      ClaimScope.overlaps (ClaimScope.File "src/Foo/../Bar/x.fs") (ClaimScope.Project "src/Foo/Foo.fsproj")
      |> Expect.isFalse "and not under src/Foo, whatever its spelling starts with"

    testPropertyWithConfig pathConfig "overlap does not depend on how a path is spelled" <| fun (raw: string) ->
      match canonical raw with
      | Error _ -> true
      | Ok path ->
        ClaimScope.overlaps (ClaimScope.File raw) (ClaimScope.File path)
        || path = ""

    testCase "WHY - ClaimScope.tryCanonical rewrites both cases and refuses an escape" <| fun () ->
      ClaimScope.tryCanonical (ClaimScope.File "src/Foo/../Bar/x.fs")
      |> Expect.equal "file" (Ok(ClaimScope.File "src/Bar/x.fs"))
      ClaimScope.tryCanonical (ClaimScope.Project "./src\\Bar/Bar.fsproj")
      |> Expect.equal "project" (Ok(ClaimScope.Project "src/Bar/Bar.fsproj"))
      ClaimScope.tryCanonical (ClaimScope.File "../x.fs")
      |> Expect.equal "escape" (Error(PathRefusal.EscapesRoot "../x.fs"))
  ]
