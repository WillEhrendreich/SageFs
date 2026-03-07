module SageFs.Samples.Koans.AboutClasses

open Expecto
open Expecto.Flip

type Zombie() =
  member this.FavoriteFood = "brains"
  member this.Eat food =
    match food with
    | "brains" -> "mmmmmmmmmmmmmmm"
    | _ -> "grrrrrrrr"

type Person(name: string) =
  member this.Speak() = "Hi my name is " + name

type Zombie2() =
  let favoriteFood = "brains"
  member this.Eat food =
    if food = favoriteFood then "mmmmmmmmmmmmmmm" else "grrrrrrrr"

type Person2(name: string) =
  let mutable internalName = name
  member this.Name
    with get () = internalName
    and set (value) = internalName <- value
  member this.Speak() = "Hi my name is " + this.Name

let zombie = Zombie()
let shaun = Person("Shaun")
let zombie2 = Zombie2()

let tests = testList "about classes" [

  test "ClassesCanHaveProperties" {
    zombie.FavoriteFood |> Expect.equal "zombies like brains" "brains"
  }

  test "ClassesCanHaveMethods — matching food" {
    (zombie.Eat "brains") |> Expect.equal "zombie loves brains" "mmmmmmmmmmmmmmm"
  }

  test "ClassesCanHaveMethods — non-matching food" {
    (zombie.Eat "chicken") |> Expect.equal "zombie rejects chicken" "grrrrrrrr"
  }

  test "ClassesCanHaveConstructors" {
    (shaun.Speak()) |> Expect.equal "person introduces themselves" "Hi my name is Shaun"
  }

  test "ClassesCanHaveLetBindings" {
    (zombie2.Eat "chicken") |> Expect.equal "zombie2 still rejects chicken" "grrrrrrrr"
  }

  test "ClassesCanHaveReadWriteProperties — before rename" {
    let p = Person2("Shaun")
    (p.Speak()) |> Expect.equal "initial name" "Hi my name is Shaun"
  }

  test "ClassesCanHaveReadWriteProperties — after rename" {
    let p = Person2("Shaun")
    p.Name <- "Shaun of the Dead"
    (p.Speak()) |> Expect.equal "after mutation" "Hi my name is Shaun of the Dead"
  }

]
