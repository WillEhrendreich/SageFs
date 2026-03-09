namespace SageFs

/// Paths and structure for the getting-started tutorial.
module Tutorial =
  let fileName = "getting-started.fsx"

  /// Expected (number, title) pairs for each section.
  let sections : (int * string) list =
    [ 1,  "Instant feedback"
      2,  "Let bindings"
      3,  "Functions"
      4,  "The pipeline operator"
      5,  "Records"
      6,  "Discriminated unions"
      7,  "Pattern matching"
      8,  "Writing tests with Expecto"
      9,  "Hot reload"
      10, "Where to go next" ]

  /// Try to resolve the tutorial file inside a samples directory.
  let resolvePath (samplesDir: string) : string option =
    let candidate = System.IO.Path.Combine(samplesDir, fileName)
    match System.IO.File.Exists candidate with
    | true  -> Some candidate
    | false -> None
