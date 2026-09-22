module SageFs.Tests.UnionCaseShadowingTests

open System.Reflection
open Expecto
open Expecto.Flip

// ---------------------------------------------------------------------------
// A SageFs session auto-opens namespaces and top-level modules from every
// project it loads (`AppState.fs`'s auto-open-namespaces warmup step — this
// is how a self-hosting SageFs.Core/SageFs session gets its own modules
// ambient). A union case named `Ok`, `Error`, `Some` or `None` anywhere in a
// SageFs assembly is then one `open` away from shadowing `Result.Ok`,
// `Result.Error`, `Option.Some` or `Option.None` for a bare, unqualified
// match arm — which fails to compile with "This union case does not take
// arguments" and a wrong-type error pointing at the SHADOWING case, not at
// the user's actual mistake.
//
// `[<RequireQualifiedAccess>]` was considered and rejected: Will wants this
// impossible outright, not merely qualifiable, because the reader (or the
// FSI auto-completer) doesn't get a heads-up that this particular case name
// is landmine-shaped. The case is renamed to something that says what it
// means instead.
//
// So this test bans the four names outright, in every SageFs.* assembly the
// test suite loads — with or without RequireQualifiedAccess — and names
// every offender so the fix has a checklist instead of a vibe.
// ---------------------------------------------------------------------------

let private shadowingNames = set [ "Ok"; "Error"; "Some"; "None" ]

let private unionFlags = BindingFlags.Public ||| BindingFlags.NonPublic

/// Every union case in `asm` named `Ok`/`Error`/`Some`/`None`, as
/// "Full.Type.Name (case=X)" — public and private alike, since a private DU
/// can still be opened into scope by an ambient module open.
let private shadowingCasesIn (asm: Assembly) : string list =
  let types =
    try
      asm.GetTypes()
    with :? ReflectionTypeLoadException as ex ->
      ex.Types |> Array.filter (fun t -> not (isNull t))
  [
    for t in types do
      let isUnion =
        try FSharp.Reflection.FSharpType.IsUnion(t, unionFlags)
        with _ -> false
      if isUnion then
        try
          for case in FSharp.Reflection.FSharpType.GetUnionCases(t, unionFlags) do
            if shadowingNames.Contains case.Name then
              yield sprintf "%s (case=%s)" t.FullName case.Name
        with _ -> ()
  ]

/// Assemblies actually loaded by this test run — SageFs.Core, SageFs, and
/// SageFs.Host/SageFs.Simulation when the test project references them.
/// `tryLoadAssembly` mirrors `ArchitectureTests.fs`'s house style: an
/// assembly this project doesn't reference is not part of what a SageFs
/// session can auto-open, so it is skipped rather than failed.
let private tryLoadAssembly (name: string) : Assembly option =
  try
    Some(Assembly.Load name)
  with
  | :? System.IO.FileNotFoundException -> None
  | :? System.IO.FileLoadException -> None

let private sageFsAssemblyNames = [ "SageFs.Core"; "SageFs"; "SageFs.Host"; "SageFs.Simulation" ]

[<Tests>]
let unionCaseShadowingTests =
  testList "Union cases must not shadow Result/Option (roast: RQA fix)" [

    testCase
      "WHY — a bare Error/Ok/Some/None case anywhere in SageFs.* makes `open`ing that module a trap for Result/Option pattern matches"
    <| fun _ ->
      let offenders =
        sageFsAssemblyNames
        |> List.choose tryLoadAssembly
        |> List.collect shadowingCasesIn
        |> List.sort

      match offenders with
      | [] -> ()
      | _ ->
        failwithf
          "%d union case(s) named Ok/Error/Some/None found in SageFs assemblies — each one shadows Result/Option the moment its module is opened:\n%s"
          offenders.Length
          (offenders |> List.map (sprintf "  - %s") |> String.concat "\n")
  ]
