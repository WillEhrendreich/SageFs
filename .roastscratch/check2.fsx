let delays (budgetMs: int) (firstDelayMs: int) : int list =
  if budgetMs <= 0 || firstDelayMs <= 0 then []
  else
    let rec grow acc next remaining =
      if next > remaining then
        List.rev (if remaining > 0 then remaining :: acc else acc)
      else
        grow (next :: acc) (next * 2) (remaining - next)
    grow [] firstDelayMs budgetMs
let d = delays 15000 50
printfn "delays = %A" d
printfn "sum = %d" (List.sum d)
printfn "pairs that do not grow: %A" (d |> List.pairwise |> List.filter (fun (a,b) -> a >= b))
