module SageFs.Tests.ErrorMessagesTests

open Expecto
open Expecto.Flip

let errorMessagesTests =
  testList "Error Messages" [
    testList "parseError" [
      test "type error is detected" {
        let result = SageFs.ErrorMessages.parseError "The type 'int' does not match the type 'string'"
        result.IsTypeError |> Expect.isTrue "should detect type error"
        result.IsSyntaxError |> Expect.isFalse "should not be syntax error"
        result.IsNameError |> Expect.isFalse "should not be name error"
      }
      test "syntax error via syntax keyword" {
        let result = SageFs.ErrorMessages.parseError "syntax error in expression"
        result.IsSyntaxError |> Expect.isTrue "should detect syntax error"
        result.IsTypeError |> Expect.isFalse "should not be type error"
      }
      test "syntax error via unexpected keyword" {
        let result = SageFs.ErrorMessages.parseError "unexpected token in definition"
        result.IsSyntaxError |> Expect.isTrue "should detect unexpected as syntax error"
      }
      test "name error with not defined" {
        let result = SageFs.ErrorMessages.parseError "The value 'foo' is not defined"
        result.IsNameError |> Expect.isTrue "should detect name error"
      }
      test "name error with not found" {
        let result = SageFs.ErrorMessages.parseError "The namespace 'Bar' is not found"
        result.IsNameError |> Expect.isTrue "should detect name error via not found"
      }
      test "no error flags for clean message" {
        let result = SageFs.ErrorMessages.parseError "Everything is fine"
        result.IsTypeError |> Expect.isFalse "should not be type error"
        result.IsSyntaxError |> Expect.isFalse "should not be syntax error"
        result.IsNameError |> Expect.isFalse "should not be name error"
      }
      test "message is preserved" {
        let msg = "Some complex error message"
        let result = SageFs.ErrorMessages.parseError msg
        result.Message |> Expect.equal "should preserve message" msg
      }
      test "line and column are None" {
        let result = SageFs.ErrorMessages.parseError "some error"
        result.Line |> Expect.isNone "line should be None"
        result.Column |> Expect.isNone "column should be None"
      }
    ]
    testList "getSuggestion" [
      test "earlier error suggests fix original" {
        let parsed = SageFs.ErrorMessages.parseError "Operation could not be completed due to earlier error"
        let suggestion = SageFs.ErrorMessages.getSuggestion parsed
        suggestion |> Expect.stringContains "should mention earlier error" "earlier"
      }
      test "earlier error warns not to reset session" {
        let parsed = SageFs.ErrorMessages.parseError "something earlier error something"
        let suggestion = SageFs.ErrorMessages.getSuggestion parsed
        suggestion |> Expect.stringContains "should warn against reset" "NOT"
      }
      test "name error gives namespace tip" {
        let parsed = SageFs.ErrorMessages.parseError "The value 'x' is not defined"
        let suggestion = SageFs.ErrorMessages.getSuggestion parsed
        suggestion |> Expect.stringContains "should mention namespace" "namespace"
      }
      test "type error gives type tip" {
        let parsed = SageFs.ErrorMessages.parseError "The type 'int' does not unify"
        let suggestion = SageFs.ErrorMessages.getSuggestion parsed
        suggestion |> Expect.stringContains "should mention type" "type"
      }
      test "syntax error gives syntax tip" {
        let parsed = SageFs.ErrorMessages.parseError "syntax error near let"
        let suggestion = SageFs.ErrorMessages.getSuggestion parsed
        suggestion |> Expect.stringContains "should mention syntax" "yntax"
      }
      test "generic error gives generic tip" {
        let parsed = SageFs.ErrorMessages.parseError "Something went wrong"
        let suggestion = SageFs.ErrorMessages.getSuggestion parsed
        suggestion |> Expect.stringContains "should suggest smaller pieces" "smaller"
      }
    ]
    testList "formatError" [
      test "includes original error text" {
        let result = SageFs.ErrorMessages.formatError "the type mismatch occurred"
        result |> Expect.stringContains "should include original error" "the type mismatch occurred"
      }
      test "includes suggestion after error" {
        let result = SageFs.ErrorMessages.formatError "the value 'x' is not defined"
        result |> Expect.stringContains "should include tip" "Tip"
      }
      test "contains newline separator" {
        let result = SageFs.ErrorMessages.formatError "some error"
        result |> Expect.stringContains "should have newline separator" "\n\n"
      }
    ]
  ]
