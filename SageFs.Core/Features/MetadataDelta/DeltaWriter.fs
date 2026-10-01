/// Writes the metadata delta that takes a running assembly from one build to the next.
///
/// A delta is three byte arrays the runtime's `MetadataUpdater.ApplyUpdate` takes: a metadata blob (the
/// rows that changed or were added), an IL blob (the new method bodies) and an optional PDB. This writes
/// the first two with System.Reflection.Metadata's own builders, from a baseline assembly (what the
/// process has loaded) and a fresh build of the same project.
///
/// What it carries: a new body for a method, with every token in it re-expressed in the BASELINE's row
/// numbering (an existing row when the baseline has one that names the same thing, a new row in the
/// delta when it does not), and a method added to a type the process already runs. New rows can be
/// AssemblyRef, TypeRef, TypeSpec, MemberRef, MethodSpec and StandAloneSig, and string literals go to the
/// delta's own #US heap.
///
/// A delta is applied to a process once and cannot be taken back, and the next one has to know what the
/// last one added (the heaps and row counts grow, and the module's EncId is chained). That knowledge is the
/// `DeltaChain`: it is a value, `Prepare` does not change it, and `Commit` returns the chain for the next
/// save. A delta that did not apply is dropped and the chain it came from is still the right one.
///
/// The writer fails closed: a `calli`, a signature element it does not know, a token from a table it has
/// no row writer for, all come back as a refusal and write nothing.
///
/// Prior art: dotnet/fsharp#19941 (Nat Elkins, unmerged) writes these tables from inside the compiler.
/// This one reads two finished assemblies instead, so it needs no compiler flag. No code is taken from it.
namespace SageFs.Features.MetadataDelta

open System
open System.Collections.Generic
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335

/// What the runtime has to report before it can take a delta. The names are the runtime's own capability
/// strings (`MetadataUpdater.GetCapabilities()`).
[<RequireQualifiedAccess>]
type RequiredFeature =
  | Baseline
  | AddMethodToExistingType
  | GenericUpdateMethod

[<RequireQualifiedAccess>]
module RequiredFeature =
  let capabilityName (feature: RequiredFeature) : string =
    match feature with
    | RequiredFeature.Baseline -> "Baseline"
    | RequiredFeature.AddMethodToExistingType -> "AddMethodToExistingType"
    | RequiredFeature.GenericUpdateMethod -> "GenericUpdateMethod"

/// The bytes of one delta and what it carries.
type DeltaPayload =
  { Generation: int
    Metadata: byte array
    Il: byte array
    Updated: MethodId list
    AddedMethods: MethodId list
    Requires: RequiredFeature list }

/// An exception the writer raises inside one prepare and turns into a refusal at the edge.
exception internal RudeCauseRaised of RudeCause

exception internal IlRefusalRaised of IlRefusal

/// How a signature blob is laid out, so the walker knows where the types start.
[<RequireQualifiedAccess>]
type internal SignatureShape =
  | TypeSpec
  | Field
  | Locals
  | MethodInstantiation
  | MethodOrProperty

