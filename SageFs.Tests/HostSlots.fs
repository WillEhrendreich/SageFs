/// How many fixture hosts one test process runs at once. (The stub: HostConcurrencyTests is red against it.)
module SageFs.Tests.HostSlots

open System.Threading.Tasks

type Slots = { Limit: int }

let create (limit: int) : Slots = { Limit = limit }

let run (_slots: Slots) (_body: unit -> Task<'a>) : Task<'a> = failwith "HostSlots.run: not built yet"

let shared : Slots = { Limit = 0 }
