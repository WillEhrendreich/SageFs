module SageFs.Tests.CallerStateTests

open Expecto
open Expecto.Flip
open SageFs.Features.CallerState

/// A save re-signs a function and the callers saved with it move onto the new method. A caller in ANOTHER file keeps
/// calling the old method until that file is saved too. These cases pin the named state that says so: who is still on the
/// old method, where, what to do, and exactly when it clears.

let private edit (declaration: string) (cause: SignatureCause) (file: string) : SignatureEdit =
  { Declaration = declaration; Cause = cause; File = file }

let private site (file: string) (line: int) (caller: string) : CallSite =
  { File = file; Line = line; Caller = caller; Evidence = SiteEvidence.ResolvedByCompiler }

let private callers (first: CallSite) (rest: CallSite list) = CallersCheck.Callers(first, rest)

let private tags = edit "Shop.Tags.stamp" SignatureCause.ReSigned "/p/Tags.fs"

let private pagesSite = site "/p/Pages.fs" 12 "Shop.Pages.render"

let private checkedWith (checks: (SignatureEdit * CallersCheck) list) (ledger: CallerLedger) =
  CallerLedger.apply (LedgerEvent.Checked checks) ledger

let private pendingFiles (state: CallersState) : string list =
  match state with
  | CallersState.CallersPending(first, rest, _) ->
    first :: rest |> List.collect (fun p -> p.First :: p.Rest) |> List.map _.File |> List.distinct |> List.sort
  | CallersState.CallersCurrent
  | CallersState.CallersNotChecked _
  | CallersState.CallersNotReported -> []

