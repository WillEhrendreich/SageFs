/// Two builds of the same project, compared: which methods are unchanged, which only have a new body,
/// which are new, and which edits a delta cannot carry.
///
/// This is the shape of the answer. The comparison itself is not written yet: `diff` says nothing changed.
namespace SageFs.Features.MetadataDelta

open System.Reflection.Metadata

/// A method, named the same way in two builds.
type MethodId =
  { TypeKey: string
    Name: string
    /// The signature as text, e.g. `String(!!0)`1`.
    Signature: string }

[<RequireQualifiedAccess>]
module MethodId =
  let describe (id: MethodId) : string = sprintf "%s.%s" id.TypeKey id.Name

[<RequireQualifiedAccess>]
type MethodChange =
  | Unchanged
  | BodyChanged
  | Added
  | Rude of RudeCause

type MethodVerdict =
  { Method: MethodId
    Change: MethodChange
    /// The method in the NEW build, which is where its body is read from.
    NextHandle: MethodDefinitionHandle }

/// What the previous image is.
type DiffOptions =
  { /// Whether the previous image carries coverage probes to look through. The new build is a clean compile.
    PreviousProbes: ProbeStripping }

type ImageDiff =
  { Methods: MethodVerdict list
    /// Causes that have no method of the new build to attach to: a type or method that is gone, a type
    /// that is new, a type whose shape changed.
    Refusals: RudeCause list }

[<RequireQualifiedAccess>]
module ImageDiff =
  /// Every cause the diff found, whichever list it sits in.
  let causes (diff: ImageDiff) : RudeCause list =
    diff.Refusals
    @ (diff.Methods
       |> List.choose (fun v ->
         match v.Change with
         | MethodChange.Rude cause -> Some cause
         | _ -> None))

  /// The methods whose body a delta has to write.
  let toWrite (diff: ImageDiff) : MethodVerdict list =
    diff.Methods
    |> List.filter (fun v ->
      match v.Change with
      | MethodChange.BodyChanged
      | MethodChange.Added -> true
      | MethodChange.Unchanged
      | MethodChange.Rude _ -> false)

[<RequireQualifiedAccess>]
module MethodDiff =

  /// Compare two builds. `previous` is what the running process holds the metadata of.
  let diff (options: DiffOptions) (previous: PeImage) (next: PeImage) : ImageDiff =
    ignore (options, previous, next)
    { Methods = []
      Refusals = [] }
