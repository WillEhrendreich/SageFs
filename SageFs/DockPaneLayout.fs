/// The two dock panes' sizes and placements. Kept out of `DashboardTypes` so the view-model module does
/// not grow past its budget, and grouped here because one choice drives four renderers: the page shell
/// declares the signals, the main area exposes them to CSS, the splitter writes the share, and the app
/// output pane toggles where it sits. All three are browser-local settings, persisted in localStorage.
module SageFs.Server.DockPaneLayout

/// Where the app-output pane lives: `stacked` under the bindings, or `side` beside the transcript.
let [<Literal>] PaneLayoutSignal = "paneLayout"
/// The live-bindings pane's share of the dock, as a CSS length. The splitter writes this; the other pane takes the rest.
/// The default fits the compact chrome plus the tree's sticky column header and three binding rows at the journey's 720px
/// viewport (140px of tree); 11rem left the tree 110px, which is two rows once the header and the row heights are counted.
let [<Literal>] PaneSplit = "paneSplit"
/// True while the splitter between the two panes is being dragged.
let [<Literal>] PaneResizing = "paneResizing"
/// Splitter between the live-bindings and app-output panes.
let [<Literal>] PaneResize = "pane-resize"
/// Toggle on the app-output pane between stacked-bottom and beside-the-transcript.
let [<Literal>] PaneLayoutToggle = "pane-layout-toggle"
