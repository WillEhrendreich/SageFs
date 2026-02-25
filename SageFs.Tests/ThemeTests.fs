module SageFs.Tests.ThemeTests

open Expecto
open Expecto.Flip
open SageFs

[<Tests>]
let themeTests = testList "Theme" [
  testList "hexToRgb" [
    testCase "parses standard hex color" <| fun () ->
      Theme.hexToRgb "#ff0000"
      |> Expect.equal "should be red" 0xFF0000u

    testCase "parses blue" <| fun () ->
      Theme.hexToRgb "#0000ff"
      |> Expect.equal "should be blue" 0x0000FFu

    testCase "parses white" <| fun () ->
      Theme.hexToRgb "#ffffff"
      |> Expect.equal "should be white" 0xFFFFFFu

    testCase "parses black" <| fun () ->
      Theme.hexToRgb "#000000"
      |> Expect.equal "should be black" 0u

    testCase "returns 0 for invalid input" <| fun () ->
      Theme.hexToRgb "not-a-color"
      |> Expect.equal "should be 0" 0u

    testCase "returns 0 for empty string" <| fun () ->
      Theme.hexToRgb ""
      |> Expect.equal "should be 0" 0u
  ]

  testList "rgb component extraction" [
    testCase "extracts red channel" <| fun () ->
      Theme.rgbR 0xFF8040u
      |> Expect.equal "should be 0xFF" 0xFFuy

    testCase "extracts green channel" <| fun () ->
      Theme.rgbG 0xFF8040u
      |> Expect.equal "should be 0x80" 0x80uy

    testCase "extracts blue channel" <| fun () ->
      Theme.rgbB 0xFF8040u
      |> Expect.equal "should be 0x40" 0x40uy
  ]

  testList "tokenColorOfCapture" [
    testCase "maps keyword to SynKeyword" <| fun () ->
      Theme.tokenColorOfCapture Theme.defaults "keyword"
      |> Expect.equal "should be keyword color" Theme.defaults.SynKeyword

    testCase "maps keyword.control to SynKeyword" <| fun () ->
      Theme.tokenColorOfCapture Theme.defaults "keyword.control"
      |> Expect.equal "should be keyword color" Theme.defaults.SynKeyword

    testCase "maps string to SynString" <| fun () ->
      Theme.tokenColorOfCapture Theme.defaults "string"
      |> Expect.equal "should be string color" Theme.defaults.SynString

    testCase "maps comment to SynComment" <| fun () ->
      Theme.tokenColorOfCapture Theme.defaults "comment"
      |> Expect.equal "should be comment color" Theme.defaults.SynComment

    testCase "maps type to SynType" <| fun () ->
      Theme.tokenColorOfCapture Theme.defaults "type"
      |> Expect.equal "should be type color" Theme.defaults.SynType

    testCase "maps function to SynFunction" <| fun () ->
      Theme.tokenColorOfCapture Theme.defaults "function"
      |> Expect.equal "should be function color" Theme.defaults.SynFunction

    testCase "maps variable.member to SynProperty" <| fun () ->
      Theme.tokenColorOfCapture Theme.defaults "variable.member"
      |> Expect.equal "should be property color" Theme.defaults.SynProperty

    testCase "maps variable to SynVariable" <| fun () ->
      Theme.tokenColorOfCapture Theme.defaults "variable"
      |> Expect.equal "should be variable color" Theme.defaults.SynVariable

    testCase "maps attribute to SynAttribute" <| fun () ->
      Theme.tokenColorOfCapture Theme.defaults "attribute"
      |> Expect.equal "should be attribute color" Theme.defaults.SynAttribute

    testCase "maps unknown to FgDefault" <| fun () ->
      Theme.tokenColorOfCapture Theme.defaults "unknown_token"
      |> Expect.equal "should be default" Theme.defaults.FgDefault

    testCase "maps spell to FgDefault" <| fun () ->
      Theme.tokenColorOfCapture Theme.defaults "spell"
      |> Expect.equal "should be default" Theme.defaults.FgDefault
  ]

  testList "withOverrides" [
    testCase "applies single override" <| fun () ->
      let overrides = Map.ofList [ "fgDefault", "#111111" ]
      let result = Theme.withOverrides overrides Theme.defaults
      result.FgDefault |> Expect.equal "should be overridden" "#111111"
      result.FgDim |> Expect.equal "should keep default" Theme.defaults.FgDim

    testCase "empty overrides returns same config" <| fun () ->
      let result = Theme.withOverrides Map.empty Theme.defaults
      result |> Expect.equal "should equal defaults" Theme.defaults

    testCase "applies multiple overrides" <| fun () ->
      let overrides = Map.ofList [
        "fgDefault", "#aaaaaa"
        "bgDefault", "#bbbbbb"
        "synKeyword", "#cccccc"
      ]
      let result = Theme.withOverrides overrides Theme.defaults
      result.FgDefault |> Expect.equal "should override fg" "#aaaaaa"
      result.BgDefault |> Expect.equal "should override bg" "#bbbbbb"
      result.SynKeyword |> Expect.equal "should override syn" "#cccccc"
  ]

  testList "toCssVariables" [
    testCase "contains all expected CSS variables" <| fun () ->
      let css = Theme.toCssVariables Theme.defaults
      css |> Expect.stringContains "should have fg-default" "--fg-default:"
      css |> Expect.stringContains "should have bg-default" "--bg-default:"
      css |> Expect.stringContains "should have syn-keyword" "--syn-keyword:"

    testCase "uses actual theme values" <| fun () ->
      let css = Theme.toCssVariables Theme.defaults
      css |> Expect.stringContains "should have default white fg" "#ffffff"
      css |> Expect.stringContains "should have default black bg" "#000000"
  ]

  testList "parseConfigLines" [
    testCase "parses theme definition" <| fun () ->
      let lines = [|
        """let theme = [ "fgDefault", "#aabbcc" ]"""
      |]
      let result = Theme.parseConfigLines lines
      result |> Map.tryFind "fgDefault"
      |> Expect.equal "should find override" (Some "#aabbcc")

    testCase "parses multiline theme definition" <| fun () ->
      let lines = [|
        """let theme = ["""
        """  "fgDefault", "#aabbcc" """
        """  "bgDefault", "#112233" """
        """]"""
      |]
      let result = Theme.parseConfigLines lines
      result |> Map.tryFind "fgDefault"
      |> Expect.equal "should find fgDefault" (Some "#aabbcc")
      result |> Map.tryFind "bgDefault"
      |> Expect.equal "should find bgDefault" (Some "#112233")

    testCase "ignores non-hex values" <| fun () ->
      let lines = [|
        """let theme = [ "fgDefault", "notahex" ]"""
      |]
      let result = Theme.parseConfigLines lines
      result |> Map.tryFind "fgDefault"
      |> Expect.equal "should not find non-hex" None

    testCase "returns empty map for no theme lines" <| fun () ->
      let lines = [| "let x = 42"; "let y = 100" |]
      let result = Theme.parseConfigLines lines
      result |> Map.isEmpty
      |> Expect.isTrue "should be empty"
  ]
]
