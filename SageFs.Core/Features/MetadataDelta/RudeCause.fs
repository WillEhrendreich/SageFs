/// Why a save cannot be taken as a metadata delta, as a closed set.
///
/// The emitter has three answers for an edit: the delta carries it, there is no change, or it cannot be
/// carried. The third answer is always one of these cases, never a string, so the planner can put the cause
/// on the row, a test can match on the case, and a case the emitter does not know cannot be spelled.
///
/// Every name below is the text of a type or method as the metadata spells it (`Ns.Outer+Inner`,
/// `Name`), so the reader of a refusal finds the thing they edited.
namespace SageFs.Features.MetadataDelta

/// Why a method body could not be read into the form the differ compares.
[<RequireQualifiedAccess>]
type IlRefusal =
  /// A `calli`: its call-site signature lives in a StandAloneSig the writer does not remap yet.
  | CalliNotSupported
  /// An opcode the table does not hold. A new opcode must be added to the table on purpose.
  | UnknownOpcode of value: int
  /// The body ends in the middle of an instruction, or a branch lands between two of them.
  | MalformedBody of detail: string
  /// A signature element the signature walker does not know (a function pointer, an element type past the
  /// ones ECMA-335 lists). The emitter refuses rather than copy bytes it cannot read.
  | UnsupportedSignature of detail: string
  /// An entity token in a table the emitter has no row writer for.
  | UnsupportedToken of table: int
  /// The body has a string literal and the running assembly has no #US heap to grow. Cecil leaves the heap
  /// out of a module with no literals, and a delta that adds one made the runtime read "no string" for the
  /// token. A compiler always writes the heap, so this is the rewritten module's case, and it is refused.
  | NoUserStringHeap

[<RequireQualifiedAccess>]
module IlRefusal =
  /// The one place an `IlRefusal` becomes text.
  let describe (refusal: IlRefusal) : string =
    match refusal with
    | IlRefusal.CalliNotSupported -> "it uses calli, whose call-site signature is not carried"
    | IlRefusal.UnknownOpcode value -> sprintf "it has an opcode the emitter does not know (0x%X)" value
    | IlRefusal.MalformedBody detail -> sprintf "its IL is malformed (%s)" detail
    | IlRefusal.UnsupportedSignature detail -> sprintf "it uses a signature the emitter cannot read (%s)" detail
    | IlRefusal.UnsupportedToken table -> sprintf "it uses a token from table 0x%02X, which the emitter does not write" table
    | IlRefusal.NoUserStringHeap -> "it has a string literal and the running assembly has no user-string heap to add it to"

/// A reason an edit needs a restart.
[<RequireQualifiedAccess>]
type RudeCause =
  /// A type the running assembly has is gone.
  | TypeRemoved of typeName: string
  /// A type the running assembly does not have. A closure class or a record the edit adds is this: the
  /// runtime can add a type, and this emitter does not write one yet.
  | TypeAdded of typeName: string
  /// A field was added, removed or retyped, so the layout of every object of the type changes.
  | FieldsChanged of typeName: string
  /// The base type, the interfaces or the type's own flags changed.
  | TypeShapeChanged of typeName: string
  /// A property or an event was added, removed or changed.
  | PropertiesOrEventsChanged of typeName: string
  /// A custom attribute on a type, method or field changed. The delta does not carry attribute rows.
  | AttributesChanged of subject: string
  /// A method of the running assembly is gone and nothing took its place.
  | MethodRemoved of typeName: string * methodName: string
  /// The same method name, with another signature: callers and overrides have to change with it.
  | SignatureChanged of typeName: string * methodName: string
  /// A virtual, abstract or interface member changed signature, so every override changes with it.
  | VirtualSignatureChanged of typeName: string * methodName: string
  /// The method's own flags changed (visibility, static, virtual, impl flags such as NoInlining).
  | MethodFlagsChanged of typeName: string * methodName: string
  /// The parameter list (names, flags, count) changed.
  | ParametersChanged of typeName: string * methodName: string
  /// A method added to a type that cannot take it: a virtual, a constructor, a generic method, a method of
  /// a generic type or an interface.
  | AddedMethodUnsupported of typeName: string * methodName: string * why: string
  /// A static constructor or a startup class method changed. A delta replaces a body and never runs it
  /// again, so a changed module-level value would be reported as patched and keep its old value.
  | StaticInitializerChanged of typeName: string * methodName: string
  /// Closure classes with the same name but another number of them: which new one replaces which old one
  /// cannot be told from the names, so none is guessed.
  | ClosureMatchAmbiguous of typeName: string
  /// A method body the emitter cannot read.
  | UnreadableBody of typeName: string * methodName: string * refusal: IlRefusal

