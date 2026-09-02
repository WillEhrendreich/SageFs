module SageFs.Tests.ErrorMessagesTests

open Expecto
open Expecto.Flip
open SageFs.ErrorMessages

[<Tests>]
let errorMessagesTests =
  testList "Error Messages" [
    testList "categorize" [
      test "type error is detected" {
        categorize "The type 'int' does not match the type 'string'"
        |> Expect.equal "should detect type error" ErrorCategory.TypeError
      }
      test "syntax error via syntax keyword" {
        categorize "syntax error in expression"
        |> Expect.equal "should detect syntax error" ErrorCategory.SyntaxError
      }
      test "syntax error via unexpected keyword" {
        categorize "unexpected token in definition"
        |> Expect.equal "should detect unexpected as syntax error" ErrorCategory.SyntaxError
      }
      test "name error with not defined" {
        categorize "The value 'foo' is not defined"
        |> Expect.equal "should detect name error" ErrorCategory.NameError
      }
      test "name error with not found" {
        categorize "The namespace 'Bar' is not found"
        |> Expect.equal "should detect name error via not found" ErrorCategory.NameError
      }
      test "clean message is Unknown" {
        categorize "Everything is fine"
        |> Expect.equal "should be Unknown" ErrorCategory.Unknown
      }
      test "TypeLoadException message is detected" {
        categorize "TypeLoadException: Could not load type 'MyProject.Foo' from assembly 'MyProject'"
        |> Expect.equal "should detect TypeLoadException" ErrorCategory.TypeLoad
      }
      test "type identity message is detected as TypeLoad" {
        categorize "The type 'X' from assembly 'Y' has different type identity"
        |> Expect.equal "should detect type identity as TypeLoad" ErrorCategory.TypeLoad
      }
      test "ordinary type error is not TypeLoad" {
        categorize "The type 'int' does not match the type 'string'"
        |> Expect.notEqual "ordinary type mismatch should not be TypeLoad" ErrorCategory.TypeLoad
      }
      test "earlier error is detected" {
        categorize "Operation could not be completed due to earlier error"
        |> Expect.equal "should detect earlier error" ErrorCategory.EarlierError
      }
      test "TypeLoad takes priority over TypeError for type-containing messages" {
        // TypeLoadException contains 'type' but should be classified as TypeLoad, not TypeError
        categorize "TypeLoadException: type conflict"
        |> Expect.equal "TypeLoad should win over TypeError" ErrorCategory.TypeLoad
      }
      test "not-found on first line is NameError even when later lines mention type" {
        // Regression: stack frames / dumps after the real error must not reclassify.
        categorize "The namespace 'Bar' is not found\nat System.Runtime.TypeLoader.Load()"
        |> Expect.equal "first-line not found should stay NameError" ErrorCategory.NameError
      }
      test "type mismatch in a later stack frame does not reclassify a name error" {
        categorize "The value 'foo' is not defined\n   at FSI_0003.Program.foo() in type Foo"
        |> Expect.equal "stack noise must not flip NameError to TypeError" ErrorCategory.NameError
      }
      test "type mismatch on first line is TypeError (not caught by earlier not-found)" {
        categorize "The type 'int' does not match the type 'string'"
        |> Expect.equal "type mismatch should be TypeError" ErrorCategory.TypeError
      }
      test "unexpected on first line is SyntaxError even when the dump mentions type" {
        categorize "unexpected token ')'\nSystem.TypeInitializationException"
        |> Expect.equal "unexpected should stay SyntaxError" ErrorCategory.SyntaxError
      }
      test "multi-line TypeLoad dump still classifies TypeLoad from first line" {
        categorize "TypeLoadException: Could not load type 'X'\n at FSI_0001..."
        |> Expect.equal "TypeLoad on first line wins" ErrorCategory.TypeLoad
      }
    ]
    testList "getSuggestion" [
      test "TypeLoad suggests removing #r" {
        getSuggestion ErrorCategory.TypeLoad
        |> Expect.stringContains "should mention #r" "#r"
      }
      test "TypeLoad suggests checking get_startup_info" {
        getSuggestion ErrorCategory.TypeLoad
        |> Expect.stringContains "should mention get_startup_info" "get_startup_info"
      }
      test "TypeLoad does NOT say to reset" {
        getSuggestion ErrorCategory.TypeLoad
        |> Expect.stringContains "should say NOT reset" "NOT"
      }
      test "earlier error suggests fix original" {
        getSuggestion ErrorCategory.EarlierError
        |> Expect.stringContains "should mention earlier error" "earlier"
      }
      test "earlier error warns not to reset session" {
        getSuggestion ErrorCategory.EarlierError
        |> Expect.stringContains "should warn against reset" "NOT"
      }
      test "name error gives namespace tip" {
        getSuggestion ErrorCategory.NameError
        |> Expect.stringContains "should mention namespace" "namespace"
      }
      test "type error gives type tip" {
        getSuggestion ErrorCategory.TypeError
        |> Expect.stringContains "should mention type" "type"
      }
      test "syntax error gives syntax tip" {
        getSuggestion ErrorCategory.SyntaxError
        |> Expect.stringContains "should mention syntax" "yntax"
      }
      test "generic error gives generic tip" {
        getSuggestion ErrorCategory.Unknown
        |> Expect.stringContains "should suggest smaller pieces" "smaller"
      }
    ]
    testList "formatError" [
      test "includes original error text" {
        formatError "the type mismatch occurred"
        |> Expect.stringContains "should include original error" "the type mismatch occurred"
      }
      test "includes suggestion after error" {
        formatError "the value 'x' is not defined"
        |> Expect.stringContains "should include tip" "Tip"
      }
      test "contains newline separator" {
        formatError "some error"
        |> Expect.stringContains "should have newline separator" "\n\n"
      }
    ]
  ]
