#load "/home/will/Work/SageFs/SageFs/WorkerProxyWait.fs"
open SageFs
let d = WorkerProxyWait.delaysByDefault
printfn "attempts=%d totalMs=%d" d.Length (List.sum d)
printfn "delays=%A" d
