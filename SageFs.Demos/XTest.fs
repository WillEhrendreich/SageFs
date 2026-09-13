/// The libXtst P/Invoke edge (demo-gif-plan.md §4.3, §4.11, §4.12): the ONLY
/// impure interpreter of the pure `X11Request` list `Input.plan` produces.
/// Never a hand-rolled X11 wire codec — this binds directly to the system
/// `libX11`/`libXtst` (the Phase-0 spike's correction to §2: both are present
/// on this machine, so this edge RO-binds and P/Invokes the system libraries
/// rather than an extracted per-RID bundle; bundling stays a Phase-4
/// portability concern, not a Phase-1 gate).
///
/// §4.11 discipline lives here concretely: every call site checks
/// `XOpenDisplay` succeeded before issuing any `XTest*` call (the `LiveDisplay`
/// type makes an unchecked handle unrepresentable — there is no public way to
/// construct one except through a successful `openDisplay`), and `XSync`s
/// after every delivered request so a real X protocol error surfaces
/// immediately rather than being silently queued. A segfault here is a real
/// bug to fix, never something to guard-and-ignore.
module SageFs.Demos.XTest

open System
open System.Runtime.InteropServices
open SageFs.Demos.Domain

module private Native =
  [<DllImport("libX11.so.6")>]
  extern nativeint XOpenDisplay(string display)

  [<DllImport("libX11.so.6")>]
  extern int XCloseDisplay(nativeint display)

  [<DllImport("libX11.so.6")>]
  extern int XFlush(nativeint display)

  [<DllImport("libX11.so.6")>]
  extern int XSync(nativeint display, bool discard)

  [<DllImport("libX11.so.6")>]
  extern int XDefaultScreen(nativeint display)

  [<DllImport("libXtst.so.6")>]
  extern int XTestFakeMotionEvent(nativeint display, int screen, int x, int y, int delay)

  [<DllImport("libXtst.so.6")>]
  extern int XTestFakeButtonEvent(nativeint display, uint32 button, [<MarshalAs(UnmanagedType.Bool)>] bool isPress, int delay)

  [<DllImport("libXtst.so.6")>]
  extern int XTestFakeKeyEvent(nativeint display, uint32 keycode, [<MarshalAs(UnmanagedType.Bool)>] bool isPress, int delay)

  [<DllImport("libX11.so.6")>]
  extern int XDisplayKeycodes(nativeint display, int& min_keycodes_return, int& max_keycodes_return)

  // `KeyCode` is Xlib's own `unsigned char` typedef — a raw byte, not `int`.
  [<DllImport("libX11.so.6")>]
  extern nativeint XGetKeyboardMapping(nativeint display, byte first_keycode, int keycode_count, int& keysyms_per_keycode_return)

  [<DllImport("libX11.so.6")>]
  extern int XFree(nativeint data)

/// A live connection to a cell's own Xvfb display, plus the default screen
/// index XTEST events are delivered against. Private constructor: the only
/// way to get one is `openDisplay` actually succeeding — an unchecked/null
/// handle is unrepresentable (roast §2's domain-modeling doctrine: a live
/// handle is proof of a live connection, never an assumed one).
type LiveDisplay = private LiveDisplay of handle: nativeint * screen: int

/// Opens `display` (e.g. ":99"). `None` if `XOpenDisplay` returns NULL
/// (`$DISPLAY` unset, Xvfb not up yet, or a socket permissions problem) — the
/// cell-agent turns this into a `Failed` StepLog entry, never a crash.
let openDisplay (display: Display) : LiveDisplay option =
  let (Display name) = display
  let handle = Native.XOpenDisplay(name)
  if handle = IntPtr.Zero then None
  else Some(LiveDisplay(handle, Native.XDefaultScreen(handle)))

let closeDisplay (live: LiveDisplay) : unit =
  let (LiveDisplay(handle, _)) = live
  Native.XCloseDisplay(handle) |> ignore