/// Rewrites the type tokens inside a signature blob into another numbering, and copies every other byte.
[<Sealed>]
type internal SignatureWalker(blob: byte array, mapType: int -> int) =
  let output = ResizeArray<byte>(blob.Length + 8)
  let mutable position = 0

  let readByte () : byte =
    match position < blob.Length with
    | false -> raise (IlRefusalRaised (IlRefusal.MalformedBody "a signature ends early"))
    | true ->
      let b = blob[position]
      position <- position + 1
      b

  let peekByte () : int = (match position < blob.Length with | true -> int blob[position] | false -> -1)

  let writeUInt (value: int) : unit =
    match value with
    | v when v < 0x80 -> output.Add(byte v)
    | v when v < 0x4000 ->
      output.Add(byte (0x80 ||| (v >>> 8)))
      output.Add(byte (v &&& 0xFF))
    | v ->
      output.Add(byte (0xC0 ||| (v >>> 24)))
      output.Add(byte ((v >>> 16) &&& 0xFF))
      output.Add(byte ((v >>> 8) &&& 0xFF))
      output.Add(byte (v &&& 0xFF))

  /// Read a compressed unsigned integer.
  let readUInt () : int =
    let b0 = int (readByte ())
    match b0 with
    | _ when b0 &&& 0x80 = 0 -> b0
    | _ when b0 &&& 0xC0 = 0x80 -> ((b0 &&& 0x3F) <<< 8) ||| int (readByte ())
    | _ ->
      let b1 = int (readByte ())
      let b2 = int (readByte ())
      let b3 = int (readByte ())
      ((b0 &&& 0x1F) <<< 24) ||| (b1 <<< 16) ||| (b2 <<< 8) ||| b3

  /// Copy a compressed unsigned integer through and say what it was.
  let copyUInt () : int =
    let value = readUInt ()
    writeUInt value
    value

  /// Copy a compressed signed integer through. Its value is never needed.
  let copySigned () : unit =
    let b0 = peekByte ()
    let length =
      match b0 with
      | _ when b0 &&& 0x80 = 0 -> 1
      | _ when b0 &&& 0xC0 = 0x80 -> 2
      | _ -> 4
    for _ in 1 .. length do
      output.Add(readByte ())

  let codedType () : unit =
    let coded = readUInt ()
    let rid = coded >>> 2
    let table =
      match coded &&& 3 with
      | 0 -> 0x02
      | 1 -> 0x01
      | 2 -> 0x1B
      | _ -> raise (IlRefusalRaised (IlRefusal.UnsupportedSignature "a coded type index with tag 3"))
    let mapped = mapType ((table <<< 24) ||| rid)
    let mappedTag =
      match mapped >>> 24 with
      | 0x02 -> 0
      | 0x01 -> 1
      | 0x1B -> 2
      | other -> raise (IlRefusalRaised (IlRefusal.UnsupportedToken other))
    writeUInt (((mapped &&& 0xFFFFFF) <<< 2) ||| mappedTag)

  let rec walkType () : unit =
    let element = int (readByte ())
    output.Add(byte element)
    match element with
    | 0x01 | 0x02 | 0x03 | 0x04 | 0x05 | 0x06 | 0x07 | 0x08 | 0x09 | 0x0A | 0x0B | 0x0C | 0x0D | 0x0E
    | 0x16 | 0x18 | 0x19 | 0x1C -> ()
    | 0x0F | 0x1D ->
      walkModifiers ()
      walkType ()
    | 0x10 | 0x45 -> walkType ()
    | 0x11 | 0x12 -> codedType ()
    | 0x13 | 0x1E -> copyUInt () |> ignore
    | 0x15 ->
      output.Add(readByte ())
      codedType ()
      let count = copyUInt ()
      for _ in 1 .. count do
        walkType ()
    | 0x14 ->
      walkType ()
      copyUInt () |> ignore
      let sizes = copyUInt ()
      for _ in 1 .. sizes do
        copyUInt () |> ignore
      let bounds = copyUInt ()
      for _ in 1 .. bounds do
        copySigned ()
    | 0x1F | 0x20 ->
      codedType ()
      walkType ()
    | 0x41 -> walkType ()
    | 0x1B -> raise (IlRefusalRaised (IlRefusal.UnsupportedSignature "a function pointer"))
    | other -> raise (IlRefusalRaised (IlRefusal.UnsupportedSignature (sprintf "element type 0x%X" other)))

  and walkModifiers () : unit =
    match peekByte () with
    | 0x1F
    | 0x20 ->
      output.Add(readByte ())
      codedType ()
      walkModifiers ()
    | _ -> ()

  member _.Rewrite(shape: SignatureShape) : byte array =
    match shape with
    | SignatureShape.TypeSpec -> walkType ()
    | SignatureShape.Field ->
      output.Add(readByte ())
      walkType ()
    | SignatureShape.Locals ->
      output.Add(readByte ())
      let count = copyUInt ()
      for _ in 1 .. count do
        walkType ()
    | SignatureShape.MethodInstantiation ->
      output.Add(readByte ())
      let count = copyUInt ()
      for _ in 1 .. count do
        walkType ()
    | SignatureShape.MethodOrProperty ->
      let header = readByte ()
      output.Add header
      match int header &&& 0x10 with
      | 0 -> ()
      | _ -> copyUInt () |> ignore
      let count = copyUInt ()
      walkType ()
      for _ in 1 .. count do
        walkType ()
    match position = blob.Length with
    | true -> output.ToArray()
    | false -> raise (IlRefusalRaised (IlRefusal.MalformedBody "a signature has bytes after its last type"))

/// The row of a method the delta may have to write again.
type internal MethodRow =
  { Token: int
    Attributes: MethodAttributes
    ImplAttributes: MethodImplAttributes
    Name: string
    /// The signature in the BASELINE's numbering.
    Signature: byte array
    FirstParameter: int }

/// How big each heap of the metadata is, counting every delta already applied.
type internal HeapSizes =
  { UserString: int
    String: int
    Blob: int
    Guid: int }

