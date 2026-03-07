module SageFs.Samples.Koans.Program

open Expecto

let allTests = testList "F# Koans — SageFs Edition" [
  AboutAsserts.tests
  AboutLet.tests
  AboutFunctions.tests
  AboutUnit.tests
  AboutOrderOfEvaluation.tests
  AboutTuples.tests
  AboutStrings.tests
  AboutBranching.tests
  AboutLists.tests
  AboutPipelining.tests
  AboutArrays.tests
  AboutLooping.tests
  MoreAboutFunctions.tests
  AboutDotNetCollections.tests
  AboutStockExample.tests
  AboutRecordTypes.tests
  AboutOptionTypes.tests
  AboutDiscriminatedUnions.tests
  AboutModules.tests
  AboutClasses.tests
  AboutFiltering.tests
  GraduationGuide.tests
]

[<EntryPoint>]
let main argv =
  Tests.runTestsWithCLIArgs [] argv allTests
