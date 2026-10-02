/// A compiled assembly read for comparison: the bytes, the metadata reader over them, and the symbolic
/// name of anything a token can point at.
///
/// A token is a row number, and two compiles of the same project number their rows differently as soon as
/// one adds a member. Everything the emitter decides is therefore decided on what a token NAMES, never on
/// its value: `md:Ns.Type::Method#(int32)int32` is the same text in the running assembly and in a fresh
/// build, whichever row each sits in.
///
/// The names fold one thing the compiler moves without the user meaning it. A closure class is named after
/// the line it is written on (`f@18-3`), so a line added above it renames it to `f@19-3`: the number is
/// dropped from the name, and closures that then share a name are told apart by their order in the file.
///
/// This module reads. The delta that patches a running process is the DeltaWriter's.
///
/// Prior art: dotnet/fsharp#19941 (Nat Elkins, unmerged) diffs typed trees inside the compiler. This reads
/// two finished assemblies instead, so it needs no compiler flag. No code is taken from it.
namespace SageFs.Features.MetadataDelta

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open System.Text.RegularExpressions

/// A handle of one kind from an entity handle of that kind. (A static class and not a module: a module with
/// nothing public in it counts against the architecture audit's ceiling on empty modules.)
[<AbstractClass; Sealed>]
type internal Handles =
  static member typeDef (h: EntityHandle) = MetadataTokens.TypeDefinitionHandle(MetadataTokens.GetRowNumber h)
  static member typeRef (h: EntityHandle) = MetadataTokens.TypeReferenceHandle(MetadataTokens.GetRowNumber h)
  static member typeSpec (h: EntityHandle) = MetadataTokens.TypeSpecificationHandle(MetadataTokens.GetRowNumber h)
  static member methodDef (h: EntityHandle) = MetadataTokens.MethodDefinitionHandle(MetadataTokens.GetRowNumber h)
  static member fieldDef (h: EntityHandle) = MetadataTokens.FieldDefinitionHandle(MetadataTokens.GetRowNumber h)
  static member memberRef (h: EntityHandle) = MetadataTokens.MemberReferenceHandle(MetadataTokens.GetRowNumber h)
  static member methodSpec (h: EntityHandle) = MetadataTokens.MethodSpecificationHandle(MetadataTokens.GetRowNumber h)
  static member assemblyRef (h: EntityHandle) = MetadataTokens.AssemblyReferenceHandle(MetadataTokens.GetRowNumber h)

/// The names the compiler moves, folded.
[<Sealed>]
type internal NameFolding private () =

  /// `f@18-3` and `f@18` (a closure named after its line), and the `at line 13` an F# pipeline closure
  /// carries in its name.
  static let lineNumbers = Regex(@"@\d+(-\d+)?$|(?<=at line )\d+", RegexOptions.Compiled)

  static member fold (name: string) : string =
    lineNumbers.Replace(name, MatchEvaluator(fun m -> match m.Value.StartsWith("@", StringComparison.Ordinal) with | true -> "@_" | false -> "_"))

/// The text of a reference, spelled in one place so a reference the writer makes up matches the same reference read
/// from an assembly that already has it.
[<AbstractClass; Sealed>]
type internal ReferenceText =
  /// The scope of a type reference that sits in an assembly.
  static member assemblyScope (assemblyName: string) : string = "asm:" + assemblyName
  /// A type reference, by the text of its scope.
  static member typeRef (scope: string) (ns: string) (name: string) : string = sprintf "tr:%s/%s.%s" scope ns name
  /// A member reference, by the text of its parent and of its signature.
  static member memberRef (parent: string) (name: string) (shape: string) : string = sprintf "mr:%s::%s#%s" parent name shape

/// The keys of a module's types, and how many types fold to each name.
[<AbstractClass; Sealed>]
type internal TypeKeys =

  /// The key of each TypeDef row, by row number minus one: the folded full name, and `#n` after it when
  /// several types fold to the same name (the closures of one binding written on different lines).
  static member build (reader: MetadataReader) : string array * string array * Dictionary<string, int> =
    let count = reader.GetTableRowCount TableIndex.TypeDef
    let rec fullName (handle: TypeDefinitionHandle) : string =
      let t = reader.GetTypeDefinition handle
      let name = NameFolding.fold (reader.GetString t.Name)
      let declaring = t.GetDeclaringType()
      match declaring.IsNil with
      | false -> fullName declaring + "+" + name
      | true ->
        match reader.GetString t.Namespace with
        | "" -> name
        | ns -> ns + "." + name
    let names = Array.init count (fun i -> fullName (MetadataTokens.TypeDefinitionHandle(i + 1)))
    let sizes = Dictionary<string, int>()
    for name in names do
      sizes[name] <- (match sizes.TryGetValue name with | true, n -> n + 1 | false, _ -> 1)
    let seen = Dictionary<string, int>()
    let keys =
      names
      |> Array.map (fun name ->
        match sizes[name] with
        | 1 -> name
        | _ ->
          let ordinal = (match seen.TryGetValue name with | true, n -> n | false, _ -> 0)
          seen[name] <- ordinal + 1
          sprintf "%s#%d" name ordinal)
    keys, names, sizes