/// What the baseline assembly already has, by what each row names. Built on first use, because most saves
/// touch a handful of rows out of tens of thousands.
[<Sealed>]
type internal BaselineIndex(baseline: PeImage) =
  let reader = baseline.Reader

  let methods =
    lazy
      (let table = Dictionary<string, MethodRow>()
       let mutable firstParameter = 1
       for handle in reader.MethodDefinitions do
         let m = reader.GetMethodDefinition handle
         let key = baseline.TypeKey(m.GetDeclaringType()) + "::" + baseline.MethodId handle
         table[key] <-
           { Token = MetadataTokens.GetToken(MetadataTokens.EntityHandle(TableIndex.MethodDef, MetadataTokens.GetRowNumber handle))
             Attributes = m.Attributes
             ImplAttributes = m.ImplAttributes
             Name = reader.GetString m.Name
             Signature = reader.GetBlobBytes m.Signature
             FirstParameter = firstParameter }
         firstParameter <- firstParameter + m.GetParameters().Count
       table)

  let tableOf (table: TableIndex) (describe: int -> string) =
    lazy
      (let found = Dictionary<string, int>()
       for row in 1 .. reader.GetTableRowCount table do
         found.TryAdd(describe row, (int table <<< 24) ||| row) |> ignore
       found)

  let typeDefs = tableOf TableIndex.TypeDef (fun row -> baseline.TypeKey(MetadataTokens.TypeDefinitionHandle row))
  let typeRefs = tableOf TableIndex.TypeRef (fun row -> baseline.Describe(MetadataTokens.EntityHandle(TableIndex.TypeRef, row)))
  let typeSpecs = tableOf TableIndex.TypeSpec (fun row -> baseline.Describe(MetadataTokens.EntityHandle(TableIndex.TypeSpec, row)))
  let memberRefs = tableOf TableIndex.MemberRef (fun row -> baseline.Describe(MetadataTokens.EntityHandle(TableIndex.MemberRef, row)))
  let methodSpecs = tableOf TableIndex.MethodSpec (fun row -> baseline.Describe(MetadataTokens.EntityHandle(TableIndex.MethodSpec, row)))
  let fields =
    lazy
      (let found = Dictionary<string, int>()
       for handle in reader.FieldDefinitions do
         found.TryAdd(baseline.Describe(MetadataTokens.EntityHandle(TableIndex.Field, MetadataTokens.GetRowNumber handle)), MetadataTokens.GetToken(MetadataTokens.EntityHandle(TableIndex.Field, MetadataTokens.GetRowNumber handle))) |> ignore
       found)
  let assemblies =
    lazy
      (let found = Dictionary<string, int>()
       for handle in reader.AssemblyReferences do
         found.TryAdd(reader.GetString((reader.GetAssemblyReference handle).Name),MetadataTokens.GetToken(MetadataTokens.EntityHandle(TableIndex.AssemblyRef, MetadataTokens.GetRowNumber handle))) |> ignore
       found)
  let localSignatures =
    lazy
      (let found = Dictionary<string, int>()
       for row in 1 .. reader.GetTableRowCount TableIndex.StandAloneSig do
         let handle = MetadataTokens.StandaloneSignatureHandle row
         let blob = reader.GetBlobReader(reader.GetStandaloneSignature(handle).Signature)
         let mutable b = blob
         match b.ReadByte() with
         | 0x07uy -> found.TryAdd(String.Join("|", baseline.LocalSignature(reader.GetStandaloneSignature(handle).Signature)), (0x11 <<< 24) ||| row) |> ignore
         | _ -> ()
       found)

  member _.Image = baseline
  member _.Method(key: string) : MethodRow voption =
    match methods.Value.TryGetValue key with
    | true, row -> ValueSome row
    | false, _ -> ValueNone
  member _.TypeDef(key: string) : int voption = (match typeDefs.Value.TryGetValue key with | true, t -> ValueSome t | false, _ -> ValueNone)
  member _.TypeRef(text: string) : int voption = (match typeRefs.Value.TryGetValue text with | true, t -> ValueSome t | false, _ -> ValueNone)
  member _.TypeSpec(text: string) : int voption = (match typeSpecs.Value.TryGetValue text with | true, t -> ValueSome t | false, _ -> ValueNone)
  member _.MemberRef(text: string) : int voption = (match memberRefs.Value.TryGetValue text with | true, t -> ValueSome t | false, _ -> ValueNone)
  member _.MethodSpec(text: string) : int voption = (match methodSpecs.Value.TryGetValue text with | true, t -> ValueSome t | false, _ -> ValueNone)
  member _.Field(text: string) : int voption = (match fields.Value.TryGetValue text with | true, t -> ValueSome t | false, _ -> ValueNone)
  member _.Assembly(name: string) : int voption = (match assemblies.Value.TryGetValue name with | true, t -> ValueSome t | false, _ -> ValueNone)
  member _.LocalSignature(text: string) : int voption = (match localSignatures.Value.TryGetValue text with | true, t -> ValueSome t | false, _ -> ValueNone)
  member _.RowCount(table: TableIndex) : int = reader.GetTableRowCount table

