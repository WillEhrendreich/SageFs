// ============================================================
//  🧘  About Classes — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutClasses.fs
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
    Expect.equal zombie.FavoriteFood __ "zombies like brains"
  }

  test "ClassesCanHaveMethods — matching food" {
    Expect.equal (zombie.Eat "brains") __ "zombie loves brains"
  }

  test "ClassesCanHaveMethods — non-matching food" {
    Expect.equal (zombie.Eat "chicken") __ "zombie rejects chicken"
  }

  test "ClassesCanHaveConstructors" {
    Expect.equal (shaun.Speak()) __ "person introduces themselves"
  }

  test "ClassesCanHaveLetBindings" {
    // Zombie2.favoriteFood is private — accessible only inside the class
    Expect.equal (zombie2.Eat "chicken") __ "zombie2 still rejects chicken"
  }

  test "ClassesCanHaveReadWriteProperties — before rename" {
    let p = Person2("Shaun")
    Expect.equal (p.Speak()) __ "initial name"
  }

  test "ClassesCanHaveReadWriteProperties — after rename" {
    let p = Person2("Shaun")
    p.Name <- "Shaun of the Dead"
    Expect.equal (p.Speak()) __ "after mutation"
  }

]

// ── Record vs Class ───────────────────────────────────────────
// Use Record when:   data-oriented, immutable, structural equality needed
// Use Class when:    encapsulation needed, mutable state, .NET interop
//
// In practice: reach for record/DU first, class only when needed.
// ============================================================
