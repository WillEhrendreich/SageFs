/// Two builds of the same project, compared: which methods are unchanged, which only have a new body,
/// which are new, and which edits a delta cannot carry.
///
/// "Previous" is whatever the running process holds the metadata of. For the first save it is the module
/// that was loaded (which, for a Cecil-instrumented shadow copy, is the instrumented one: its probes are
/// looked through). For the next save it is the build the last delta was made from.
///
/// The comparison is on what things are called and what the code does, never on row numbers or byte
/// offsets (see PeImage and IlCanon). The answer for every method is one of four, and the last carries a
/// closed cause:
///
///   * Unchanged: nothing the runtime could notice differs.
///   * BodyChanged: same name, signature, flags and parameters, and the instructions differ.
///   * Added: a method the running assembly does not have, on a type it does, that the runtime can add.
///   * Rude: anything else.
///
/// A type or method that disappeared has no entry in `Methods` (there is nothing in the new build to read
/// a body from), so it is a `Refusals` entry.
namespace SageFs.Features.MetadataDelta

open System
open System.Collections.Generic
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335

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

/// Whether the runtime can take a method added to a type.
[<RequireQualifiedAccess>]
type internal AddedSupport =
  | Supported
  | Unsupported of why: string

[<RequireQualifiedAccess>]
module MethodDiff =

  /// Attributes the compiler numbers by position, so they move when a member is inserted. An edit that
  /// inserts a member is refused for that reason, so the number itself is not compared.
  let private positional = set [ "CompilationMappingAttribute" ]

  let private entityOf (table: TableIndex) (rowNumber: int) : EntityHandle = MetadataTokens.EntityHandle(table, rowNumber)

  /// The custom attributes on `owner`, as sorted text of constructor and value.
  let private attributesOf (image: PeImage) (owner: EntityHandle) : string list =
    let reader = image.Reader
    [ for handle in reader.GetCustomAttributes owner do
        let attribute = reader.GetCustomAttribute handle
        let constructor = image.Describe attribute.Constructor
        let skipped = positional |> Set.exists (fun name -> constructor.Contains(name, StringComparison.Ordinal))
        match skipped with
        | true -> ()
        | false -> yield constructor + "=" + Convert.ToHexString(reader.GetBlobBytes attribute.Value) ]
    |> List.sort

  /// The types of an image by key, without `<Module>` and without anything in `skip`.
  let private typeTable (image: PeImage) (skip: string -> bool) : (string * TypeDefinitionHandle) list =
    [ for handle in image.Reader.TypeDefinitions do
        match MetadataTokens.GetRowNumber handle with
        | 1 -> ()
        | _ ->
          let key = image.TypeKey handle
          match skip key with
          | true -> ()
          | false -> yield key, handle ]

  /// The shape of a type apart from its methods: what a delta cannot change.
  let private shapeOf (image: PeImage) (handle: TypeDefinitionHandle) : string list =
    let reader = image.Reader
    let t = reader.GetTypeDefinition handle
    let baseType =
      match t.BaseType.IsNil with
      | true -> ""
      | false -> image.Describe t.BaseType
    let interfaces =
      [ for impl in t.GetInterfaceImplementations() -> image.Describe (reader.GetInterfaceImplementation impl).Interface ]
      |> List.sort
    // Concatenation, not sprintf: a large assembly has ten thousand types, and `%A` reflects over an enum.
    [ "flags=" + string (int t.Attributes)
      "base=" + baseType
      "interfaces=" + String.Join(";", interfaces)
      "generics=" + string (t.GetGenericParameters().Count) ]

  let private fieldsOf (image: PeImage) (handle: TypeDefinitionHandle) : string list =
    let reader = image.Reader
    [ for fieldHandle in reader.GetTypeDefinition(handle).GetFields() do
        let f = reader.GetFieldDefinition fieldHandle
        yield
          String.Join(
            "|",
            [ reader.GetString f.Name
              string (int f.Attributes)
              image.FieldSignature f.Signature
              String.Join(";", attributesOf image (entityOf TableIndex.Field (MetadataTokens.GetRowNumber fieldHandle))) ]) ]

  /// Properties and events by name and accessors. The signature is carried by the accessors, which are methods.
  let private membersOf (image: PeImage) (handle: TypeDefinitionHandle) : string list =
    let reader = image.Reader
    let t = reader.GetTypeDefinition handle
    let accessor (h: MethodDefinitionHandle) =
      match h.IsNil with
      | true -> "-"
      | false -> image.MethodId h
    [ for p in t.GetProperties() do
        let property = reader.GetPropertyDefinition p
        let accessors = property.GetAccessors()
        yield String.Join("|", [ "property " + reader.GetString property.Name; string (int property.Attributes); accessor accessors.Getter; accessor accessors.Setter ])
      for e in t.GetEvents() do
        let event = reader.GetEventDefinition e
        let accessors = event.GetAccessors()
        yield String.Join("|", [ "event " + reader.GetString event.Name; accessor accessors.Adder; accessor accessors.Remover ]) ]

  let private parametersOf (image: PeImage) (m: MethodDefinition) : string list =
    let reader = image.Reader
    [ for handle in m.GetParameters() do
        let p = reader.GetParameter handle
        yield String.Join("|", [ string p.SequenceNumber; reader.GetString p.Name; string (int p.Attributes) ]) ]

  let private isStaticInitializer (typeKey: string) (name: string) : bool =
    name = ".cctor" || typeKey.StartsWith("<StartupCode$", StringComparison.Ordinal)

  let private bodyOf (image: PeImage) (m: MethodDefinition) : MethodBodyBlock voption =
    match m.RelativeVirtualAddress with
    | 0 -> ValueNone
    | rva -> ValueSome (image.Pe.GetMethodBody rva)

  /// Compare two builds. `previous` is what the running process holds the metadata of.
  let diff (options: DiffOptions) (previous: PeImage) (next: PeImage) : ImageDiff =
    let skipInPrevious (key: string) =
      match options.PreviousProbes with
      | ProbeStripping.KeepEveryInstruction -> false
      | ProbeStripping.StripCoverageProbes _ -> key = CoverageProbe.trackerTypeKey
    let previousTypes = typeTable previous skipInPrevious
    let nextTypes = typeTable next (fun _ -> false)
    let previousByKey = dict previousTypes
    let nextByKey = dict nextTypes
    let refusals = ResizeArray<RudeCause>()
    let verdicts = ResizeArray<MethodVerdict>()

    // Closures that fold to one name in both builds are matched by order. A different number of them on
    // either side cannot be matched, so no pair among them is guessed.
    let ambiguous = HashSet<string>()
    for key, handle in previousTypes do
      let folded = previous.FoldedName handle
      let before = previous.GroupSize folded
      let after = next.GroupSize folded
      match after > 0 && before <> after with
      | true ->
        match ambiguous.Add folded with
        | true -> refusals.Add (RudeCause.ClosureMatchAmbiguous folded)
        | false -> ()
      | false -> ()

    let isAmbiguous (image: PeImage) (handle: TypeDefinitionHandle) = ambiguous.Contains(image.FoldedName handle)

    for key, handle in previousTypes do
      match isAmbiguous previous handle, nextByKey.ContainsKey key with
      | true, _ -> ()
      | false, false -> refusals.Add (RudeCause.TypeRemoved key)
      | false, true -> ()
    for key, handle in nextTypes do
      match isAmbiguous next handle, previousByKey.ContainsKey key with
      | true, _ -> ()
      | false, false -> refusals.Add (RudeCause.TypeAdded key)
      | false, true -> ()

    let compareType (key: string) (previousHandle: TypeDefinitionHandle) (nextHandle: TypeDefinitionHandle) : unit =
      match shapeOf previous previousHandle = shapeOf next nextHandle with
      | false -> refusals.Add (RudeCause.TypeShapeChanged key)
      | true -> ()
      match fieldsOf previous previousHandle = fieldsOf next nextHandle with
      | false -> refusals.Add (RudeCause.FieldsChanged key)
      | true -> ()
      match membersOf previous previousHandle = membersOf next nextHandle with
      | false -> refusals.Add (RudeCause.PropertiesOrEventsChanged key)
      | true -> ()
      let typeAttributes (image: PeImage) (handle: TypeDefinitionHandle) =
        attributesOf image (entityOf TableIndex.TypeDef (MetadataTokens.GetRowNumber handle))
      match typeAttributes previous previousHandle = typeAttributes next nextHandle with
      | false -> refusals.Add (RudeCause.AttributesChanged key)
      | true -> ()

      let methodsOf (image: PeImage) (handle: TypeDefinitionHandle) =
        [ for h in image.Reader.GetTypeDefinition(handle).GetMethods() -> image.MethodId h, h ]
      let previousMethods = methodsOf previous previousHandle
      let nextMethods = methodsOf next nextHandle
      let previousIds = dict previousMethods
      let nextIds = dict nextMethods
      let nextTypeDefinition = next.Reader.GetTypeDefinition nextHandle
      let nameOf (id: string) = id.Substring(0, id.IndexOf '#')
      let verdict (id: string) (change: MethodChange) (h: MethodDefinitionHandle) =
        verdicts.Add
          { Method = { TypeKey = key; Name = nameOf id; Signature = id.Substring(id.IndexOf '#' + 1) }
            Change = change
            NextHandle = h }

      // A method present before and after.
      for id, previousMethod in previousMethods do
        match nextIds.TryGetValue id with
        | false, _ -> ()
        | true, nextMethod ->
          let pm = previous.Reader.GetMethodDefinition previousMethod
          let nm = next.Reader.GetMethodDefinition nextMethod
          let name = nameOf id
          let rude (cause: RudeCause) = verdict id (MethodChange.Rude cause) nextMethod
          match pm.Attributes = nm.Attributes && pm.ImplAttributes = nm.ImplAttributes with
          | false -> rude (RudeCause.MethodFlagsChanged (key, name))
          | true ->
          match parametersOf previous pm = parametersOf next nm with
          | false -> rude (RudeCause.ParametersChanged (key, name))
          | true ->
          let methodAttributes (image: PeImage) (h: MethodDefinitionHandle) =
            attributesOf image (entityOf TableIndex.MethodDef (MetadataTokens.GetRowNumber h))
          match methodAttributes previous previousMethod = methodAttributes next nextMethod with
          | false -> rude (RudeCause.AttributesChanged (sprintf "%s.%s" key name))
          | true ->
          match bodyOf previous pm, bodyOf next nm with
          | ValueNone, ValueNone -> verdict id MethodChange.Unchanged nextMethod
          | ValueSome _, ValueNone
          | ValueNone, ValueSome _ -> rude (RudeCause.MethodFlagsChanged (key, name))
          | ValueSome before, ValueSome after when (match options.PreviousProbes with
                                                    | ProbeStripping.KeepEveryInstruction -> IlCanon.sameWithoutLooking previous before next after
                                                    | ProbeStripping.StripCoverageProbes _ -> false) ->
            // Byte-identical and naming the same things: the common case, found without reading either into instructions.
            verdict id MethodChange.Unchanged nextMethod
          | ValueSome before, ValueSome after ->
            match IlCanon.canonicalize previous before options.PreviousProbes, IlCanon.canonicalize next after ProbeStripping.KeepEveryInstruction with
            | Result.Error refusal, _
            | _, Result.Error refusal -> rude (RudeCause.UnreadableBody (key, name, refusal))
            | Result.Ok a, Result.Ok b ->
              match a = b, isStaticInitializer key name with
              | true, _ -> verdict id MethodChange.Unchanged nextMethod
              | false, true -> rude (RudeCause.StaticInitializerChanged (key, name))
              | false, false -> verdict id MethodChange.BodyChanged nextMethod

      // A method that is gone, or whose signature changed.
      let nextOnlyNames = nextMethods |> List.filter (fun (id, _) -> not (previousIds.ContainsKey id)) |> List.map (fun (id, _) -> nameOf id) |> Set.ofList
      for id, previousMethod in previousMethods do
        match nextIds.ContainsKey id with
        | true -> ()
        | false ->
          let name = nameOf id
          let virtualMember = (previous.Reader.GetMethodDefinition previousMethod).Attributes.HasFlag MethodAttributes.Virtual
          match nextOnlyNames.Contains name, virtualMember with
          | true, true -> refusals.Add (RudeCause.VirtualSignatureChanged (key, name))
          | true, false -> refusals.Add (RudeCause.SignatureChanged (key, name))
          | false, _ -> refusals.Add (RudeCause.MethodRemoved (key, name))

      // A method that is new.
      let previousNames = previousMethods |> List.map (fun (id, _) -> nameOf id) |> Set.ofList
      let genericOwner = nextTypeDefinition.GetGenericParameters().Count > 0
      let interfaceOwner = nextTypeDefinition.Attributes.HasFlag TypeAttributes.Interface
      for id, nextMethod in nextMethods do
        match previousIds.ContainsKey id with
        | true -> ()
        | false ->
          let name = nameOf id
          // Reported once as a signature change when the name was already there.
          match previousNames.Contains name with
          | true -> ()
          | false ->
            let nm = next.Reader.GetMethodDefinition nextMethod
            let support =
              match nm.Attributes.HasFlag MethodAttributes.Virtual, name.StartsWith(".", StringComparison.Ordinal), nm.GetGenericParameters().Count > 0, genericOwner, interfaceOwner with
              | true, _, _, _, _ -> AddedSupport.Unsupported "a virtual member changes what every override has to be"
              | _, true, _, _, _ -> AddedSupport.Unsupported "a constructor cannot be added to a running type"
              | _, _, true, _, _ -> AddedSupport.Unsupported "a generic method cannot be added by this emitter"
              | _, _, _, true, _ -> AddedSupport.Unsupported "the type is generic"
              | _, _, _, _, true -> AddedSupport.Unsupported "the type is an interface"
              | false, false, false, false, false -> AddedSupport.Supported
            match support with
            | AddedSupport.Unsupported why -> verdict id (MethodChange.Rude (RudeCause.AddedMethodUnsupported (key, name, why))) nextMethod
            | AddedSupport.Supported ->
              match bodyOf next nm with
              | ValueNone -> verdict id MethodChange.Added nextMethod
              | ValueSome after ->
                match IlCanon.canonicalize next after ProbeStripping.KeepEveryInstruction with
                | Result.Error refusal -> verdict id (MethodChange.Rude (RudeCause.UnreadableBody (key, name, refusal))) nextMethod
                | Result.Ok _ -> verdict id MethodChange.Added nextMethod

    for key, previousHandle in previousTypes do
      match isAmbiguous previous previousHandle, nextByKey.TryGetValue key with
      | false, (true, nextHandle) -> compareType key previousHandle nextHandle
      | _ -> ()

    { Methods = List.ofSeq verdicts
      Refusals = List.ofSeq refusals }
