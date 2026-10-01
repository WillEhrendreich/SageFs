/// ## CohortGit Mutation Tests
///
/// `classifyRebaseFailure` is `SageFs.Features.CohortGit`'s one pure,
/// exhaustive classifier — the encoding boundary between a genuine rebase
/// conflict and an infra failure squeezed through the same `string list`
/// channel (see its doc comment and `infraFailureMarker`). CohortGitTests.fs
/// only reaches it indirectly, through slow `[Integration]` tests that spawn
/// a real `git` process; this file pins its boundary behavior directly and
/// fast, with no process spawn required. Each case asserts EXACT equality
/// against the correct value (not merely inequality with one hand-picked
/// wrong value) so a mutant that returns any other wrong value is killed
/// too.
module CohortGitMutationTests

open Expecto
open Expecto.Flip
open SageFs.Features

// The private `infraFailureMarker` in CohortGit.fs is
// `string (char 0) + "cohort-git-infra-failure:"` — reproduced here
// deliberately (it is intentionally private; `classifyRebaseFailure` is the
// only production code that should ever construct or inspect it) so these
// tests can drive the classifier's public boundary from the outside, the
// same way a real `rebase` infra-failure path would.
let private marker = string (char 0) + "cohort-git-infra-failure:"

let cohortGitMutationTests = testList "CohortGit.classifyRebaseFailure mutations" [

  testCase "WHY — infra_marker_prefixed_single_element_becomes_InfraFailure — a lone marker-prefixed string must classify as InfraFailure with the reason after the marker" <| fun () ->
    CohortGit.classifyRebaseFailure [ marker + "bad onto ref" ]
    |> Expect.equal "a single infra-marker element must decode to InfraFailure \"bad onto ref\"" (CohortGit.InfraFailure "bad onto ref")

  testCase "WHY — plain_single_file_stays_Conflict — a single plain file path (no marker) must classify as Conflict, not InfraFailure" <| fun () ->
    CohortGit.classifyRebaseFailure [ "Foo.fs" ]
    |> Expect.equal "a lone real conflicting file must decode to Conflict [ \"Foo.fs\" ]" (CohortGit.Conflict [ "Foo.fs" ])

  testCase "WHY — multiple_conflict_files_stay_Conflict_in_order — several conflicting files must classify as Conflict with all of them, in order" <| fun () ->
    CohortGit.classifyRebaseFailure [ "A.fs"; "B.fs"; "C.fs" ]
    |> Expect.equal "multiple files must decode to Conflict [ \"A.fs\"; \"B.fs\"; \"C.fs\" ], never reordered or truncated" (CohortGit.Conflict [ "A.fs"; "B.fs"; "C.fs" ])

  testCase "WHY — empty_list_is_Conflict_empty_not_InfraFailure — an empty failure list must classify as Conflict [], never InfraFailure" <| fun () ->
    CohortGit.classifyRebaseFailure []
    |> Expect.equal "an empty list has no marker to match, so it must fall through to Conflict []" (CohortGit.Conflict [])

  testCase "WHY — marker_prefixed_element_alongside_others_stays_Conflict — the marker only applies to a LONE element; a marker-prefixed string alongside a second element must stay Conflict" <| fun () ->
    CohortGit.classifyRebaseFailure [ marker + "x"; "extra.fs" ]
    |> Expect.equal "the single-element guard must not fire for a 2-element list even if the first element carries the marker" (CohortGit.Conflict [ marker + "x"; "extra.fs" ])
]
