module Game.Tuning

let gravity = 9.8
let maxHealth = 100
let hardMode = false
let title = "Nudge"
let jump = gravity * 2.0

type Feel = { JumpVelocity: float; CoyoteTime: float }

let feel = { JumpVelocity = 13.2; CoyoteTime = 0.12 }

type Rig = { Mass: float; Drag: float }

let makeRig () = { Mass = 80.0; Drag = 0.25 }

let rig = makeRig ()
let spareRig = makeRig ()
