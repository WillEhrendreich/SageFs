;; Tree-sitter queries for F# test attribute detection
;; Used by LiveTesting to discover test locations in source files
;;
;; Captures:
;;   @test.attribute  — the attribute name (e.g. "Fact", "Test", "Tests")
;;   @test.name       — the function/binding name
;;   @test.definition — the entire definition node (for line range)

;; --- xUnit: [<Fact>], [<Theory>] ---
(
  (attributes (attribute_set (attribute (_type) @test.attribute)))
  .
  (function_or_value
    (function_declaration_left . (identifier) @test.name) @test.definition)
  (#any-of? @test.attribute "Fact" "FactAttribute" "Theory" "TheoryAttribute")
)

;; --- NUnit: [<Test>], [<TestCase>], [<TestCaseSource>] ---
(
  (attributes (attribute_set (attribute (_type) @test.attribute)))
  .
  (function_or_value
    (function_declaration_left . (identifier) @test.name) @test.definition)
  (#any-of? @test.attribute "Test" "TestAttribute" "TestCase" "TestCaseAttribute" "TestCaseSource" "TestCaseSourceAttribute")
)

;; --- MSTest: [<TestMethod>], [<DataTestMethod>] ---
(
  (attributes (attribute_set (attribute (_type) @test.attribute)))
  .
  (function_or_value
    (function_declaration_left . (identifier) @test.name) @test.definition)
  (#any-of? @test.attribute "TestMethod" "TestMethodAttribute" "DataTestMethod" "DataTestMethodAttribute")
)

;; --- TUnit: [<Test>] (same attribute name as NUnit, different assembly) ---
;; Handled by the NUnit pattern above — disambiguation happens at runtime via assembly marker

;; --- Expecto: [<Tests>] on let bindings ---
(
  (attributes (attribute_set (attribute (_type) @test.attribute)))
  .
  (function_or_value
    (value_declaration_left . (identifier_pattern . (identifier) @test.name)) @test.definition)
  (#any-of? @test.attribute "Tests" "TestsAttribute")
)

;; --- Benchmark.NET: [<Benchmark>] ---
(
  (attributes (attribute_set (attribute (_type) @test.attribute)))
  .
  (function_or_value
    (function_declaration_left . (identifier) @test.name) @test.definition)
  (#any-of? @test.attribute "Benchmark" "BenchmarkAttribute")
)

;; --- FsCheck: [<Property>] ---
(
  (attributes (attribute_set (attribute (_type) @test.attribute)))
  .
  (function_or_value
    (function_declaration_left . (identifier) @test.name) @test.definition)
  (#any-of? @test.attribute "Property" "PropertyAttribute")
)

;; --- Class-level test methods (for OOP-style test frameworks) ---
;; Matches member definitions with test attributes inside type declarations

;; xUnit/NUnit/MSTest/TUnit class members
(
  (attributes (attribute_set (attribute (_type) @test.attribute)))
  .
  (member_defn
    (method_or_prop_defn
      (property_or_ident
        method: (identifier) @test.name)) @test.definition)
  (#any-of? @test.attribute
    "Fact" "FactAttribute" "Theory" "TheoryAttribute"
    "Test" "TestAttribute" "TestCase" "TestCaseAttribute"
    "TestMethod" "TestMethodAttribute" "DataTestMethod" "DataTestMethodAttribute"
    "Benchmark" "BenchmarkAttribute"
    "Property" "PropertyAttribute")
)