/// Reads a signature blob into the text the differ compares.
[<Sealed>]
type internal SignatureText(reader: MetadataReader, typeKey: TypeDefinitionHandle -> string, describeReference: TypeReferenceHandle -> string) =

  let rec provider : ISignatureTypeProvider<string, unit> =
    { new ISignatureTypeProvider<string, unit> with
        member _.GetPrimitiveType(code) = code.ToString()
        member _.GetTypeFromDefinition(_, handle, _) = "td:" + typeKey handle
        member _.GetTypeFromReference(_, handle, _) = describeReference handle
        member _.GetTypeFromSpecification(_, _, handle, _) =
          reader.GetTypeSpecification(handle).DecodeSignature(provider, ())
        member _.GetSZArrayType(element) = element + "[]"
        member _.GetArrayType(element, shape) = sprintf "%s[%d]" element shape.Rank
        member _.GetByReferenceType(element) = element + "&"
        member _.GetPointerType(element) = element + "*"
        member _.GetGenericInstantiation(generic, arguments) = sprintf "%s<%s>" generic (String.Join(",", arguments))
        member _.GetGenericTypeParameter(_, index) = sprintf "!%d" index
        member _.GetGenericMethodParameter(_, index) = sprintf "!!%d" index
        member _.GetFunctionPointerType(signature) =
          sprintf "fnptr(%s)" (String.Join(",", signature.ParameterTypes))
        member _.GetModifiedType(modifier, unmodified, isRequired) =
          sprintf "%s %s %s" unmodified (match isRequired with | true -> "modreq" | false -> "modopt") modifier
        member _.GetPinnedType(element) = element + " pinned" }

  member _.OfEntity(handle: EntityHandle) : string =
    match handle.Kind with
    | HandleKind.TypeDefinition -> "td:" + typeKey (Handles.typeDef handle)
    | HandleKind.TypeReference -> describeReference (Handles.typeRef handle)
    | HandleKind.TypeSpecification -> reader.GetTypeSpecification(Handles.typeSpec handle).DecodeSignature(provider, ())
    | other -> sprintf "?%A" other

  member _.OfMethod(blob: BlobHandle) : string =
    let mutable blobReader = reader.GetBlobReader blob
    let signature = SignatureDecoder<string, unit>(provider, reader, ()).DecodeMethodSignature(&blobReader)
    let parameters : string = String.Join(",", signature.ParameterTypes)
    sprintf "%s(%s)%s%s"
      signature.ReturnType
      parameters
      (match signature.GenericParameterCount with
       | 0 -> ""
       | n -> sprintf "`%d" n)
      (match signature.Header.IsInstance with
       | true -> " instance"
       | false -> "")

  member _.OfField(blob: BlobHandle) : string =
    let mutable blobReader = reader.GetBlobReader blob
    SignatureDecoder<string, unit>(provider, reader, ()).DecodeFieldSignature(&blobReader)

  member _.OfLocals(blob: BlobHandle) : string list =
    let mutable blobReader = reader.GetBlobReader blob
    SignatureDecoder<string, unit>(provider, reader, ()).DecodeLocalSignature(&blobReader) |> Seq.toList

  member _.OfInstantiation(spec: MethodSpecification) : string =
    let arguments : string = String.Join(",", spec.DecodeSignature(provider, ()))
    arguments

