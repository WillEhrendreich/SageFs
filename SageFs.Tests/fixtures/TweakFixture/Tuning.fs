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

// What the program changes while it runs: a binding declared `let mutable`, and a record field declared `mutable`. The states
// journey assigns them from the REPL, the way the running program would, to see the pane tell its change from a tweak.
let mutable lives = 3

type Player = { Speed: float; mutable Health: int }

let player = { Speed = 4.5; Health = 100 }
