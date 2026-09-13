module SageFs.Tests.MemberTableTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.MemberTable

/// RED tests for sagefs-multiagent-vision.md §4.1 / §10 Phase 0 item 4:
/// `MemberId` is the bound-connection identity — a caller's self-declared
/// name never determines the key.
[<Tests>]
let tests = testList "MemberTable.MemberId" [

  testCase "WHY — Minted renders verbatim so an unbound caller's key equals its plain name" <| fun () ->
    MemberId.display (MemberId.Minted "claude")
    |> Expect.equal "Minted must not add a prefix — every existing name-keyed call site depends on this" "claude"

  testCase "WHY — Mcp and Browser get distinguishing prefixes so they can never collide with a Minted name" <| fun () ->
    MemberId.display (MemberId.Mcp "conn-123")
    |> Expect.equal "Mcp prefix" "mcp:conn-123"
    MemberId.display (MemberId.Browser "tab-1")
    |> Expect.equal "Browser prefix" "browser:tab-1"

  testCase "WHY — two Mcp members with the same self-declared display, but different connections, are different keys" <| fun () ->
    let a = MemberId.display (MemberId.Mcp "conn-A")
    let b = MemberId.display (MemberId.Mcp "conn-B")
    (a <> b) |> Expect.isTrue "distinct connections must resolve to distinct keys"

  testCase "WHY — a Browser and an Mcp member sharing the same raw id never collide" <| fun () ->
    let browser = MemberId.display (MemberId.Browser "x")
    let mcp = MemberId.display (MemberId.Mcp "x")
    (browser <> mcp) |> Expect.isTrue "kind-tagged prefixes keep the two identity spaces disjoint"

  testCase "WHY — MemberId equality is structural (two Minted with the same name are the SAME member)" <| fun () ->
    (MemberId.Minted "claude" = MemberId.Minted "claude")
    |> Expect.isTrue "an unbound caller using the same name twice IS the same member — no ambient connection to distinguish them"
]