[<Tests>]
let tests =
  testList "caller state: a re-signed declaration whose callers live in other files" [

    testList "what the ledger says" [
      testCase "WHY — a ledger nothing was recorded in is Current, never Pending" <| fun _ ->
        CallerLedger.empty |> CallerLedger.stateOf |> Expect.equal "nothing is pending" CallersState.CallersCurrent

      testCase "WHY — a re-signed declaration with a caller in another file is Pending, naming the file, the line and the caller" <| fun _ ->
        let state = CallerLedger.empty |> checkedWith [ tags, callers pagesSite [] ] |> CallerLedger.stateOf
        match state with
        | CallersState.CallersPending(first, [], []) ->
          first.Edit |> Expect.equal "the edit is the re-sign" tags
          first.First |> Expect.equal "the site names the caller's file, line and declaration" pagesSite
          first.Rest |> Expect.isEmpty "one site"
        | other -> failtestf "expected one pending declaration, got %A" other

      testCase "WHY — a declaration nothing outside its file calls leaves the ledger Current" <| fun _ ->
        CallerLedger.empty
        |> checkedWith [ tags, CallersCheck.NoCallers ]
        |> CallerLedger.stateOf
        |> Expect.equal "no callers means nothing to wait for" CallersState.CallersCurrent

      testCase "WHY — callers that could not be checked are NotChecked, said out loud, never Current" <| fun _ ->
        let state =
          CallerLedger.empty
          |> checkedWith [ tags, CallersCheck.NotChecked UncheckedReason.ProjectNotLoaded ]
          |> CallerLedger.stateOf
        match state with
        | CallersState.CallersNotChecked(first, []) ->
          first.Unresolved |> Expect.equal "names the declaration whose callers are unknown" tags
          first.Why |> Expect.equal "and why" UncheckedReason.ProjectNotLoaded
        | other -> failtestf "expected NotChecked, got %A" other

      testCase "WHY — pending sites and unchecked declarations are both kept when a save produced both" <| fun _ ->
        let other = edit "Shop.Tags.unstamp" SignatureCause.Removed "/p/Tags.fs"
        let state =
          CallerLedger.empty
          |> checkedWith [ tags, callers pagesSite []; other, CallersCheck.NotChecked UncheckedReason.ProjectNotLoaded ]
          |> CallerLedger.stateOf
        match state with
        | CallersState.CallersPending(_, [], [ unchecked ]) -> unchecked.Unresolved |> Expect.equal "the unchecked one rides along" other
        | other -> failtestf "expected Pending with one unchecked, got %A" other
    ]

    testList "when it clears" [
      testCase "WHY — saving the caller's file, with the caller's declaration patched, clears exactly that pending state" <| fun _ ->
        let ledger = CallerLedger.empty |> checkedWith [ tags, callers pagesSite [] ]
        ledger
        |> CallerLedger.apply (LedgerEvent.FileLanded("/p/Pages.fs", [ "Shop.Pages.render" ]))
        |> CallerLedger.stateOf
        |> Expect.equal "the caller landed, so nothing is pending" CallersState.CallersCurrent

      testCase "WHY — a save of some other file clears nothing" <| fun _ ->
        let ledger = CallerLedger.empty |> checkedWith [ tags, callers pagesSite [] ]
        ledger
        |> CallerLedger.apply (LedgerEvent.FileLanded("/p/Other.fs", [ "Shop.Pages.render" ]))
        |> CallerLedger.stateOf
        |> pendingFiles
        |> Expect.equal "Pages.fs is still on the old method" [ "/p/Pages.fs" ]

      testCase "WHY — a save of the caller's file that did not patch the declaration holding the call clears nothing" <| fun _ ->
        let ledger = CallerLedger.empty |> checkedWith [ tags, callers pagesSite [] ]
        ledger
        |> CallerLedger.apply (LedgerEvent.FileLanded("/p/Pages.fs", [ "Shop.Pages.somethingElse" ]))
        |> CallerLedger.stateOf
        |> pendingFiles
        |> Expect.equal "the call is still in a declaration that was not patched" [ "/p/Pages.fs" ]

      testCase "WHY — a site the planner could not place in a declaration clears when its file lands at all" <| fun _ ->
        let loose = site "/p/Pages.fs" 3 ""
        CallerLedger.empty
        |> checkedWith [ tags, callers loose [] ]
        |> CallerLedger.apply (LedgerEvent.FileLanded("/p/Pages.fs", []))
        |> CallerLedger.stateOf
        |> Expect.equal "file-level code lands with its file" CallersState.CallersCurrent

      testCase "WHY — with two caller files, each landing clears only its own, and the state is Pending until the last" <| fun _ ->
        let second = site "/p/Admin.fs" 40 "Shop.Admin.show"
        let ledger = CallerLedger.empty |> checkedWith [ tags, callers pagesSite [ second ] ]
        let afterFirst = ledger |> CallerLedger.apply (LedgerEvent.FileLanded("/p/Pages.fs", [ "Shop.Pages.render" ]))
        afterFirst |> CallerLedger.stateOf |> pendingFiles |> Expect.equal "Admin.fs is still pending" [ "/p/Admin.fs" ]
        afterFirst
        |> CallerLedger.apply (LedgerEvent.FileLanded("/p/Admin.fs", [ "Shop.Admin.show" ]))
        |> CallerLedger.stateOf
        |> Expect.equal "the last caller landing clears it" CallersState.CallersCurrent

      testCase "WHY — a caller file saved twice clears once and the second save changes nothing" <| fun _ ->
        let landed = LedgerEvent.FileLanded("/p/Pages.fs", [ "Shop.Pages.render" ])
        CallerLedger.empty
        |> checkedWith [ tags, callers pagesSite [] ]
        |> CallerLedger.apply landed
        |> CallerLedger.apply landed
        |> CallerLedger.stateOf
        |> Expect.equal "still Current" CallersState.CallersCurrent

      testCase "WHY — a restart starts a process that has no old methods, so everything clears, unchecked included" <| fun _ ->
        CallerLedger.empty
        |> checkedWith [ tags, callers pagesSite []; edit "Shop.Tags.x" SignatureCause.Removed "/p/Tags.fs", CallersCheck.NotChecked UncheckedReason.ProjectNotLoaded ]
        |> CallerLedger.apply LedgerEvent.AppRestarted
        |> CallerLedger.stateOf
        |> Expect.equal "a restarted app is current" CallersState.CallersCurrent
    ]

    testList "a later save of the declaring file" [
      testCase "WHY — re-signing again recomputes who calls it: a file that landed in between is pending again" <| fun _ ->
        let ledger =
          CallerLedger.empty
          |> checkedWith [ tags, callers pagesSite [] ]
          |> CallerLedger.apply (LedgerEvent.FileLanded("/p/Pages.fs", [ "Shop.Pages.render" ]))
          |> checkedWith [ tags, callers pagesSite [] ]
        ledger |> CallerLedger.stateOf |> pendingFiles |> Expect.equal "the second signature change strands Pages.fs again" [ "/p/Pages.fs" ]

      testCase "WHY — a body-only save of the declaring file records nothing, so what was pending stays pending" <| fun _ ->
        let ledger = CallerLedger.empty |> checkedWith [ tags, callers pagesSite [] ]
        ledger
        |> CallerLedger.apply (LedgerEvent.Checked [])
        |> CallerLedger.stateOf
        |> pendingFiles
        |> Expect.equal "the callers are still on the old method" [ "/p/Pages.fs" ]

      testCase "WHY — a re-sign whose recheck finds no callers any more clears that declaration's entry" <| fun _ ->
        CallerLedger.empty
        |> checkedWith [ tags, callers pagesSite [] ]
        |> checkedWith [ tags, CallersCheck.NoCallers ]
        |> CallerLedger.stateOf
        |> Expect.equal "nobody calls it now" CallersState.CallersCurrent

      testCase "WHY — a removal is pending the same way a re-sign is, and says it was removed" <| fun _ ->
        let removed = edit "Shop.Tags.stamp" SignatureCause.Removed "/p/Tags.fs"
        let state = CallerLedger.empty |> checkedWith [ removed, callers pagesSite [] ] |> CallerLedger.stateOf
        CallersState.describe state |> Expect.stringContains "says removed" "removed"
        pendingFiles state |> Expect.equal "Pages.fs still calls the removed method" [ "/p/Pages.fs" ]
    ]

    testList "what it says to a person" [
      let pending = CallerLedger.empty |> checkedWith [ tags, callers pagesSite [] ] |> CallerLedger.stateOf

      testCase "WHY — the description names the declaration, the caller's file and line, and says the old method is what runs" <| fun _ ->
        let text = CallersState.describe pending
        text |> Expect.stringContains "the declaration" "Shop.Tags.stamp"
        text |> Expect.stringContains "the file, by name" "Pages.fs"
        text |> Expect.stringContains "the line" "12"
        text |> Expect.stringContains "what runs" "old"

      testCase "WHY — the remedy is the next action: save the caller's file, and it names the file" <| fun _ ->
        CallersState.remedy pending |> Expect.stringContains "save the file" "Save Pages.fs"

      testCase "WHY — Current has nothing to say and nothing to do" <| fun _ ->
        CallersState.describe CallersState.CallersCurrent |> Expect.equal "silent by being empty of news" ""
        CallersState.remedy CallersState.CallersCurrent |> Expect.equal "no action" ""

      testCase "WHY — NotChecked says the callers were not checked and why, and what to do instead" <| fun _ ->
        let state =
          CallerLedger.empty
          |> checkedWith [ tags, CallersCheck.NotChecked UncheckedReason.ProjectNotLoaded ]
          |> CallerLedger.stateOf
        CallersState.describe state |> Expect.stringContains "says not checked" "not checked"
        CallersState.remedy state |> Expect.stringContains "gives an action" "Shop.Tags.stamp"

      testCase "WHY — a site matched by name only says so, with the reason the compiler was not used" <| fun _ ->
        let bound = SageFs.Timeouts.callerCheck
        let byName = { pagesSite with Evidence = SiteEvidence.MatchedByName(NameOnlyReason.CompilerTimedOut bound) }
        let state = CallerLedger.empty |> checkedWith [ tags, callers byName [] ] |> CallerLedger.stateOf
        CallersState.describe state |> Expect.stringContains "by name" "by name"
        CallersState.describe state |> Expect.stringContains "why the compiler was not used, and for how long" (sprintf "%.0fs" bound.TotalSeconds)
    ]

    testList "the wire" [
      testCase "WHY — every state survives the trip through its JSON, so the daemon reads exactly what the worker said" <| fun _ ->
        let byName = { pagesSite with Evidence = SiteEvidence.MatchedByName NameOnlyReason.DeclarationRemoved }
        let states =
          [ CallersState.CallersCurrent
            CallersState.CallersNotReported
            CallerLedger.empty |> checkedWith [ tags, callers pagesSite [ byName ] ] |> CallerLedger.stateOf
            CallerLedger.empty |> checkedWith [ tags, CallersCheck.NotChecked(UncheckedReason.SourceUnreadable("/p/B.fs", "locked")) ] |> CallerLedger.stateOf
            CallerLedger.empty
            |> checkedWith
              [ tags, callers pagesSite []
                edit "Shop.Tags.gone" SignatureCause.Removed "/p/Tags.fs", CallersCheck.NotChecked(UncheckedReason.NotSearchableByName "(+++)") ]
            |> CallerLedger.stateOf ]
        for state in states do
          CallersState.toJson state |> CallersState.ofJson |> Expect.equal "round trip" (Result.Ok state)

      testCase "WHY — a payload with no callers field is NotReported, not Current: a worker that said nothing has not said all is well" <| fun _ ->
        CallersState.ofJson "" |> Expect.equal "absent" (Result.Ok CallersState.CallersNotReported)

      testCase "WHY — the state token is one stable word per case" <| fun _ ->
        CallersState.token CallersState.CallersCurrent |> Expect.equal "current" "CallersCurrent"
        CallersState.token CallersState.CallersNotReported |> Expect.equal "not reported" "CallersNotReported"
        CallersState.token (CallerLedger.empty |> checkedWith [ tags, callers pagesSite [] ] |> CallerLedger.stateOf)
        |> Expect.equal "pending" "CallersPending"
    ]
  ]
