module SageFs.Samples.Counter.Program

#nowarn "3391"

open Raylib_cs
open System

type CounterTheme =
  { Background: Color
    Panel: Color
    Accent: Color
    Text: Color
    Muted: Color
    Positive: Color
    Negative: Color }

type CounterState =
  { Value: int
    Pulse: float32
    ThemeIndex: int
    LastChange: int
    LastInteraction: string }

type CounterAction =
  | Increment
  | Decrement
  | Reset
  | CycleTheme
  | Tick of float32

type CounterButton =
  { Label: string
    Hint: string
    Bounds: Rectangle
    Action: CounterAction
    Fill: Color
    Border: Color }

let screenWidth = 960
let screenHeight = 640

let themes =
  [|
    { Background = Color(242uy, 244uy, 248uy, 255uy)
      Panel = Color(255uy, 255uy, 255uy, 255uy)
      Accent = Color(61uy, 104uy, 232uy, 255uy)
      Text = Color(30uy, 34uy, 45uy, 255uy)
      Muted = Color(110uy, 118uy, 140uy, 255uy)
      Positive = Color(53uy, 179uy, 126uy, 255uy)
      Negative = Color(219uy, 79uy, 79uy, 255uy) }
    { Background = Color(16uy, 19uy, 28uy, 255uy)
      Panel = Color(29uy, 34uy, 49uy, 255uy)
      Accent = Color(118uy, 201uy, 255uy, 255uy)
      Text = Color(232uy, 239uy, 248uy, 255uy)
      Muted = Color(148uy, 161uy, 184uy, 255uy)
      Positive = Color(103uy, 235uy, 159uy, 255uy)
      Negative = Color(255uy, 138uy, 128uy, 255uy) }
    { Background = Color(250uy, 238uy, 224uy, 255uy)
      Panel = Color(255uy, 249uy, 240uy, 255uy)
      Accent = Color(208uy, 122uy, 72uy, 255uy)
      Text = Color(74uy, 48uy, 31uy, 255uy)
      Muted = Color(144uy, 108uy, 88uy, 255uy)
      Positive = Color(72uy, 148uy, 99uy, 255uy)
      Negative = Color(190uy, 86uy, 70uy, 255uy) }
  |]

let clampCounter value =
  value |> max -9 |> min 99

let currentTheme state =
  themes.[state.ThemeIndex % themes.Length]

let cardBounds () =
  Rectangle(180.0f, 110.0f, 600.0f, 420.0f)

let buttonWidth = 124.0f
let buttonHeight = 60.0f
let buttonGap = 18.0f

let inline isKeyPressed key : bool =
  Raylib.IsKeyPressed key

let inline isMousePressed button : bool =
  Raylib.IsMouseButtonPressed button

let pointInRect (pointX: float32) (pointY: float32) (rect: Rectangle) =
  pointX >= rect.X
  && pointX <= rect.X + rect.Width
  && pointY >= rect.Y
  && pointY <= rect.Y + rect.Height

let buttonFill theme action =
  match action with
  | Increment -> theme.Positive
  | Decrement -> theme.Negative
  | Reset -> theme.Accent
  | CycleTheme -> theme.Muted
  | Tick _ -> theme.Panel

let buttonLayout state =
  let theme = currentTheme state
  let card = cardBounds ()
  let startX =
    card.X + (card.Width - (buttonWidth * 4.0f + buttonGap * 3.0f)) / 2.0f
  let y = card.Y + card.Height - 110.0f
  [
    ("-1", "Left / Down", Decrement)
    ("Reset", "Space", Reset)
    ("Theme", "T", CycleTheme)
    ("+1", "Right / Up", Increment)
  ]
  |> List.mapi (fun index (label, hint, action) ->
    let x = startX + float32 index * (buttonWidth + buttonGap)
    let fill = buttonFill theme action
    let border =
      match action with
      | CycleTheme -> theme.Text
      | _ -> Color.White
    { Label = label
      Hint = hint
      Bounds = Rectangle(x, y, buttonWidth, buttonHeight)
      Action = action
      Fill = fill
      Border = border })

let keyboardAction () =
  [
    KeyboardKey.Right, Increment
    KeyboardKey.Up, Increment
    KeyboardKey.Left, Decrement
    KeyboardKey.Down, Decrement
    KeyboardKey.Space, Reset
    KeyboardKey.T, CycleTheme
  ]
  |> List.tryPick (fun (key, action) ->
    match isKeyPressed key with
    | true -> Some action
    | false -> None)

let hoveredAction state =
  let mouse = Raylib.GetMousePosition()
  buttonLayout state
  |> List.tryPick (fun button ->
    match pointInRect mouse.X mouse.Y button.Bounds with
    | true -> Some button.Action
    | false -> None)

let mouseAction state =
  match isMousePressed MouseButton.Left with
  | true -> hoveredAction state
  | false -> None

let inputAction state =
  match keyboardAction () with
  | Some action -> Some action
  | None -> mouseAction state

let decayPulse dt pulse =
  pulse - dt * 2.6f |> max 0.0f

let stepCounter delta state =
  let nextValue = clampCounter (state.Value + delta)
  { state with
      Value = nextValue
      Pulse = 1.0f
      LastChange = delta
      LastInteraction = sprintf "%+d" delta }

