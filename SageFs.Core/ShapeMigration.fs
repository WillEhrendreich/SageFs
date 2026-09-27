module SageFs.ShapeMigration

/// WHY — I asserted, and wrote into the docs, that a type change could not be
/// migrated cheaply: a `Migrate` plan skips the build, so "the new type has no
/// compiled form and there is nothing to construct". **That premise was wrong,
/// and testing it is what proved it.**
///
/// A patched type is compiled by the FSI session at RUNTIME, so it is a real
/// `System.Type` the moment the patch lands. Proven in a live session, end to
/// end, with no build anywhere:
///
///     PROBE ctor count = 1
///     PROBE ctor arg counts = [|3|]      <- one arg PER FIELD
///     PROBE backing fields = [||]        <- no backing fields to write
///     MIGRATE new field order = [|"Id"; "Name"; "Note"|]
///     MIGRATE the migrated value = { Id = 42; Name = "widget"; Note = "added by the edit" }
///
/// Three facts the implementation depends on, each measured rather than assumed:
///   1. the constructor takes ONE ARGUMENT PER FIELD, in DECLARATION order;
///   2. the constructor's parameters are NOT named, so matching is by POSITION;
///   3. there are no backing fields, so a setter-based copy is impossible and
///      construction is the only route.
///
/// The design consequence: a migration is a CONSTRUCTOR CALL with values lined
/// up by name from the old shape. That is a different piece of work from a
/// setter-based rewrite, and it is why the premise mattered — the wrong premise
/// would have produced an executor written against an API that does not exist.

open System
open System.Reflection
open SageFs.TypeShapeMigration

/// What migrating one value can come to.
///
/// NOT generic. The caller wants "some value of the new type" and will unbox it
/// where it knows the type; making the case generic only makes every pattern
/// harder to write, because `RequireQualifiedAccess` forbids naming the type
/// argument in a pattern. `Migrated.Carried v` is all a caller should have to
/// write.
///
/// `NoConstructor` and `TypeMismatch` are separate because the fix is different:
/// the first means the type is not a record this can build, the second means a
/// field's kind changed and the old value is not a valid argument for it.
[<RequireQualifiedAccess>]
type Migrated =
  /// Built and filled. The value is a real instance of the NEW type.
  | Carried of value: obj
  /// The target has no one-argument-per-field constructor, so there is no
  /// constructor call to make. Never a silent default.
  | NoConstructor of because: string
  /// A field exists in both shapes but its KIND changed, so the old value cannot
  /// be passed as the new parameter. The name is carried so the user learns
  /// which field broke it.
  | TypeMismatch of field: string * was: string * now: string

/// The kind a `System.Type` is for migration purposes.
///
/// Deliberately narrow. `Undecidable` is a real answer — an int and a string
/// are carryable, a `Map` is not — and guessing would put a wrongly-typed value
/// into a live object, which is the one outcome this whole design refuses.
let private kindOf (t: Type) : FieldKind option =
  if t = typeof<int> || t = typeof<int64> || t = typeof<uint32> || t = typeof<int32> then
    Some FieldKind.IntField
  elif t = typeof<string> || t = typeof<char> then Some FieldKind.StringField
  elif t = typeof<bool> then Some FieldKind.BoolField
  else None

