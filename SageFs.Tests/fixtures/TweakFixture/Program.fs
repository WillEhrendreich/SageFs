module Game.Program

[<EntryPoint>]
let main _ =
  printfn "gravity %g, max health %d, jump %g" Game.Tuning.gravity Game.Tuning.maxHealth Game.Tuning.jump
  0
