module SageFs.Tests.CallerCheckTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Features.CallerState
open SageFs.Features.CallerCheck
open SageFs.Features.ReloadPlanning

/// Who calls a declaration a save re-signed or removed, when the caller is in another file of the project. The pure part
/// reads the saved file against its baseline and the other files' text; the compiler part resolves the uses so a function
/// that only shares a name is not mistaken for a caller.

let private declsOf (source: string) : FileDecls =
  match extractDecls source with
  | Ok decls -> decls
  | Error reason -> failtestf "extractDecls failed: %s" reason

let private tagsBefore = """module Shop.Tags

let stamp (n: int) : string = "S1-" + string n

let other (n: int) : string = "O" + string n
"""

let private tagsResigned = """module Shop.Tags

let stamp (n: int) (suffix: string) : string = "S2-" + string n + suffix

let other (n: int) : string = "O" + string n
"""

let private tagsBodyOnly = """module Shop.Tags

let stamp (n: int) : string = "S3-" + string n

let other (n: int) : string = "O" + string n
"""

let private tagsRemoved = """module Shop.Tags

let other (n: int) : string = "O" + string n
"""

let private tagsRenamed = """module Shop.Tags

let tagged (n: int) : string = "S1-" + string n

let other (n: int) : string = "O" + string n
"""

let private tagsAdded = tagsBefore + "\nlet extra (n: int) : string = string n\n"

let private tagsFile = "/p/Tags.fs"

let private subjects (before: string) (after: string) = subjectsOf tagsFile (declsOf before) (declsOf after)

let private causes (found: SignatureSubject list) = found |> List.map (fun s -> s.Edit.Declaration, s.Edit.Cause)

let private pagesText = """module Shop.Pages

let render () : string =
  Shop.Tags.stamp 7

let unrelated () : string = "x"
"""

let private other (path: string) (text: string) : OtherFile = { Path = path; Text = text }

let private aSubject (cause: SignatureCause) : SignatureSubject =
  { Edit = { Declaration = "Shop.Tags.stamp"; Cause = cause; File = tagsFile }
    Name = "stamp"
    Lines = (match cause with SignatureCause.ReSigned -> SubjectLines.Declared(3, 3) | SignatureCause.Removed -> SubjectLines.GoneFromFile) }

let private noCompiler = CompilerAnswer.Unavailable NameOnlyReason.NoProjectOptions

/// What the compiler said when it checked clean and found nothing resolving to the declaration.
let private resolvedNothing = CompilerAnswer.Answered { Uses = []; ErrorLines = [] }

/// Project options for a few files on disk, with the references a script gets (FSharp.Core and the framework), in the
/// order given. A real project's options carry its packages too; the files of these cases need only the framework.
let private bareOptions (dir: string) (files: string list) : System.Threading.Tasks.Task<FSharp.Compiler.CodeAnalysis.FSharpProjectOptions> =
  task {
    let checker = FSharp.Compiler.CodeAnalysis.FSharpChecker.Create()
    let! script, _ =
      checker.GetProjectOptionsFromScript(Path.Combine(dir, "probe.fsx"), FSharp.Compiler.Text.SourceText.ofString "", assumeDotNetFramework = false)
    return { script with ProjectFileName = Path.Combine(dir, "Check.fsproj"); SourceFiles = List.toArray files }
  }

let private sitesOf (check: CallersCheck) : CallSite list =
  match check with
  | CallersCheck.Callers(first, rest) -> first :: rest
  | CallersCheck.NoCallers -> []
  | CallersCheck.NotChecked why -> failtestf "expected a check, got NotChecked %A" why

let private decideOne (sources: OtherSources) (answer: CompilerAnswer) (subject: SignatureSubject) : CallersCheck =
  match decide sources answer [ subject ] with
  | [ _, check ] -> check
  | other -> failtestf "one subject, one answer, got %A" other