/// Rows a delta added, by what each names, so the next delta finds them instead of adding them again.
type internal KnownRows =
  { TypeRefs: Map<string, int>
    TypeSpecs: Map<string, int>
    MemberRefs: Map<string, int>
    MethodSpecs: Map<string, int>
    Assemblies: Map<string, int>
    LocalSignatures: Map<string, int>
    Methods: Map<string, MethodRow> }

[<RequireQualifiedAccess>]
module internal KnownRows =
  let empty =
    { TypeRefs = Map.empty
      TypeSpecs = Map.empty
      MemberRefs = Map.empty
      MethodSpecs = Map.empty
      Assemblies = Map.empty
      LocalSignatures = Map.empty
      Methods = Map.empty }

/// Everything one prepare decided that a commit makes permanent.
type internal Pending =
  { Rows: KnownRows
    /// Rows added per table (indexed by table number), this delta only.
    Added: int array
    Heaps: HeapSizes
    EncId: Guid
    Next: PeImage }

/// The state after a delta applied.
type internal ChainState =
  { Index: BaselineIndex
    Probes: ProbeStripping
    Generation: int
    EncId: Guid
    Heaps: HeapSizes
    /// Rows added per table across every delta so far.
    Added: int array
    Rows: KnownRows
    /// The build the last delta was made from, or the baseline itself before the first one.
    Previous: PeImage }

/// A delta ready to apply, and what committing it changes.
[<Sealed>]
type PreparedDelta internal (payload: DeltaPayload, pending: Pending, from: int) =
  member _.Payload : DeltaPayload = payload
  member internal _.Pending : Pending = pending
  /// The generation of the chain this was prepared from, so a commit onto another chain is refused.
  member internal _.From : int = from

/// What preparing a delta came to.
[<RequireQualifiedAccess>]
type PrepareOutcome =
  /// Every method is the same as the build the process already runs.
  | NothingChanged
  | Ready of PreparedDelta
  | Refused of RudeCause list