/// Fetches `live`'s REAL keysym→keycode table via `XGetKeyboardMapping` —
/// the fix for §9's root cause: a fictitious placeholder ("keysym ==
/// keycode") was used in its place, sending raw ASCII codepoints as literal
/// X11 keycodes and corrupting every non-trivial typed character against a
/// real display. `XGetKeyboardMapping` returns, for every keycode in
/// `[minKeycode..maxKeycode]`, an ordered list of `keysymsPerKeycode`
/// keysyms — one per shift "level" (index 0 = unshifted, index 1 = shifted;
/// higher levels, e.g. AltGr, are out of scope for the plain US-style Latin
/// typing this tool ever does). Level 0 keysyms populate `KeysymToKeycode`
/// unconditionally; a DIFFERENT level-1 keysym populates it too AND is
/// recorded in `ShiftedKeysyms` — the one piece of information
/// `Keymap.resolve` needs to decide whether to hold Shift, replacing the
/// old `Char.IsUpper`-only guess that could never see shifted punctuation.
/// The first keycode seen for a given keysym wins on a clash (some layouts
/// map a keysym to more than one physical key); `XFree`s the server-owned
/// buffer before returning, per Xlib's own contract for `XGetKeyboardMapping`.
let keyboardMapping (live: LiveDisplay) : KeyboardMapping =
  let (LiveDisplay(handle, _)) = live
  let mutable minKeycode = 0
  let mutable maxKeycode = 0
  Native.XDisplayKeycodes(handle, &minKeycode, &maxKeycode) |> ignore
  let keycodeCount = maxKeycode - minKeycode + 1
  let mutable keysymsPerKeycode = 0
  let buffer = Native.XGetKeyboardMapping(handle, byte minKeycode, keycodeCount, &keysymsPerKeycode)

  try
    let keysymAt (keycodeIndex: int) (level: int) : int option =
      if level >= keysymsPerKeycode then
        None
      else
        let offset = (keycodeIndex * keysymsPerKeycode + level) * IntPtr.Size
        let value = Marshal.ReadIntPtr(buffer, offset).ToInt64()
        if value = 0L then None else Some(int value)

    let mutable keysymToKeycode = Map.empty
    let mutable shiftedKeysyms = Set.empty

    for i in 0 .. keycodeCount - 1 do
      let keycode = minKeycode + i
      let unshifted = keysymAt i 0

      match unshifted with
      | Some ks when not (keysymToKeycode |> Map.containsKey ks) -> keysymToKeycode <- keysymToKeycode |> Map.add ks keycode
      | _ -> ()

      match keysymAt i 1 with
      | Some ks when Some ks <> unshifted ->
        if not (keysymToKeycode |> Map.containsKey ks) then
          keysymToKeycode <- keysymToKeycode |> Map.add ks keycode

        shiftedKeysyms <- shiftedKeysyms |> Set.add ks
      | _ -> ()

    { KeysymToKeycode = keysymToKeycode
      ShiftedKeysyms = shiftedKeysyms }
  finally
    Native.XFree(buffer) |> ignore

let private buttonNumber (button: Button) : uint32 =
  match button with
  | Button.Left -> 1u
  | Button.Middle -> 2u
  | Button.Right -> 3u

let private isPress (pressed: Pressed) : bool =
  match pressed with
  | Pressed.Down -> true
  | Pressed.Up -> false

/// Delivers one `X11Request` and immediately `XSync`s (§4.11: a real X
/// protocol error — e.g. an out-of-range keycode — must surface here, not be
/// queued silently until some later, unrelated call fails confusingly).
let deliverOne (live: LiveDisplay) (request: X11Request) : unit =
  let (LiveDisplay(handle, screen)) = live
  match request with
  | X11Request.FakeMotion(x, y) -> Native.XTestFakeMotionEvent(handle, screen, x, y, 0) |> ignore
  | X11Request.FakeButton(button, pressed) ->
    Native.XTestFakeButtonEvent(handle, buttonNumber button, isPress pressed, 0) |> ignore
  | X11Request.FakeKey(KeyCode keyCode, pressed) ->
    Native.XTestFakeKeyEvent(handle, uint32 keyCode, isPress pressed, 0) |> ignore
  Native.XFlush(handle) |> ignore
  Native.XSync(handle, false) |> ignore

/// Delivers a whole planned sequence (`Input.plan`'s output) with the same
/// pacing the Phase-0 spike's `clickAt` proved reads as human on camera: a
/// short settle between motion frames, a hold before releasing a button, a
/// brief gap between key-down and key-up.
let deliver (live: LiveDisplay) (requests: X11Request list) : unit =
  for request in requests do
    deliverOne live request
    match request with
    | X11Request.FakeMotion _ -> Threading.Thread.Sleep(16)
    | X11Request.FakeButton(_, Pressed.Down) -> Threading.Thread.Sleep(60)
    | X11Request.FakeButton(_, Pressed.Up) -> Threading.Thread.Sleep(40)
    | X11Request.FakeKey(_, Pressed.Down) -> Threading.Thread.Sleep(8)
    | X11Request.FakeKey(_, Pressed.Up) -> ()