[<Tests>]
let tests =
  testList "caller check: who still calls what a save re-signed or removed" [

    testList "what a save re-signed or removed" [
      testCase "WHY — a function whose header changed is re-signed, and is named by its qualified declaration" <| fun _ ->
        subjects tagsBefore tagsResigned |> causes |> Expect.equal "re-signed" [ "Shop.Tags.stamp", SignatureCause.ReSigned ]

      testCase "WHY — a body-only edit moves no caller, so it is not a subject" <| fun _ ->
        subjects tagsBefore tagsBodyOnly |> Expect.isEmpty "same signature, same method"

      testCase "WHY — a removed function is a subject: callers elsewhere still call the old method" <| fun _ ->
        subjects tagsBefore tagsRemoved |> causes |> Expect.equal "removed" [ "Shop.Tags.stamp", SignatureCause.Removed ]

      testCase "WHY — a rename is a removal of the old name, and the new name is nobody's caller yet" <| fun _ ->
        subjects tagsBefore tagsRenamed |> causes |> Expect.equal "the old name is what callers still call" [ "Shop.Tags.stamp", SignatureCause.Removed ]

      testCase "WHY — an added function has no callers to strand" <| fun _ ->
        subjects tagsBefore tagsAdded |> Expect.isEmpty "adding strands nobody"

      testCase "WHY — an unchanged file has nothing to report" <| fun _ ->
        subjects tagsBefore tagsBefore |> Expect.isEmpty "no change"

      testCase "WHY — the saved declaration's new lines are carried, so the compiler's resolution can be matched to it" <| fun _ ->
        match subjects tagsBefore tagsResigned with
        | [ s ] -> s.Lines |> Expect.equal "line 3 holds stamp" (SubjectLines.Declared(3, 3))
        | other -> failtestf "expected one subject, got %A" other

      testCase "WHY — a function inside a nested module is qualified by its module path and container" <| fun _ ->
        let before = "namespace Shop\n\nmodule Tags =\n  let stamp (n: int) = n\n"
        let after = "namespace Shop\n\nmodule Tags =\n  let stamp (n: int) (m: int) = n + m\n"
        subjects before after |> causes |> Expect.equal "namespace, module, name" [ "Shop.Tags.stamp", SignatureCause.ReSigned ]
    ]

    testList "reading the other files by name" [
      testCase "WHY — a caller in another file is found with its line and the declaration that holds the call" <| fun _ ->
        let found = decideOne (OtherSources.Loaded [ other "/p/Pages.fs" pagesText ]) noCompiler (aSubject SignatureCause.ReSigned)
        match sitesOf found with
        | [ s ] ->
          s.File |> Expect.equal "the file" "/p/Pages.fs"
          s.Line |> Expect.equal "the line of the call" 4
          s.Caller |> Expect.equal "the declaration holding the call" "Shop.Pages.render"
          s.Evidence |> Expect.equal "named as a name match, and why" (SiteEvidence.MatchedByName NameOnlyReason.NoProjectOptions)
        | other -> failtestf "expected one site, got %A" other

      testCase "WHY — a file that never says the name is not a caller, and no callers is Checked, not NotChecked" <| fun _ ->
        decideOne (OtherSources.Loaded [ other "/p/Pages.fs" "module Shop.Pages\n\nlet render () = 1\n" ]) noCompiler (aSubject SignatureCause.ReSigned)
        |> Expect.equal "nobody calls it" CallersCheck.NoCallers

      testCase "WHY — a name that only appears in a comment or a string is not a call" <| fun _ ->
        let text = "module Shop.Pages\n\n// stamp it\nlet render () = \"stamp\"\n"
        decideOne (OtherSources.Loaded [ other "/p/Pages.fs" text ]) noCompiler (aSubject SignatureCause.ReSigned)
        |> Expect.equal "prose is not a reference" CallersCheck.NoCallers

      testCase "WHY — another function's own definition of the same name is not a call to this one" <| fun _ ->
        let text = "module Shop.Pages\n\nlet stamp (x: int) = x\n"
        decideOne (OtherSources.Loaded [ other "/p/Pages.fs" text ]) noCompiler (aSubject SignatureCause.ReSigned)
        |> Expect.equal "a definition is not a use" CallersCheck.NoCallers

      testCase "WHY — callers in several files are all named, in file order" <| fun _ ->
        let found =
          decideOne
            (OtherSources.Loaded [ other "/p/Pages.fs" pagesText; other "/p/Admin.fs" "module Shop.Admin\n\nlet show () = Shop.Tags.stamp 1\n" ])
            noCompiler
            (aSubject SignatureCause.ReSigned)
        sitesOf found |> List.map _.File |> Expect.equal "both files" [ "/p/Admin.fs"; "/p/Pages.fs" ]

      testCase "WHY — a removal is matched by name whatever the compiler said: a name that is gone cannot be resolved" <| fun _ ->
        let answer = resolvedNothing
        let found = decideOne (OtherSources.Loaded [ other "/p/Pages.fs" pagesText ]) answer (aSubject SignatureCause.Removed)
        sitesOf found
        |> List.map _.Evidence
        |> Expect.equal "name evidence, saying the declaration is gone" [ SiteEvidence.MatchedByName NameOnlyReason.DeclarationRemoved ]
    ]

    testList "failing closed" [
      testCase "WHY — a project that is not loaded is NotChecked, never NoCallers" <| fun _ ->
        decideOne OtherSources.NotLoaded noCompiler (aSubject SignatureCause.ReSigned)
        |> Expect.equal "cannot say" (CallersCheck.NotChecked UncheckedReason.ProjectNotLoaded)

      testCase "WHY — a file that cannot be read is NotChecked, naming it" <| fun _ ->
        decideOne (OtherSources.Unreadable("/p/B.fs", "locked")) noCompiler (aSubject SignatureCause.ReSigned)
        |> Expect.equal "names the file" (CallersCheck.NotChecked(UncheckedReason.SourceUnreadable("/p/B.fs", "locked")))

      testCase "WHY — a name no scan can search for (an operator) is NotChecked" <| fun _ ->
        let op = { aSubject SignatureCause.ReSigned with Name = "(+++)" }
        decideOne (OtherSources.Loaded [ other "/p/Pages.fs" pagesText ]) noCompiler op
        |> Expect.equal "not searchable" (CallersCheck.NotChecked(UncheckedReason.NotSearchableByName "(+++)"))

      testCase "WHY — a compiler that timed out leaves the name matches in place and says it timed out" <| fun _ ->
        let bound = TimeSpan.FromSeconds 20.0
        let found =
          decideOne (OtherSources.Loaded [ other "/p/Pages.fs" pagesText ]) (CompilerAnswer.Unavailable(NameOnlyReason.CompilerTimedOut bound)) (aSubject SignatureCause.ReSigned)
        sitesOf found
        |> List.map _.Evidence
        |> Expect.equal "the reason is in the evidence" [ SiteEvidence.MatchedByName(NameOnlyReason.CompilerTimedOut bound) ]
    ]

    testList "what the compiler resolved" [
      let pagesCall = { UseFile = "/p/Pages.fs"; UseLine = 4; DeclaredIn = tagsFile; DeclaredLine = 3 }

      testCase "WHY — a use the compiler resolved to the re-signed declaration is a caller, with compiler evidence" <| fun _ ->
        let answer = CompilerAnswer.Answered { Uses = [ pagesCall ]; ErrorLines = [] }
        let found = decideOne (OtherSources.Loaded [ other "/p/Pages.fs" pagesText ]) answer (aSubject SignatureCause.ReSigned)
        sitesOf found |> List.map _.Evidence |> Expect.equal "resolved" [ SiteEvidence.ResolvedByCompiler ]

      testCase "WHY — a same-named function the compiler resolved elsewhere is NOT a caller" <| fun _ ->
        let elsewhere = { pagesCall with DeclaredIn = "/p/Other.fs"; DeclaredLine = 9 }
        let answer = CompilerAnswer.Answered { Uses = [ elsewhere ]; ErrorLines = [] }
        decideOne (OtherSources.Loaded [ other "/p/Pages.fs" pagesText ]) answer (aSubject SignatureCause.ReSigned)
        |> Expect.equal "resolved to another declaration, so not this one" CallersCheck.NoCallers

      testCase "WHY — a name match on a line the compiler errored on and did not resolve is kept, as a name match" <| fun _ ->
        let answer = CompilerAnswer.Answered { Uses = []; ErrorLines = [ "/p/Pages.fs", 4 ] }
        let found = decideOne (OtherSources.Loaded [ other "/p/Pages.fs" pagesText ]) answer (aSubject SignatureCause.ReSigned)
        sitesOf found
        |> List.map _.Evidence
        |> Expect.equal "unresolved use stays a caller until proven otherwise" [ SiteEvidence.MatchedByName NameOnlyReason.UseNotResolved ]

      testCase "WHY — a name match on a line the compiler checked cleanly and did not resolve to it is not a caller" <| fun _ ->
        let answer = CompilerAnswer.Answered { Uses = []; ErrorLines = [] }
        decideOne (OtherSources.Loaded [ other "/p/Pages.fs" pagesText ]) answer (aSubject SignatureCause.ReSigned)
        |> Expect.equal "the compiler saw that line and it is not a use of the declaration" CallersCheck.NoCallers
    ]

    testList "with the real compiler over a real two-file project" [
      testTask "WHY — the compiler resolves the caller in the other file to the re-signed function and not the same-named one beside it" {
        let dir = Directory.CreateTempSubdirectory("callercheck-").FullName
        try
          let tags = Path.Combine(dir, "Tags.fs")
          let pages = Path.Combine(dir, "Pages.fs")
          let decoy = Path.Combine(dir, "Decoy.fs")
          File.WriteAllText(tags, tagsResigned)
          // Calls the OLD arity, exactly what the file looks like before it is saved against the new signature.
          File.WriteAllText(pages, pagesText)
          File.WriteAllText(decoy, "module Shop.Decoy\n\nlet stamp (n: int) : string = string n\n\nlet use' () = stamp 3\n")
          let! options = bareOptions dir [ tags; decoy; pages ]
          let subject = { aSubject SignatureCause.ReSigned with Edit = { (aSubject SignatureCause.ReSigned).Edit with File = tags } }
          let others = OtherSources.Loaded [ other decoy (File.ReadAllText decoy); other pages (File.ReadAllText pages) ]
          let! found = callersAsync (Some options) others [ subject ] |> Async.StartAsTask
          match found with
          | [ _, check ] ->
            let sites = sitesOf check
            sites |> List.map _.File |> Expect.equal "only the real caller" [ pages ]
            sites |> List.map _.Evidence |> Expect.equal "resolved by the compiler" [ SiteEvidence.ResolvedByCompiler ]
            sites |> List.map _.Line |> Expect.equal "on the call's line" [ 4 ]
          | other -> failtestf "expected one answer, got %A" other
        finally
          try Directory.Delete(dir, true) with _ -> ()
      }

      testTask "WHY — with no compiler options the answer is still given, by name, saying why" {
        let others = OtherSources.Loaded [ other "/p/Pages.fs" pagesText ]
        let! found = callersAsync None others [ aSubject SignatureCause.ReSigned ] |> Async.StartAsTask
        match found with
        | [ _, check ] ->
          sitesOf check |> List.map _.Evidence |> Expect.equal "name evidence" [ SiteEvidence.MatchedByName NameOnlyReason.NoProjectOptions ]
        | other -> failtestf "expected one answer, got %A" other
      }
    ]
  ]
