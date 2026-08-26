namespace SageFs

/// Version-variant selection for API-coupled host libraries.
///
/// The host's closure contains API-coupled libraries (Fantomas, Cecil,
/// Harmony) whose public APIs the host's own code calls directly. A project
/// pinning a DIFFERENT version of one of these cannot share the host's copy —
/// loading the project's version would throw MissingMethodException the moment
/// host code touches it.
///
/// The automatic adaptation: the host ships VERSION-MATCHED VARIANT assemblies
/// (e.g. a preprocessing variant compiled against Fantomas 6, another against
/// Fantomas 8), each behind a host-defined interface. At session start the
/// supervisor selects the variant matching the project's pinned version.
///
/// Selection is PURE and fail-closed: an unknown version or a cross-project
/// conflict is an Error with an actionable message — never a silent default.
module VariantSelector =

  /// A selected variant assembly (short name, e.g. "Fantomas6").
  type Variant = Variant of string

  /// How a project's pinned assembly identity is turned into a variant name.
  /// Pure and injectable for tests: the real implementation reads the
  /// assembly version from the project bin's DLL; tests provide a fake.
  type VariantLookup = string -> string -> Result<Variant, string>
  // ^ library name -> version -> selected variant

  /// The known variant mapping: library family -> version major -> variant
  /// short name. This is the v1 bounded set (see plan: "Variant count ->
  /// bounded v1 set"). The full variant ASSEMBLY name is derived from the
  /// short name (e.g. "Fantomas6" -> "SageFs.Host.Preprocess.Fantomas6.dll")
  /// at load time by the supervisor.
  let private knownVariants: (string * string * string) list = [
    // (libraryName, versionPrefix, variantShortName)
    ("Fantomas", "6", "Fantomas6")
    ("Fantomas", "8", "Fantomas8")
    ("Mono.Cecil", "0.11", "Cecil0.11")
    ("HarmonyLib", "2.3", "Harmony2.3")
  ]

  /// The real lookup: match the library name + version major against the
  /// known variant set.
  let selectVariantFromAssemblyIdentity (library: string) (version: string) : Result<Variant, string> =
    let versionMajor =
      version.Split('.')
      |> Array.tryHead
      |> Option.defaultValue ""
    let cecilPrefix =
      version.Split('.')
      |> Array.truncate 2
      |> String.concat "."
    knownVariants
    |> List.tryFind (fun (lib, prefix, _) ->
      lib = library
      && (prefix = versionMajor || prefix = cecilPrefix))
    |> function
      | Some (_, _, variantName) -> Ok(Variant variantName)
      | None ->
        Error(
          sprintf
            "no variant for %s v%s — the host ships version-matched variants for this library; add a variant to support this pin (see plan: fsi-host-supervisor-isolation)"
            library version)

  /// Select a variant for one library/version pin.
  let select (lookup: VariantLookup) (library: string) (version: string) : Result<Variant, string> =
    lookup library version

  /// Select variants for a whole solution's worth of pins. Fail-closed:
  /// if two projects pin different versions of the SAME library, that is a
  /// genuine conflict and the whole selection refuses with a message naming
  /// the conflicting versions.
  ///
  /// `pins` is a list of (library, version) pairs.
  let selectForSolution
    (lookup: VariantLookup)
    (pins: (string * string) list)
    : Result<Variant list, string> =
    // Group pins by library; if a library appears with 2+ distinct versions,
    // that's a conflict.
    pins
    |> List.groupBy fst
    |> List.tryPick (fun (lib, group) ->
      let distinctVersions = group |> List.map snd |> List.distinct
      match distinctVersions with
      | [ _ ] -> None
      | conflicting ->
        Some(
          Error(
            sprintf
              "conflict: projects pin different versions of %s: %s"
              lib (String.concat ", " conflicting))))
    |> function
      | Some err -> err
      | None ->
        // All libraries have a single version each — select each.
        pins
        |> List.map (fun (lib, ver) -> lookup lib ver)
        |> List.fold
          (fun acc r ->
            match acc, r with
            | Error e, _ -> Error e
            | _, Error e -> Error e
            | Ok acc', Ok v -> Ok(v :: acc'))
          (Ok [])
        |> Result.map List.rev
