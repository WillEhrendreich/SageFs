namespace SageFs.Features.LiveTesting

/// Whose file is this? Two checkouts of one repository share every test name,
/// so a name match can never say which session a source file belongs to. A
/// session owns only the files under its working directory and its project
/// directories ("roots"); a test position outside them is someone else's (or a
/// deleted directory's) and is reported as "no location" instead.
///
/// Pure and total. The roots come from the session's own snapshot, never from
/// a daemon-global field.
module SessionSourceScope =

  /// Is `file` inside one of `roots`? Compared on whole path segments
  /// ("/tmp/a" does not contain "/tmp/a-evil/x.fs"); a relative `file` is read
  /// relative to the root it is tested against. An empty root list contains
  /// nothing.
  let isWithinRoots (roots: string list) (file: string) : bool =
    let comparison =
      match System.OperatingSystem.IsWindows() with
      | true -> System.StringComparison.OrdinalIgnoreCase
      | false -> System.StringComparison.Ordinal
    roots
    |> List.exists (fun root ->
      match System.String.IsNullOrWhiteSpace root with
      | true -> false
      | false ->
        let fullRoot = System.IO.Path.GetFullPath root
        let prefix =
          match fullRoot.EndsWith(System.IO.Path.DirectorySeparatorChar) with
          | true -> fullRoot
          | false -> fullRoot + string System.IO.Path.DirectorySeparatorChar
        System.IO.Path.GetFullPath(file, fullRoot).StartsWith(prefix, comparison))

  /// Demote every test whose source position lies outside `roots` back to
  /// `ReflectionOnly`. Fail-closed: no roots, no locations.
  let confineToRoots (roots: string list) (tests: TestCase array) : TestCase array =
    tests |> Array.map (fun test ->
      match test.Origin with
      | TestOrigin.SourceMapped (file, _) when not (isWithinRoots roots file) ->
        { test with Origin = TestOrigin.ReflectionOnly }
      | TestOrigin.SourceMapped _
      | TestOrigin.ReflectionOnly -> test)
