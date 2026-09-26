#load "/home/will/Work/SageFs/SageFs/WorkerProxyWait.fs"
open SageFs
let d = WorkerProxyWait.delaysByDefault
printfn "delays = %A" d
printfn "sum    = %d" (List.sum d)
// find the first pair that does not grow
let bad =
  d |> List.pairwise |> List.tryFind (fun (a,b) -> a >= b)
printfn "first non-growing pair: %A" bad
