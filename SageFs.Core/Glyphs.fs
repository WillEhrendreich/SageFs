/// The icons the dashboard and the session output draw, as `Glyphs.text Glyph.Warn`. Every one is a monochrome text glyph, so it
/// takes the colour of the text around it and follows the reader's font size, and none is a colour emoji, which would draw in its
/// own colours at its own size and ignore the theme. The set is closed: a place that needs an icon names one of these, and
/// `DashboardGlyphTests` fails on any emoji left in the sources that draw the page.
[<AutoOpen>]
module SageFs.Glyphs

/// What an icon says. Two meanings never share a glyph, so a reader who learns one has learned it everywhere.
[<RequireQualifiedAccess>]
type Glyph =
  /// Worked, passed, healthy.
  | Pass
  /// Failed, broken, an error.
  | Fail
  /// Caution: it works, with something to read.
  | Warn
  /// A loud alert that wants attention now.
  | Alarm
  /// Working on it: warming up, evaluating.
  | Busy
  /// Stopped, idle.
  | Stopped
  /// Never ran, so there is no result to show.
  | NotRun
  /// Takes effect now.
  | Live
  /// Remove, close, delete.
  | Remove
  /// A folder or a directory.
  | Folder
  /// A file or a document.
  | File
  /// A package, an assembly, a set of sessions.
  | Package
  /// Search or discover.
  | Search
  /// Add or create.
  | Add
  /// A list to pick from.
  | List
  /// Settings.
  | Settings
  /// A span of time.
  | Time
  /// Tests.
  | Test
  /// People or members.
  | Members
  /// Something the daemon is watching.
  | Watched
  /// Reload.
  | Reload
  /// Something that is not there any more.
  | Phantom
  /// Tidy up.
  | Tidy
  /// Numbers, the perf stats.
  | Chart
  /// Go: start, open, run.
  | Go
  /// Opens somewhere else: a link out.
  | Open
  /// Skip ahead.
  | Skip
  /// The product's mark, in the header and the tab.
  | Logo

/// The one exhaustive spelling of each glyph. Each is a character with no emoji presentation.
let text (glyph: Glyph) : string =
  match glyph with
  | Glyph.Pass -> "✓"
  | Glyph.Fail -> "✗"
  | Glyph.Warn -> "▲"
  | Glyph.Alarm -> "◆"
  | Glyph.Busy -> "◐"
  | Glyph.Stopped -> "■"
  | Glyph.NotRun -> "□"
  | Glyph.Live -> "●"
  | Glyph.Remove -> "✕"
  | Glyph.Folder -> "▤"
  | Glyph.File -> "▯"
  | Glyph.Package -> "◈"
  | Glyph.Search -> "⌕"
  | Glyph.Add -> "+"
  | Glyph.List -> "☰"
  | Glyph.Settings -> "⛭"
  | Glyph.Time -> "◷"
  | Glyph.Test -> "◇"
  | Glyph.Members -> "◎"
  | Glyph.Watched -> "◉"
  | Glyph.Reload -> "↻"
  | Glyph.Phantom -> "◌"
  | Glyph.Tidy -> "∅"
  | Glyph.Chart -> "▥"
  | Glyph.Go -> "▶"
  | Glyph.Open -> "↗"
  | Glyph.Skip -> "»"
  | Glyph.Logo -> "λ"

/// The glyph and one space, for a label that starts with it.
let lead (glyph: Glyph) : string = text glyph + " "

/// Every glyph, for the tests that walk the whole set.
let all : Glyph list =
  [ Glyph.Pass; Glyph.Fail; Glyph.Warn; Glyph.Alarm; Glyph.Busy; Glyph.Stopped; Glyph.NotRun; Glyph.Live; Glyph.Remove; Glyph.Folder; Glyph.File; Glyph.Package
    Glyph.Search; Glyph.Add; Glyph.List; Glyph.Settings; Glyph.Time; Glyph.Test; Glyph.Members; Glyph.Watched; Glyph.Reload; Glyph.Phantom
    Glyph.Tidy; Glyph.Chart; Glyph.Go; Glyph.Open; Glyph.Skip; Glyph.Logo ]
