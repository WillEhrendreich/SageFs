module Game.Tuning

let gravity = 9.8
let maxHealth = 100
let hardMode = false
let title = "Nudge"
let jump = gravity * 2.0

type Feel = { JumpVelocity: float; CoyoteTime: float }

let feel = { JumpVelocity = 13.2; CoyoteTime = 0.12 }