let cycleTheme state =
  { state with
      ThemeIndex = (state.ThemeIndex + 1) % themes.Length
      Pulse = 0.65f
      LastInteraction = "theme" }

let applyAction state action =
  match action with
  | Increment -> stepCounter 1 state
  | Decrement -> stepCounter -1 state
  | Reset ->
      { state with
          Value = 0
          Pulse = 0.9f
          LastChange = 0
          LastInteraction = "reset" }
  | CycleTheme -> cycleTheme state
  | Tick dt ->
      { state with Pulse = decayPulse dt state.Pulse }

let valueColor state =
  let theme = currentTheme state
  match compare state.LastChange 0 with
  | 1 -> theme.Positive
  | -1 -> theme.Negative
  | _ -> theme.Text

let pulseRadius state =
  78.0f + state.Pulse * 18.0f

let pulseAlpha state =
  byte (40.0f + state.Pulse * 120.0f)

let drawButton state button =
  let mouse = Raylib.GetMousePosition()
  let hovered = pointInRect mouse.X mouse.Y button.Bounds
  let fill =
    match hovered with
    | true -> Color(
        min 255 (int button.Fill.R + 16) |> byte,
        min 255 (int button.Fill.G + 16) |> byte,
        min 255 (int button.Fill.B + 16) |> byte,
        255uy)
    | false -> button.Fill
  let labelSize = 26
  let hintSize = 14
  let labelWidth = Raylib.MeasureText(button.Label, labelSize)
  let hintWidth = Raylib.MeasureText(button.Hint, hintSize)
  let labelX = int button.Bounds.X + (int button.Bounds.Width - labelWidth) / 2
  let hintX = int button.Bounds.X + (int button.Bounds.Width - hintWidth) / 2
  let theme = currentTheme state

  Raylib.DrawRectangleRounded(button.Bounds, 0.26f, 10, fill)
  Raylib.DrawRectangleRoundedLinesEx(button.Bounds, 0.26f, 10, 2.2f, button.Border)
  Raylib.DrawText(button.Label, labelX, int button.Bounds.Y + 10, labelSize, Color.White)
  Raylib.DrawText(button.Hint, hintX, int button.Bounds.Y + 38, hintSize, theme.Panel)

let drawCounterCard state =
  let theme = currentTheme state
  let card = cardBounds ()
  let centerX = int (card.X + card.Width / 2.0f)
  let counterY = int (card.Y + 158.0f)
  let valueText = string state.Value
  let valueSize = 110
  let subtitle = "Tiny single-file Raylib counter for Code City showcase"
  let subtitleX = centerX - Raylib.MeasureText(subtitle, 20) / 2
  let valueX = centerX - Raylib.MeasureText(valueText, valueSize) / 2
  let title = "Counter Studio"
  let titleX = centerX - Raylib.MeasureText(title, 34) / 2
  let interactionText = $"last input: {state.LastInteraction}"
  let interactionX = centerX - Raylib.MeasureText(interactionText, 18) / 2
  let ringColor = Color(theme.Accent.R, theme.Accent.G, theme.Accent.B, pulseAlpha state)

  Raylib.DrawRectangleRounded(card, 0.08f, 10, theme.Panel)
  Raylib.DrawRectangleRoundedLinesEx(card, 0.08f, 10, 3.0f, Color(theme.Accent.R, theme.Accent.G, theme.Accent.B, 110uy))
  Raylib.DrawText(title, titleX, int card.Y + 34, 34, theme.Text)
  Raylib.DrawText(subtitle, subtitleX, int card.Y + 74, 20, theme.Muted)
  Raylib.DrawCircleLines(centerX, counterY + 18, pulseRadius state, ringColor)
  Raylib.DrawText(valueText, valueX, int card.Y + 120, valueSize, valueColor state)
  Raylib.DrawText(interactionText, interactionX, int card.Y + 264, 18, theme.Muted)

let drawHints state =
  let theme = currentTheme state
  let footer = "Click buttons or use arrow keys, space, and T. Save the file and tweak the rules."
  let footerX = (screenWidth - Raylib.MeasureText(footer, 18)) / 2
  let badge = $"theme {state.ThemeIndex + 1} / {themes.Length}"
  Raylib.DrawText(footer, footerX, screenHeight - 54, 18, theme.Muted)
  Raylib.DrawText(badge, 24, 20, 20, theme.Accent)
  Raylib.DrawFPS(screenWidth - 100, 20)

let drawFrame state =
  let theme = currentTheme state
  Raylib.ClearBackground(theme.Background)
  drawCounterCard state
  buttonLayout state |> List.iter (drawButton state)
  drawHints state

let initState () =
  { Value = 0
    Pulse = 0.0f
    ThemeIndex = 0
    LastChange = 0
    LastInteraction = "boot" }

[<EntryPoint>]
let main _argv =
  Raylib.InitWindow(screenWidth, screenHeight, "Counter Studio — SageFs sample")
  Raylib.SetTargetFPS(60)

  let mutable state = initState ()

  while not (Raylib.WindowShouldClose()) do
    let dt = Raylib.GetFrameTime()
    state <- applyAction state (Tick dt)
    state <-
      match inputAction state with
      | Some action -> applyAction state action
      | None -> state

    Raylib.BeginDrawing()
    drawFrame state
    Raylib.EndDrawing()

  Raylib.CloseWindow()
  0