/// A compiled assembly, read.
[<Sealed>]
type PeImage private (bytes: byte array) =
  let pe = new PEReader(ImmutableArray.Create<byte> bytes)
  let reader = pe.GetMetadataReader()
  let typeKeys, foldedNames, groupSizes = TypeKeys.build reader

  let typeKey (handle: TypeDefinitionHandle) : string = typeKeys[MetadataTokens.GetRowNumber handle - 1]

  let rec typeReferenceText (handle: TypeReferenceHandle) : string =
    let t = reader.GetTypeReference handle
    let scope =
      match t.ResolutionScope.Kind with
      | HandleKind.AssemblyReference ->
        ReferenceText.assemblyScope (reader.GetString(reader.GetAssemblyReference(Handles.assemblyRef t.ResolutionScope).Name))
      | HandleKind.TypeReference -> typeReferenceText (Handles.typeRef t.ResolutionScope)
      | HandleKind.ModuleReference -> "module"
      | _ -> "other"
    ReferenceText.typeRef scope (reader.GetString t.Namespace) (reader.GetString t.Name)

  let signatures = SignatureText(reader, typeKey, typeReferenceText)

  /// The identity of every method inside its type, by row number minus one: folded name, `#`, signature. The
  /// F# Debug build names a helper after its line (`<Bind>__debug@59`), so the number is folded out of the name, and
  /// methods of one type that then share an id are told apart by their order (`~0`, `~1`). Computed once for the whole
  /// module: every method is asked for several times.
  let methodIds : Lazy<string array> =
    lazy
      (let count = reader.GetTableRowCount TableIndex.MethodDef
       let baseIds =
         Array.init count (fun i ->
           let m = reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(i + 1))
           NameFolding.fold (reader.GetString m.Name) + "#" + signatures.OfMethod m.Signature)
       let ids = Array.copy baseIds
       for typeHandle in reader.TypeDefinitions do
         let rows = reader.GetTypeDefinition(typeHandle).GetMethods() |> Seq.map (fun h -> MetadataTokens.GetRowNumber h - 1) |> Seq.toArray
         let seen = Dictionary<string, int>()
         let sizes = Dictionary<string, int>()
         for row in rows do
           sizes[baseIds[row]] <- (match sizes.TryGetValue baseIds[row] with | true, n -> n + 1 | false, _ -> 1)
         for row in rows do
           match sizes[baseIds[row]] with
           | 1 -> ()
           | _ ->
             let ordinal = (match seen.TryGetValue baseIds[row] with | true, n -> n | false, _ -> 0)
             seen[baseIds[row]] <- ordinal + 1
             ids[row] <- baseIds[row] + "~" + string ordinal
       ids)

  let described = Dictionary<EntityHandle, string>()

  let rec describe (handle: EntityHandle) : string =
    match described.TryGetValue handle with
    | true, cached -> cached
    | false, _ ->
      let result =
        match handle.Kind with
        | HandleKind.TypeDefinition
        | HandleKind.TypeReference
        | HandleKind.TypeSpecification -> signatures.OfEntity handle
        | HandleKind.MethodDefinition ->
          let m = reader.GetMethodDefinition(Handles.methodDef handle)
          "md:" + typeKey (m.GetDeclaringType()) + "::" + methodIds.Value[MetadataTokens.GetRowNumber handle - 1]
        | HandleKind.FieldDefinition ->
          let f = reader.GetFieldDefinition(Handles.fieldDef handle)
          // A field F# makes for a module-level value is named after its line (`counter@49`), so a line added above it
          // renames it, the same way it renames a closure.
          sprintf "fd:%s::%s#%s" (typeKey (f.GetDeclaringType())) (NameFolding.fold (reader.GetString f.Name)) (signatures.OfField f.Signature)
        | HandleKind.MemberReference ->
          let m = reader.GetMemberReference(Handles.memberRef handle)
          let parent =
            match m.Parent.Kind with
            | HandleKind.MethodDefinition ->
              let owner = reader.GetMethodDefinition(Handles.methodDef m.Parent)
              "md:" + typeKey (owner.GetDeclaringType()) + "::" + NameFolding.fold (reader.GetString owner.Name)
            | HandleKind.ModuleReference -> "module"
            | _ -> signatures.OfEntity m.Parent
          let shape =
            match m.GetKind() with
            | MemberReferenceKind.Field -> signatures.OfField m.Signature
            | _ -> signatures.OfMethod m.Signature
          ReferenceText.memberRef parent (reader.GetString m.Name) shape
        | HandleKind.MethodSpecification ->
          let s = reader.GetMethodSpecification(Handles.methodSpec handle)
          sprintf "ms:%s<%s" (describe s.Method) (signatures.OfInstantiation s)
        | other -> sprintf "?%A" other
      described[handle] <- result
      result

  /// Read an assembly from memory. The bytes are held, so the file can be replaced while the image is in use.
  static member OfBytes(bytes: byte array) : PeImage = PeImage bytes

  static member OfFile(path: string) : PeImage = PeImage(File.ReadAllBytes path)

  member _.Bytes : byte array = bytes
  member _.Pe : PEReader = pe
  member _.Reader : MetadataReader = reader
  member _.Mvid : Guid = reader.GetGuid(reader.GetModuleDefinition().Mvid)

  /// The folded, ordinal-disambiguated name of a type: the same in two compiles that differ in line numbers.
  member _.TypeKey(handle: TypeDefinitionHandle) : string = typeKey handle

  /// The folded full name of a type, without the ordinal: what its closure siblings share.
  member _.FoldedName(handle: TypeDefinitionHandle) : string = foldedNames[MetadataTokens.GetRowNumber handle - 1]

  /// How many types of this image fold to `foldedName` (more than one means closures told apart by order).
  member _.GroupSize(foldedName: string) : int =
    match groupSizes.TryGetValue foldedName with
    | true, n -> n
    | false, _ -> 0

  /// What a token names, as text that does not depend on any row number.
  member _.Describe(handle: EntityHandle) : string = describe handle

  member _.MethodSignature(blob: BlobHandle) : string = signatures.OfMethod blob

  member _.FieldSignature(blob: BlobHandle) : string = signatures.OfField blob

  member _.LocalSignature(blob: BlobHandle) : string list = signatures.OfLocals blob

  /// The identity of a method inside its type: name and signature.
  member _.MethodId(handle: MethodDefinitionHandle) : string = methodIds.Value[MetadataTokens.GetRowNumber handle - 1]
