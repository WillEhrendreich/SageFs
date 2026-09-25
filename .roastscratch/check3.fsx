// The remainder is only worth a final step if it is LONGER than the last full
// delay. A short remainder is noise: emitting it is a backwards step, and
// dropping it wastes less than one doubling.
let delays (budgetMs: int) (firstDelayMs: int) : int list =
  if budgetMs <= 0 || firstDelayMs <= 0 then []
  else
    let rec grow acc next remaining =
      if next <= remaining then grow (next :: acc) (next * 2) (remaining - next)
      else
        // `next` would overrun. Use the remainder only if it still grows.
        match acc with
        | [] -> List.rev [ remaining ]
        | last :: _ when remaining > last -> List.rev (remaining :: acc)
        | _ -> List.rev acc
    grow [] firstDelayMs budgetMs

for budget in [1; 7; 50; 750; 5000; 15000] do
  let d = delays budget 50
  let bad = d |> List.pairwise |> List.filter (fun (a,b) -> a >= b)
  printfn "budget=%-6d sum=%-6d ok=%-5b delays=%A" budget (List.sum d) (bad.IsEmpty) d
