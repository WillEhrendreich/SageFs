// ============================================================
//  🧘  About Classes — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutClasses.fs
//  Adapted from FSharpKoans by Chris Marinos (MIT). See LICENSE-FSharpKoans.
//
//  F# is a fully object-oriented language too.
//  Classes work like you'd expect from C# or Java.
//  But in F#, you usually reach for records and DUs first —
//  classes shine for encapsulation and .NET interop.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto
open Expecto.Flip

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── Defining classes ─────────────────────────────────────────

type Zombie() =
  member this.FavoriteFood = "brains"
  member this.Eat food =
    match food with
    | "brains" -> "mmmmmmmmmmmmmmm"
    | _        -> "grrrrrrrr"

type Person(name: string) =
  member this.Speak() = "Hi my name is " + name

// Private let bindings — not accessible from outside:
type Zombie2() =
  let favoriteFood = "brains"    // private!
  member this.Eat food =
    if food = favoriteFood then "mmmmmmmmmmmmmmm" else "grrrrrrrr"

// Read/write properties:
type Person2(name: string) =
  let mutable internalName = name
  member this.Name
    with get ()          = internalName
    and  set (value)     = internalName <- value
  member this.Speak() = "Hi my name is " + this.Name

// ── Creating and using instances ─────────────────────────────

let zombie  = Zombie()
zombie.FavoriteFood          // → "brains"
zombie.Eat "brains"          // → "mmmmmmmmmmmmmmm"
zombie.Eat "chicken"         // → "grrrrrrrr"

let shaun   = Person("Shaun")
shaun.Speak()                // → "Hi my name is Shaun"

let zombie2 = Zombie2()
zombie2.Eat "chicken"        // → "grrrrrrrr"
// zombie2.favoriteFood      // ← compiler error — it's private!

let shaun2 = Person2("Shaun")
shaun2.Speak()               // → "Hi my name is Shaun"
shaun2.Name <- "Shaun of the Dead"
shaun2.Speak()               // → "Hi my name is Shaun of the Dead"

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about classes" [

  test "ClassesCanHaveProperties" {
    zombie.FavoriteFood |> Expect.equal "zombies like brains" __
  }

  test "ClassesCanHaveMethods — matching food" {
    (zombie.Eat "brains") |> Expect.equal "zombie loves brains" __
  }

  test "ClassesCanHaveMethods — non-matching food" {
    (zombie.Eat "chicken") |> Expect.equal "zombie rejects chicken" __
  }

  test "ClassesCanHaveConstructors" {
    (shaun.Speak()) |> Expect.equal "person introduces themselves" __
  }

  test "ClassesCanHaveLetBindings" {
    // Zombie2.favoriteFood is private — accessible only inside the class
    (zombie2.Eat "chicken") |> Expect.equal "zombie2 still rejects chicken" __
  }

  test "ClassesCanHaveReadWriteProperties — before rename" {
    let p = Person2("Shaun")
    (p.Speak()) |> Expect.equal "initial name" __
  }

  test "ClassesCanHaveReadWriteProperties — after rename" {
    let p = Person2("Shaun")
    p.Name <- "Shaun of the Dead"
    (p.Speak()) |> Expect.equal "after mutation" __
  }

]

// ── Record vs Class ───────────────────────────────────────────
// Use Record when:   data-oriented, immutable, structural equality needed
// Use Class when:    encapsulation needed, mutable state, .NET interop
//
// In practice: reach for record/DU first, class only when needed.
//
// 💡 SageFs convention: Immutability by default.
//    Notice `Person2` uses `mutable` and `<-` assignment. This works,
//    but in idiomatic F# (and all SageFs code), we prefer:
//
//      type Person = { Name: string }
//      let rename newName person = { person with Name = newName }
//
//    The `with` syntax creates a NEW record — the old one never changes.
//    No mutation, no surprises, no bugs from shared mutable state.
//    Classes with `mutable` are fine for .NET interop, but records are
//    the default choice in F# for good reason!
// ============================================================
