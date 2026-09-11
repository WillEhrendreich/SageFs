/// A compiled module that stable-identity reload tests patch in a real FSI
/// session. Its root name is unique so the emitted FSI module shadows nothing else.
module StableIdentityProbe.Fixture

type Item = { Id: int }

let mutable items: Item list = [ { Id = 1 } ]

let count (xs: Item list) =
  xs.Length
