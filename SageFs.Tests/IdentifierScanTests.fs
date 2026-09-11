module SageFs.Tests.IdentifierScanTests

open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.IdentifierScan

// The regexes the eval-history analysis used before IdentifierScan. They are
// the oracle: the scanner must agree with them on every input.
let private wordBoundedRegex (name: string) (source: string) =
  Regex.IsMatch(source, @"\b" + Regex.Escape name + @"\b")

let private freeIdentifierRegex (name: string) (source: string) =
  Regex.IsMatch(source, @"(?<![.\w])" + Regex.Escape name + @"(?![.\w])")

// Characters chosen to stress every edge the regexes care about: ASCII word
// chars, '.', quotes, backticks, operators, whitespace, a Latin-1 letter, a
// combining mark (Mn), a spacing mark (Mc), ZWJ/ZWNJ, a connector punctuation
// and a lone surrogate.
let private alphabet =
  [| 'a'; 'b'; 'x'; 'y'; 'A'; '1'; '_'; '.'; '\''; '`'; '('; ')'; '|'; '+'
     ' '; '\n'; '"'; '\u00E9'; '\u0301'; '\u0903'; '\u200C'; '\u200D'; '\u203F'; '\uD800' |]

let private genText maxLen =
  gen {
    let! len = Gen.choose (0, maxLen)
    let! chars = Gen.arrayOfLength len (Gen.elements alphabet)
    return System.String chars
  }

/// Names are drawn both at random and as substrings of the source, so that
/// matches (not just misses) are exercised heavily.
let private genCase =
  gen {
    let! source = genText 24
    let! fromSource = Gen.elements [ true; false ]
    let! name =
      match fromSource && source.Length > 0 with
      | true ->
        gen {
          let! start = Gen.choose (0, source.Length - 1)
          let! len = Gen.choose (0, source.Length - start)
          return source.Substring(start, len)
        }
      | false -> genText 4
    return name, source
  }

let private config = { FsCheckConfig.defaultConfig with maxTest = 3000 }

[<Tests>]
let identifierScanTests =
  testList "IdentifierScan" [
    testCase "word-char classes agree with Regex for every UTF-16 code unit" <| fun _ ->
      let wordRe = Regex(@"^\w$")
      let boundaryRe = Regex(@"^\b")
      let mismatches =
        [ for i in 0 .. 0xFFFF do
            let c = char i
            let s = string c
            if isRegexWordChar c <> wordRe.IsMatch s then yield sprintf "\\w U+%04X" i
            if isBoundaryWordChar c <> boundaryRe.IsMatch s then yield sprintf "\\b U+%04X" i ]
      mismatches |> List.truncate 10 |> Expect.isEmpty "no character is classified differently from Regex"

    testPropertyWithConfig config "occursWordBounded equals the \\bNAME\\b regex" <|
      Prop.forAll (Arb.fromGen genCase) (fun (name, source) ->
        occursWordBounded name source = wordBoundedRegex name source)

    testPropertyWithConfig config "occursAsFreeIdentifier equals the (?<![.\\w])NAME(?![.\\w]) regex" <|
      Prop.forAll (Arb.fromGen genCase) (fun (name, source) ->
        occursAsFreeIdentifier name source = freeIdentifierRegex name source)

    testPropertyWithConfig config "for word-only names, boundaryWords membership equals the regex" <|
      Prop.forAll (Arb.fromGen genCase) (fun (name, source) ->
        (not (isBoundaryWordToken name))
        || (Array.contains name (boundaryWords source) = wordBoundedRegex name source))

    testPropertyWithConfig config "for word-only names, freeIdentifiers membership equals the regex" <|
      Prop.forAll (Arb.fromGen genCase) (fun (name, source) ->
        (not (isFreeIdentifierToken name))
        || (Array.contains name (freeIdentifiers source) = freeIdentifierRegex name source))

    testCase "dotted access and longer words are not free identifiers" <| fun _ ->
      let source = "let y = x.Length + xs.Head + Foo.x"
      occursAsFreeIdentifier "x" source |> Expect.isFalse "x only appears dotted or inside xs"
      occursWordBounded "x" source |> Expect.isTrue "\\b treats '.' as a boundary"
      freeIdentifiers source |> Array.contains "y" |> Expect.isTrue "y is a free identifier"

    testCase "operator and multi-word names are matched literally" <| fun _ ->
      occursWordBounded "mutable x" "let mutable x = 1" |> Expect.isTrue "multi-word name"
      occursWordBounded "(+.)" "a(+.)b" |> Expect.isTrue "\\b before '(' needs a word char before it"
      occursWordBounded "(+.)" "let (+.) a b = a" |> Expect.isFalse "space before '(' is not a boundary"
  ]
