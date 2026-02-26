;; Tree-sitter queries for F# test attribute detection
;; Used by LiveTesting to discover test locations in source files
;;
;; Grammar: ionide/tree-sitter-fsharp (bundled in TreeSitter.DotNet 1.3.0)
;; Verified parse tree structure via FSI:
;;
;;   Module-level function: [<Fact>] let foo () = ...
;;     (declaration_expression
;;       (attributes (attribute (simple_type (long_identifier (identifier)))))
;;       (function_or_value_defn
;;         (function_declaration_left (identifier))))
;;
;;   Module-level value: [<Tests>] let myTests = ...
;;     (declaration_expression
;;       (attributes (attribute (simple_type (long_identifier (identifier)))))
;;       (function_or_value_defn
;;         (value_declaration_left (identifier_pattern (long_identifier_or_op (identifier))))))
;;
;;   Class member: [<Test>] member _.Foo() = ...
;;     (member_defn
;;       (attributes (attribute (simple_type (long_identifier (identifier)))))
;;       (method_or_prop_defn (property_or_ident method: (identifier))))
;;
;; Captures:
;;   @test.attribute  — the attribute name (e.g. "Fact", "Test", "Tests")
;;   @test.name       — the function/binding name

;; --- Module-level function declarations with test attributes ---
;; Matches: [<Fact>] let testFoo () = ..., [<Test>] let testBar x = ..., etc.
(declaration_expression
  (attributes
    (attribute
      (simple_type
        (long_identifier
          (identifier) @test.attribute))))
  (function_or_value_defn
    (function_declaration_left
      (identifier) @test.name))
  (#any-of? @test.attribute
    "Fact" "FactAttribute" "Theory" "TheoryAttribute"
    "Test" "TestAttribute" "TestCase" "TestCaseAttribute" "TestCaseSource" "TestCaseSourceAttribute"
    "TestMethod" "TestMethodAttribute" "DataTestMethod" "DataTestMethodAttribute"
    "Tests" "TestsAttribute"
    "Benchmark" "BenchmarkAttribute"
    "Property" "PropertyAttribute"))

;; --- Module-level value bindings with test attributes ---
;; Matches: [<Tests>] let myTests = testList "..." [...], etc.
(declaration_expression
  (attributes
    (attribute
      (simple_type
        (long_identifier
          (identifier) @test.attribute))))
  (function_or_value_defn
    (value_declaration_left
      (identifier_pattern
        (long_identifier_or_op
          (identifier) @test.name))))
  (#any-of? @test.attribute
    "Tests" "TestsAttribute"
    "Fact" "FactAttribute" "Theory" "TheoryAttribute"
    "Test" "TestAttribute" "TestCase" "TestCaseAttribute"
    "Benchmark" "BenchmarkAttribute"
    "Property" "PropertyAttribute"))

;; --- Class-level test methods ---
;; Matches member definitions with test attributes inside type declarations
(member_defn
  (attributes
    (attribute
      (simple_type
        (long_identifier
          (identifier) @test.attribute))))
  (method_or_prop_defn
    (property_or_ident
      method: (identifier) @test.name))
  (#any-of? @test.attribute
    "Fact" "FactAttribute" "Theory" "TheoryAttribute"
    "Test" "TestAttribute" "TestCase" "TestCaseAttribute"
    "TestMethod" "TestMethodAttribute" "DataTestMethod" "DataTestMethodAttribute"
    "Benchmark" "BenchmarkAttribute"
    "Property" "PropertyAttribute"))
