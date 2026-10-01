/// A virtual member. An edit that changes its signature changes what every override has to be, which a patch cannot do to a
/// running process, so landing that edit has to restart the app.
module CohortTrunkFixture.Rude

[<AbstractClass>]
type Shape() =
  abstract Name: unit -> string

type Square() =
  inherit Shape()
  override _.Name() : string = "rude:v1"

let shape : Shape = Square()

let rudeMessage () : string = shape.Name()