/// The chain of deltas applied to one baseline assembly.
[<Sealed>]
type DeltaChain private (state: ChainState) =

  static let tableCount = 64

  static let alignFour (n: int) = (n + 3) &&& ~~~3

  /// Start the chain at a baseline: the assembly the process loaded.
  static member Start(baseline: PeImage, probes: ProbeStripping) : DeltaChain =
    let reader = baseline.Reader
    DeltaChain
      { Index = BaselineIndex baseline
        Probes = probes
        Generation = 0
        EncId = Guid.Empty
        Heaps =
          { UserString = reader.GetHeapSize HeapIndex.UserString
            String = reader.GetHeapSize HeapIndex.String
            Blob = reader.GetHeapSize HeapIndex.Blob
            Guid = reader.GetHeapSize HeapIndex.Guid }
        Added = Array.zeroCreate tableCount
        Rows = KnownRows.empty
        Previous = baseline }

  /// How many deltas have been committed.
  member _.Generation : int = state.Generation

  /// Compare `next` with what the process runs, and write the delta that takes it there.
  member this.Prepare(encId: Guid, next: PeImage) : PrepareOutcome =
    let diff = MethodDiff.diff { PreviousProbes = (match state.Generation with | 0 -> state.Probes | _ -> ProbeStripping.KeepEveryInstruction) } state.Previous next
    this.PrepareFrom(encId, next, diff)

  /// The writing half of `Prepare`, for a diff the caller already has.
  member internal _.PrepareFrom(encId: Guid, next: PeImage, diff: ImageDiff) : PrepareOutcome =
    match ImageDiff.causes diff with
    | _ :: _ as causes -> PrepareOutcome.Refused causes
    | [] ->
    match ImageDiff.toWrite diff with
    | [] -> PrepareOutcome.NothingChanged
    | writes ->
      try
        PrepareOutcome.Ready (DeltaChain.write state encId next writes)
      with
      | RudeCauseRaised cause -> PrepareOutcome.Refused [ cause ]
      | IlRefusalRaised refusal -> PrepareOutcome.Refused [ RudeCause.UnreadableBody ("(signature)", "(unknown)", refusal) ]

  /// The chain after a prepared delta applied.
  member _.Commit(prepared: PreparedDelta) : DeltaChain =
    match prepared.From = state.Generation with
    | false -> invalidArg "prepared" (sprintf "the delta was prepared from generation %d, and this chain is at %d" prepared.From state.Generation)
    | true ->
      let pending = prepared.Pending
      let added = Array.init tableCount (fun i -> state.Added[i] + pending.Added[i])
      DeltaChain
        { state with
            Generation = state.Generation + 1
            EncId = pending.EncId
            Heaps = pending.Heaps
            Added = added
            Rows = pending.Rows
            Previous = pending.Next }

  static member private write (state: ChainState) (encId: Guid) (next: PeImage) (writes: MethodVerdict list) : PreparedDelta =
    let baseline = state.Index.Image
    let baselineReader = baseline.Reader
    let nextReader = next.Reader
    let builder =
      MetadataBuilder(
        userStringHeapStartOffset = state.Heaps.UserString,
        stringHeapStartOffset = state.Heaps.String,
        blobHeapStartOffset = state.Heaps.Blob,
        guidHeapStartOffset = state.Heaps.Guid)

    let pendingAdded = Array.zeroCreate<int> tableCount
    let ledger = ref state.Rows
    // The rows this delta adds, in the order they are written, for the EncLog.
    let touched = SortedSet<int>()
    let nextRow (table: TableIndex) : int =
      pendingAdded[int table] <- pendingAdded[int table] + 1
      state.Index.RowCount table + state.Added[int table] + pendingAdded[int table]

    let methodKey (typeKey: string) (id: string) = typeKey + "::" + id

    // Methods this delta adds get their rows first, so a body that calls one finds it.
    let addedMethods =
      writes
      |> List.filter (fun v -> v.Change = MethodChange.Added)
      |> List.sortBy (fun v -> MetadataTokens.GetRowNumber v.NextHandle)
    let addedRows = Dictionary<string, int>()
    for v in addedMethods do
      let rid = nextRow TableIndex.MethodDef
      addedRows[methodKey v.Method.TypeKey (v.Method.Name + "#" + v.Method.Signature)] <- rid

    let rec map (token: int) : int =
      let table = token >>> 24
      let row = token &&& 0xFFFFFF
      match table with
      | 0x02 ->
        let key = next.TypeKey(MetadataTokens.TypeDefinitionHandle row)
        match state.Index.TypeDef key with
        | ValueSome mapped -> mapped
        | ValueNone -> raise (RudeCauseRaised (RudeCause.TypeAdded key))
      | 0x06 ->
        let handle = MetadataTokens.MethodDefinitionHandle row
        let m = nextReader.GetMethodDefinition handle
        let key = methodKey (next.TypeKey(m.GetDeclaringType())) (next.MethodId handle)
        match state.Index.Method key, Map.tryFind key ledger.Value.Methods, addedRows.TryGetValue key with
        | ValueSome existing, _, _ -> existing.Token
        | _, Some added, _ -> added.Token
        | _, _, (true, rid) -> (0x06 <<< 24) ||| rid
        | _ -> raise (RudeCauseRaised (RudeCause.MethodRemoved (next.TypeKey(m.GetDeclaringType()), nextReader.GetString m.Name)))
      | 0x04 ->
        let text = next.Describe(MetadataTokens.EntityHandle token)
        match state.Index.Field text with
        | ValueSome mapped -> mapped
        | ValueNone -> raise (RudeCauseRaised (RudeCause.FieldsChanged text))
      | 0x01 ->
        let text = next.Describe(MetadataTokens.EntityHandle token)
        match state.Index.TypeRef text, Map.tryFind text ledger.Value.TypeRefs with
        | ValueSome mapped, _ -> mapped
        | _, Some mapped -> mapped
        | _ ->
          let t = nextReader.GetTypeReference(MetadataTokens.TypeReferenceHandle row)
          let scope : EntityHandle =
            match t.ResolutionScope.Kind with
            | HandleKind.AssemblyReference ->
              let a = nextReader.GetAssemblyReference(Handles.assemblyRef t.ResolutionScope)
              let name = nextReader.GetString a.Name
              let assembly =
                match state.Index.Assembly name, Map.tryFind name ledger.Value.Assemblies with
                | ValueSome existing, _ -> existing
                | _, Some existing -> existing
                | _ ->
                  let rid = nextRow TableIndex.AssemblyRef
                  builder.AddAssemblyReference(
                    builder.GetOrAddString name,
                    a.Version,
                    builder.GetOrAddString(nextReader.GetString a.Culture),
                    builder.GetOrAddBlob(nextReader.GetBlobBytes a.PublicKeyOrToken),
                    a.Flags,
                    BlobHandle()) |> ignore
                  let added = (0x23 <<< 24) ||| rid
                  touched.Add added |> ignore
                  ledger.Value <- { ledger.Value with Assemblies = Map.add name added ledger.Value.Assemblies }
                  added
              MetadataTokens.EntityHandle assembly
            | HandleKind.TypeReference -> MetadataTokens.EntityHandle(map (MetadataTokens.GetToken t.ResolutionScope))
            | other -> raise (IlRefusalRaised (IlRefusal.UnsupportedSignature (sprintf "a type reference scoped by %A" other)))
          let rid = nextRow TableIndex.TypeRef
          builder.AddTypeReference(scope, builder.GetOrAddString(nextReader.GetString t.Namespace), builder.GetOrAddString(nextReader.GetString t.Name)) |> ignore
          let added = (0x01 <<< 24) ||| rid
          touched.Add added |> ignore
          ledger.Value <- { ledger.Value with TypeRefs = Map.add text added ledger.Value.TypeRefs }
          added
      | 0x1B ->
        let text = next.Describe(MetadataTokens.EntityHandle token)
        match state.Index.TypeSpec text, Map.tryFind text ledger.Value.TypeSpecs with
        | ValueSome mapped, _ -> mapped
        | _, Some mapped -> mapped
        | _ ->
          let blob = nextReader.GetBlobBytes(nextReader.GetTypeSpecification(MetadataTokens.TypeSpecificationHandle row).Signature)
          let rewritten = SignatureWalker(blob, map).Rewrite SignatureShape.TypeSpec
          let rid = nextRow TableIndex.TypeSpec
          builder.AddTypeSpecification(builder.GetOrAddBlob rewritten) |> ignore
          let added = (0x1B <<< 24) ||| rid
          touched.Add added |> ignore
          ledger.Value <- { ledger.Value with TypeSpecs = Map.add text added ledger.Value.TypeSpecs }
          added
      | 0x0A ->
        let text = next.Describe(MetadataTokens.EntityHandle token)
        match state.Index.MemberRef text, Map.tryFind text ledger.Value.MemberRefs with
        | ValueSome mapped, _ -> mapped
        | _, Some mapped -> mapped
        | _ ->
          let m = nextReader.GetMemberReference(MetadataTokens.MemberReferenceHandle row)
          let parent = map (MetadataTokens.GetToken m.Parent)
          let shape =
            match m.GetKind() with
            | MemberReferenceKind.Field -> SignatureShape.Field
            | _ -> SignatureShape.MethodOrProperty
          let rewritten = SignatureWalker(nextReader.GetBlobBytes m.Signature, map).Rewrite shape
          let rid = nextRow TableIndex.MemberRef
          builder.AddMemberReference(MetadataTokens.EntityHandle parent, builder.GetOrAddString(nextReader.GetString m.Name), builder.GetOrAddBlob rewritten) |> ignore
          let added = (0x0A <<< 24) ||| rid
          touched.Add added |> ignore
          ledger.Value <- { ledger.Value with MemberRefs = Map.add text added ledger.Value.MemberRefs }
          added
      | 0x2B ->
        let text = next.Describe(MetadataTokens.EntityHandle token)
        match state.Index.MethodSpec text, Map.tryFind text ledger.Value.MethodSpecs with
        | ValueSome mapped, _ -> mapped
        | _, Some mapped -> mapped
        | _ ->
          let s = nextReader.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle row)
          let meth = map (MetadataTokens.GetToken s.Method)
          let rewritten = SignatureWalker(nextReader.GetBlobBytes s.Signature, map).Rewrite SignatureShape.MethodInstantiation
          let rid = nextRow TableIndex.MethodSpec
          builder.AddMethodSpecification(MetadataTokens.EntityHandle meth, builder.GetOrAddBlob rewritten) |> ignore
          let added = (0x2B <<< 24) ||| rid
          touched.Add added |> ignore
          ledger.Value <- { ledger.Value with MethodSpecs = Map.add text added ledger.Value.MethodSpecs }
          added
      | 0x11 ->
        let signature = nextReader.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle row).Signature
        let text = String.Join("|", next.LocalSignature signature)
        match state.Index.LocalSignature text, Map.tryFind text ledger.Value.LocalSignatures with
        | ValueSome mapped, _ -> mapped
        | _, Some mapped -> mapped
        | _ ->
          let rewritten = SignatureWalker(nextReader.GetBlobBytes signature, map).Rewrite SignatureShape.Locals
          let rid = nextRow TableIndex.StandAloneSig
          builder.AddStandaloneSignature(builder.GetOrAddBlob rewritten) |> ignore
          let added = (0x11 <<< 24) ||| rid
          touched.Add added |> ignore
          ledger.Value <- { ledger.Value with LocalSignatures = Map.add text added ledger.Value.LocalSignatures }
          added
      | other -> raise (IlRefusalRaised (IlRefusal.UnsupportedToken other))

    let ilStream = BlobBuilder()
    let bodies = MethodBodyStreamEncoder ilStream

    /// Writes one body and says where it starts in the IL stream.
    let writeBody (typeKey: string) (name: string) (body: MethodBodyBlock) : int =
      try
        let il = body.GetILBytes()
        let rewritten = Array.copy il
        match IlCanon.scan il with
        | Result.Error refusal -> raise (IlRefusalRaised refusal)
        | Result.Ok instructions ->
          for instruction in instructions do
            match instruction.Operand with
            | RawOperand.Token (kind, token) ->
              let operandAt = instruction.Offset + instruction.Length - 4
              let replacement =
                match kind with
                | TokenKind.Entity -> map token
                | TokenKind.UserString ->
                  match state.Heaps.UserString with
                  | 0 -> raise (IlRefusalRaised IlRefusal.NoUserStringHeap)
                  | _ -> ()
                  let text = nextReader.GetUserString(MetadataTokens.UserStringHandle(token &&& 0xFFFFFF))
                  0x70000000 ||| MetadataTokens.GetHeapOffset(builder.GetOrAddUserString text)
              BitConverter.TryWriteBytes(Span<byte>(rewritten, operandAt, 4), replacement) |> ignore
            | _ -> ()
        let locals =
          match body.LocalSignature.IsNil with
          | true -> StandaloneSignatureHandle()
          | false -> MetadataTokens.StandaloneSignatureHandle(map ((0x11 <<< 24) ||| MetadataTokens.GetRowNumber body.LocalSignature) &&& 0xFFFFFF)
        let regions = body.ExceptionRegions
        let small =
          ExceptionRegionEncoder.IsSmallRegionCount regions.Length
          && regions |> Seq.forall (fun r -> ExceptionRegionEncoder.IsSmallExceptionRegion(r.TryOffset, r.TryLength) && ExceptionRegionEncoder.IsSmallExceptionRegion(r.HandlerOffset, r.HandlerLength))
        let written =
          bodies.AddMethodBody(
            rewritten.Length,
            body.MaxStack,
            regions.Length,
            small,
            locals,
            (match body.LocalVariablesInitialized with
             | true -> MethodBodyAttributes.InitLocals
             | false -> MethodBodyAttributes.None))
        BlobWriter(written.Instructions).WriteBytes rewritten
        for region in regions do
          let catchType =
            match region.Kind, region.CatchType.IsNil with
            | ExceptionRegionKind.Catch, false -> MetadataTokens.EntityHandle(map (MetadataTokens.GetToken region.CatchType))
            | _ -> EntityHandle()
          written.ExceptionRegions.Add(region.Kind, region.TryOffset, region.TryLength, region.HandlerOffset, region.HandlerLength, catchType, region.FilterOffset) |> ignore
        written.Offset
      with IlRefusalRaised refusal -> raise (RudeCauseRaised (RudeCause.UnreadableBody (typeKey, name, refusal)))

    // Updated methods first (existing rows, ascending), then the added ones, which sort after them.
    let updates =
      writes
      |> List.filter (fun v -> v.Change = MethodChange.BodyChanged)
      |> List.map (fun v ->
        let key = methodKey v.Method.TypeKey (v.Method.Name + "#" + v.Method.Signature)
        let row =
          match state.Index.Method key, Map.tryFind key state.Rows.Methods with
          | ValueSome existing, _ -> existing
          | _, Some added -> added
          | _ -> raise (RudeCauseRaised (RudeCause.MethodRemoved (v.Method.TypeKey, v.Method.Name)))
        v, row)
      |> List.sortBy (fun (_, row) -> row.Token)

    let updatedRows =
      updates
      |> List.map (fun (v, row) ->
        let m = nextReader.GetMethodDefinition v.NextHandle
        let offset = writeBody v.Method.TypeKey v.Method.Name (next.Pe.GetMethodBody m.RelativeVirtualAddress)
        row, offset)

    // Each added method: its body, its signature in the baseline's numbering, its parameters.
    let parameterRows = ResizeArray<int * ParameterAttributes * string * int * int>()
    let addedWritten =
      addedMethods
      |> List.map (fun v ->
        let m = nextReader.GetMethodDefinition v.NextHandle
        let key = methodKey v.Method.TypeKey (v.Method.Name + "#" + v.Method.Signature)
        let rid = addedRows[key]
        let offset =
          match m.RelativeVirtualAddress with
          | 0 -> 0
          | rva -> writeBody v.Method.TypeKey v.Method.Name (next.Pe.GetMethodBody rva)
        let signature =
          try SignatureWalker(nextReader.GetBlobBytes m.Signature, map).Rewrite SignatureShape.MethodOrProperty
          with IlRefusalRaised refusal -> raise (RudeCauseRaised (RudeCause.UnreadableBody (v.Method.TypeKey, v.Method.Name, refusal)))
        let parameters = m.GetParameters() |> Seq.toList
        // The row the parameter list starts at: the next free one, whether or not this method has any.
        let firstParameter = state.Index.RowCount TableIndex.Param + state.Added[int TableIndex.Param] + pendingAdded[int TableIndex.Param] + 1
        for handle in parameters do
          let p = nextReader.GetParameter handle
          let prid = nextRow TableIndex.Param
          parameterRows.Add((prid, p.Attributes, nextReader.GetString p.Name, int p.SequenceNumber, (0x06 <<< 24) ||| rid))
        let parentType =
          match state.Index.TypeDef v.Method.TypeKey with
          | ValueSome parent -> parent
          | ValueNone -> raise (RudeCauseRaised (RudeCause.TypeAdded v.Method.TypeKey))
        let row =
          { Token = (0x06 <<< 24) ||| rid
            Attributes = m.Attributes
            ImplAttributes = m.ImplAttributes
            Name = nextReader.GetString m.Name
            Signature = signature
            FirstParameter = firstParameter }
        ledger.Value <- { ledger.Value with Methods = Map.add key row ledger.Value.Methods }
        row, offset, parentType, v)

    // The module row, then the method rows in token order.
    let moduleName = baselineReader.GetString(baselineReader.GetModuleDefinition().Name)
    let generation = state.Generation + 1
    builder.AddModule(
      generation,
      builder.GetOrAddString moduleName,
      builder.GetOrAddGuid baseline.Mvid,
      builder.GetOrAddGuid encId,
      (match state.Generation with
       | 0 -> GuidHandle()
       | _ -> builder.GetOrAddGuid state.EncId))
    |> ignore
    for row, offset in updatedRows do
      builder.AddMethodDefinition(
        row.Attributes,
        row.ImplAttributes,
        builder.GetOrAddString row.Name,
        builder.GetOrAddBlob row.Signature,
        offset,
        MetadataTokens.ParameterHandle row.FirstParameter)
      |> ignore
    for row, offset, _, _ in addedWritten do
      builder.AddMethodDefinition(
        row.Attributes,
        row.ImplAttributes,
        builder.GetOrAddString row.Name,
        builder.GetOrAddBlob row.Signature,
        offset,
        MetadataTokens.ParameterHandle row.FirstParameter)
      |> ignore
    for _, attributes, name, sequence, _ in parameterRows do
      builder.AddParameter(attributes, builder.GetOrAddString name, sequence) |> ignore

    // EncLog: every changed row, in token order, and for an added row the entry that says what it hangs on.
    let log = SortedDictionary<int, ResizeArray<int * EditAndContinueOperation>>()
    let put (token: int) (entries: (int * EditAndContinueOperation) list) =
      match log.TryGetValue token with
      | true, existing -> existing.AddRange entries
      | false, _ -> log[token] <- ResizeArray entries
    for token in touched do
      put token [ (token, EditAndContinueOperation.Default) ]
    for row, _ in updatedRows do
      put row.Token [ (row.Token, EditAndContinueOperation.Default) ]
    for row, _, parentType, _ in addedWritten do
      put row.Token [ parentType, EditAndContinueOperation.AddMethod; row.Token, EditAndContinueOperation.Default ]
    for prid, _, _, _, owner in parameterRows do
      put ((0x08 <<< 24) ||| prid) [ owner, EditAndContinueOperation.AddParameter; (0x08 <<< 24) ||| prid, EditAndContinueOperation.Default ]
    builder.AddEncLogEntry(EntityHandle.ModuleDefinition, EditAndContinueOperation.Default)
    builder.AddEncMapEntry EntityHandle.ModuleDefinition
    for entry in log do
      for token, operation in entry.Value do
        builder.AddEncLogEntry(MetadataTokens.EntityHandle token, operation)
      builder.AddEncMapEntry(MetadataTokens.EntityHandle entry.Key)

    let root = MetadataRootBuilder builder
    let metadata = BlobBuilder()
    root.Serialize(metadata, 0, 0)
    let heapSizes = root.Sizes.HeapSizes
    let heaps =
      { UserString = state.Heaps.UserString + alignFour heapSizes[int HeapIndex.UserString]
        String = state.Heaps.String + heapSizes[int HeapIndex.String]
        Blob = state.Heaps.Blob + alignFour heapSizes[int HeapIndex.Blob]
        Guid = state.Heaps.Guid + heapSizes[int HeapIndex.Guid] }

    let genericCarrier (v: MethodVerdict) : bool =
      let m = nextReader.GetMethodDefinition v.NextHandle
      m.GetGenericParameters().Count > 0 || nextReader.GetTypeDefinition(m.GetDeclaringType()).GetGenericParameters().Count > 0
    let requires =
      [ yield RequiredFeature.Baseline
        match addedMethods with
        | [] -> ()
        | _ -> yield RequiredFeature.AddMethodToExistingType
        match writes |> List.exists genericCarrier with
        | true -> yield RequiredFeature.GenericUpdateMethod
        | false -> () ]

    let payload =
      { Generation = generation
        Metadata = metadata.ToArray()
        Il = ilStream.ToArray()
        Updated = updates |> List.map (fun (v, _) -> v.Method)
        AddedMethods = addedMethods |> List.map (fun v -> v.Method)
        Requires = requires }
    PreparedDelta(
      payload,
      { Rows = ledger.Value
        Added = pendingAdded
        Heaps = heaps
        EncId = encId
        Next = next },
      state.Generation)