/// Migrate ONE live value from `oldValue`'s type into `newType`.
///
/// Both types must be RECORDS. A class, a union or an interface has no
/// one-argument-per-field constructor, and there is no honest way to build one
/// without a compile step — so it is refused by name rather than defaulted.
///
/// Fields are matched BY NAME against the old value:
///   - in both, same kind -> the old value's argument carries across;
///   - only in the new   -> `defaultArg` supplies it, which is the same claim
///     `decideField` already makes about a defaulted field;
///   - only in the old   -> dropped, because the new shape has nowhere to put it;
///   - in both, different kind -> `TypeMismatch`, and nothing is built.
///
/// That is `decideField`'s vocabulary applied to real `System.Type`s, so the
/// domain decision and the executor cannot disagree about what is carryable.
let migrate (oldValue: obj) (newType: Type) : Migrated =
  let oldType = oldValue.GetType()
  if not (Microsoft.FSharp.Reflection.FSharpType.IsRecord newType) then
    Migrated.NoConstructor(sprintf "'%s' is not a record, so it has no field constructor" newType.Name)
  elif not (Microsoft.FSharp.Reflection.FSharpType.IsRecord oldType) then
    Migrated.NoConstructor(sprintf "'%s' is not a record, so there are no fields to carry" oldType.Name)
  else
    let newFields = Microsoft.FSharp.Reflection.FSharpType.GetRecordFields newType
    let oldIndex =
      Microsoft.FSharp.Reflection.FSharpType.GetRecordFields oldType
      |> Array.map (fun f -> f.Name, f.PropertyType)
      |> Map.ofArray
    // A field with no old value takes the shape's OWN default for that type —
    // the same claim `decideField` makes about a defaulted field, made against a
    // real `System.Type` rather than a name.
    //
    // The subtlety that cost a round: an F# `Map` is NOT default-constructible.
    // Measured:
    //   MAPFAIL Map has ctors with arg counts = [|1|]
    //   MAPFAIL Activator.CreateInstance(Map) = "No parameterless constructor"
    //
    // so the previous `| None -> null` fallback quietly put a NULL in a live
    // field and called it a migration — precisely the silently-wrong value this
    // design refuses to produce. There is no honest default here, so it is a
    // REFUSAL, and the refusal carries the type that could not be defaulted.
    let mutable undefaultable: string option = None

    let defaultOf (t: Type) : obj =
      if t.IsValueType then Activator.CreateInstance t
      elif t = typeof<string> then box ""
      else
        let emptyCtor =
          t.GetConstructors()
          |> Array.tryFind (fun (c: ConstructorInfo) -> c.GetParameters().Length = 0)
        match emptyCtor with
        | Some c -> c.Invoke(Array.empty)
        | None ->
          undefaultable <- Some t.Name
          null

    // The constructor takes one argument PER FIELD, in DECLARATION order, and
    // its parameters are named lower-cased (`id`, `name`) while the FIELDS are
    // Pascal-cased (`Id`, `Name`). Both facts are measured, and both matter:
    //
    //   PARAM ctor parameters = [|"id:Int32"; "name:String"; "note:String"|]
    //   PARAM field names      = [|"Id"; "Name"; "Note"|]
    //
    // So the match is ordinal-case-INsensitive on name, and POSITION is the
    // fallback when a parameter matches no field at all. Position alone was what
    // this function first did, and it broke on `RecordShape` — whose constructor
    // order genuinely differs from its field order — so neither signal is
    // sufficient alone. Name first, position only when name cannot help, and a
    // kind change still refuses either way.
    let fieldNamed (n: string) =
      newFields
      |> Array.tryFind (fun f -> String.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase))

    match newType.GetConstructors() |> Array.tryFind (fun c -> c.GetParameters().Length = newFields.Length) with
    | None ->
      Migrated.NoConstructor(sprintf "'%s' has no constructor taking one argument per field" newType.Name)
    | Some ctor ->
      let ctorParams = ctor.GetParameters()
      let mutable refusal: (string * string * string) option = None
      let args =
        ctorParams
        |> Array.mapi (fun i (p: ParameterInfo) ->
          // By name when the name helps; otherwise the field at the same
          // POSITION, which is the only remaining signal.
          let byPosition () =
            if i < newFields.Length then Some newFields.[i] else None
          let f =
            match fieldNamed p.Name with
            | Some found -> Some found
            | None -> byPosition ()
          match f with
          | Some f ->
            match Map.tryFind f.Name oldIndex with
            | Some oldFieldType when kindOf oldFieldType <> kindOf f.PropertyType ->
              if refusal.IsNone then
                refusal <- Some(f.Name, oldFieldType.Name, f.PropertyType.Name)
              defaultOf f.PropertyType
            | Some _ -> oldValue.GetType().GetProperty(f.Name).GetValue(oldValue)
            | None -> defaultOf f.PropertyType
          | None -> defaultOf p.ParameterType)
      // `undefaultable` is checked BEFORE `refusal`, and both before the invoke:
      // a field whose type has no honest default can never be carried, whatever
      // the other fields decided. Constructing first and reporting afterwards
      // would put a null into a live object on the way to the same answer.
      match undefaultable with
      | Some t ->
        Migrated.NoConstructor(
          sprintf
            "a field of type '%s' has no default value, so it cannot be supplied for a field the edit added — give it a default in the new shape"
            t)
      | None ->
        match refusal with
        | Some(field, was, now) -> Migrated.TypeMismatch(field, was, now)
        | None ->
          // The catch is a DIAGNOSTIC, not a fallback: a throw here means the
          // arguments were wrong, and reporting it as "no constructor" hides the
          // only information that would fix it.
          try Migrated.Carried(ctor.Invoke(args))
          with ex ->
            // Report the arguments as well as the message, because which
            // ordering signal was used is only visible in the types given.
            let given =
              Array.mapi (fun i (a: obj) -> sprintf "#%d=%s" i (if isNull a then "null" else a.GetType().Name)) args
            let wanted : string array = Array.map (fun (p: ParameterInfo) -> p.ParameterType.Name) ctorParams
            Migrated.NoConstructor(
              sprintf "'%s' matched a %d-argument constructor but could not be invoked: %s | given [%s] wanted [%s]"
                newType.Name newFields.Length ex.Message (String.Join(", ", given)) (String.Join(", ", wanted)))

/// The `Boundary.Migrate` hook an app declares when migrating its holder across
/// a type change is just this.
///
/// It exists as a named value rather than something each app writes, for two
/// reasons. First, the decision it makes is the SAME one `migrate` makes, and an
/// app that answered "worth carrying" by hand could disagree with the executor
/// that has to build the value — and a disagreement there is a live object built
/// from a shape someone else refused. Second, it is what makes this reachable
/// from a running app: the hook is `obj -> System.Type -> MigrationWorth`,
/// exactly this signature, so `DeclareWithSubject` is the whole wiring.
let hook () : obj -> Type -> MigrationWorth =
  fun oldValue newType ->
    match migrate oldValue newType with
    | Migrated.Carried v ->
      MigrationWorth.WorthCarrying 1
    | Migrated.TypeMismatch(field, was, now) ->
      MigrationWorth.NoValueToMigrate(
        sprintf "'%s' changed from %s to %s, so the live value is not a valid argument for it" field was now)
    | Migrated.NoConstructor why -> MigrationWorth.NoValueToMigrate why

/// The `Boundary.Subject` hook: the live value, and the type it becomes.
///
/// `toNewType` is a FUNCTION, so the cell is read at DECISION time and the new
/// type is looked up at the same moment. A value captured at declaration time
/// would be a stale snapshot rather than the live state, and a type captured
/// then would be the type as it was before the patch.
///
/// An unresolved new type is a REFUSAL. The patch has not produced the type yet,
/// and inventing one would be a type this session never compiled.
let subjectFor (cell: Holder.Cell<'a>) (toNewType: unit -> Type) : unit -> (obj * Type) option =
  fun () ->
    let live = cell.Value.Value
    // `toNewType` is asked at the same moment. A type the patch has not produced
    // yet is not something to guess, so an empty lookup is a refusal.
    match toNewType () with null -> None | t -> Some(box live, t)