[<RequireQualifiedAccess>]
module RudeCause =
  /// The one place a `RudeCause` becomes text: what happened, in the words of the user's own code.
  let describe (cause: RudeCause) : string =
    match cause with
    | RudeCause.TypeRemoved t -> sprintf "type %s was removed" t
    | RudeCause.TypeAdded t -> sprintf "type %s was added (a new closure class or type needs a restart)" t
    | RudeCause.FieldsChanged t -> sprintf "the fields of %s changed, which changes the layout of every object of it" t
    | RudeCause.TypeShapeChanged t -> sprintf "the base type, interfaces or flags of %s changed" t
    | RudeCause.PropertiesOrEventsChanged t -> sprintf "a property or event of %s changed" t
    | RudeCause.AttributesChanged s -> sprintf "a custom attribute on %s changed" s
    | RudeCause.MethodRemoved (t, m) -> sprintf "%s.%s was removed" t m
    | RudeCause.SignatureChanged (t, m) -> sprintf "the signature of %s.%s changed" t m
    | RudeCause.VirtualSignatureChanged (t, m) -> sprintf "the signature of the virtual member %s.%s changed, so every override changes with it" t m
    | RudeCause.MethodFlagsChanged (t, m) -> sprintf "the flags of %s.%s changed" t m
    | RudeCause.ParametersChanged (t, m) -> sprintf "the parameters of %s.%s changed" t m
    | RudeCause.AddedMethodUnsupported (t, m, why) -> sprintf "%s.%s was added, and %s" t m why
    | RudeCause.StaticInitializerChanged (t, m) -> sprintf "%s.%s runs at startup and a delta never runs it again" t m
    | RudeCause.ClosureMatchAmbiguous t -> sprintf "the closures of %s changed in number, so they cannot be matched to the running ones" t
    | RudeCause.UnreadableBody (t, m, refusal) -> sprintf "%s.%s cannot be patched: %s" t m (IlRefusal.describe refusal)

  /// A stable token to branch on or grep for, one per case.
  let caseName (cause: RudeCause) : string =
    match cause with
    | RudeCause.TypeRemoved _ -> "TypeRemoved"
    | RudeCause.TypeAdded _ -> "TypeAdded"
    | RudeCause.FieldsChanged _ -> "FieldsChanged"
    | RudeCause.TypeShapeChanged _ -> "TypeShapeChanged"
    | RudeCause.PropertiesOrEventsChanged _ -> "PropertiesOrEventsChanged"
    | RudeCause.AttributesChanged _ -> "AttributesChanged"
    | RudeCause.MethodRemoved _ -> "MethodRemoved"
    | RudeCause.SignatureChanged _ -> "SignatureChanged"
    | RudeCause.VirtualSignatureChanged _ -> "VirtualSignatureChanged"
    | RudeCause.MethodFlagsChanged _ -> "MethodFlagsChanged"
    | RudeCause.ParametersChanged _ -> "ParametersChanged"
    | RudeCause.AddedMethodUnsupported _ -> "AddedMethodUnsupported"
    | RudeCause.StaticInitializerChanged _ -> "StaticInitializerChanged"
    | RudeCause.ClosureMatchAmbiguous _ -> "ClosureMatchAmbiguous"
    | RudeCause.UnreadableBody _ -> "UnreadableBody"
