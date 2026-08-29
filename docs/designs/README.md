# SageFs Dashboard — Redesign Directions

Four full redesigns of the SageFs web dashboard. Each is a complete,
opinionated commitment to a different aesthetic and a different
relationship between the user and the work. Pick the one whose
commitment matches your own, and we'll iterate from there.

The screenshots above this document show each direction rendered at
1600×1000. The HTML files are in this directory; the live server is the
SageFs daemon itself.

## A note on light vs. dark (Aug 2026)

The first pass rendered all four directions in light palettes (paper,
bone, warm paper). After review the three light directions were
rejected: a working REPL lives in the dark, and the rest of SageFs is
dark, so the dashboard has to read as a dark surface even when the
aesthetic commitments are editorial or paper-like.

The first dark direction (Terminal Noir) was already dark and
remained unchanged. The other three were rebuilt as **dark
variants** that preserve the original structural commitment but
re-cast the palette in dark keys. The same layouts, the same
typography, the same signatures — just the right room for the work.

- **2 · Ink & Paper → Dark Editorial** — the same masthead and italic
  captions, but on a deep ink background with a dimmed copper accent.
- **3 · Architectural Grid → Dark Blueprint** — the same corner marks
  and § sections, but on charcoal with a single signal-green accent
  that glows the way traces glow on a CRT.
- **4 · Studio Oblique → Dark Studio** — the same hand-script
  labels and asymmetric layout, but on charcoal with a deep
  oxblood slab. Terracotta survives the dark pivot because it
  works against dark too.

Screenshots in this README and at the repo root show the dark
variants. The HTML files in this directory are the dark variants.

## What I found in the current code

Two things matter for the redesign:

1. The sidebar resize is **slow because of three compounding bugs** in
   `SageFs/Dashboard.fs:175-184`:
   - `document.documentElement.style.setProperty('--sidebar-width', w + 'px')`
     on **every** `mousemove` event (no rAF batching).
   - The CSS rule `.sidebar { transition: width 0.2s, padding 0.2s; }` in
     `dashboard.css:103` — every mouse-move spawns a new 200ms tween
     that gets immediately replaced, so the sidebar chases the cursor.
   - The CSS var lives on `<html>`, so *every* `mousemove` reflows the
     whole document, not just the sidebar.

   Fix: drop the transition (or set it to 0ms), batch via
   `requestAnimationFrame`, write to the sidebar's own `style.width`,
   not the document root.

2. The page's categorical job is unclear. Today it's a control panel
   (sessions sidebar, action buttons), a transcript (output stream), a
   notepad (eval textarea), and a status bar (health, memory, uptime)
   all at once, with no commitment to which is primary. The four
   redesigns each pick a primary thing.

## The four directions

### 1. Terminal Noir
**Palette**: `#0A0A0F` bg · `#14141F` panel · `#1E1E2E` raised · `#00D4AA`
mint accent · `#FF6B6B` error red · `#8BE9FD` blue · `#6B6B80` dim.

**Type**: JetBrains Mono everywhere. Display, body, utility — one
family. The terminal is the type signature.

**Layout**: Title bar with session tabs (one per REPL), a thin
health bar, a main split (output on the left, sessions sidebar on the
right), and a one-line status strip at the bottom. The output is the
primary mass; the eval input is a band along the bottom; sessions are
listed in the sidebar as compact rows.

**Signature**: a blinking block cursor in the eval input + an "awaiting
input" cursor in the status bar. The signature is the cursor; the
whole page reads as a terminal.

**Risk**: monospace everywhere. If you ever want a non-code string to
feel hand-set (a heading, a status), you don't get to. The terminal
aesthetic is total, not partial.

**File**: `01-terminal-noir.html` · `design-01-terminal-noir.png`

**Animation philosophy (resize)**: snappy. No transition on
`.sidebar` — direct width write on `mousemove` rAF-batched. Feels
like a real terminal window dragging.

---

### 2. Dark Editorial
**Palette**: `#0C0C10` bg · `#14141B` surface · `#E8E2D0` text · `#C8A951`
brass/copper accent · `#7A7466` dim · `#2A2A35` rule.

**Type**: Playfair Display 700 for display (headings, masthead).
Source Serif 4 for body. JetBrains Mono for code/output. Inter for
small utility text. Four faces, all doing distinct work — now on dark.

**Layout**: Editorial masthead at the top with a serif wordmark and a
small caps subtitle, a vertical hairline rule, then a two-column
layout: a wide left column for the output stream, and a narrower
right column for the "Session Ledger" — sessions rendered as
research-notebook rows separated by hairlines, not cards. § section
markers (§ I — Sessions, § II — Controls) in italic brass.

**Signature**: the brass rule under each session row, the italic
caption ("A session ledger, typeset on dark") that closes the
sidebar. The page reads as a midnight notebook, not a paper one.

**Risk**: the serif-on-dark aesthetic for a developer tool. On paper it
reads as a newspaper; on dark it reads as a midnight notebook. The
bet is that the people who choose SageFs over a Jupyter notebook
already value precision over decoration — and precision is what
editorial typography *is*, even at night.

**File**: `02-ink-paper.html` · `design-02-dark-editorial.png`

**Animation philosophy (resize)**: soft. A 120ms ease-out on the
sidebar's width. The sidebar feels like a page being revealed, not a
panel being dragged. The serif body asks for grace.

---

### 3. Dark Blueprint
**Palette**: `#0A0E12` bg · `#0E1318` surface · `#D6DEE5` text · `#7FD1A0`
signal-green accent · `#6AB7C8` cyan · `#D68A5C` warm · `#1F2A36` rule.
Hairline rules everywhere, now on a dark grid.

**Type**: Space Grotesk 600 for display (section heads, buttons).
JetBrains Mono for code/output and all data rows. Two faces, both
geometric.

**Layout**: Strict 8pt grid. Three-column top bar (brand, session
info, status), each column with its own hairline. Main area split into
three columns: left sidebar (Build/Layers/Telemetry with § A/B/C
marks), center output (§ numbered lines with a faint 80px grid behind
the transcript), right sidebar (§ 1–4 Sessions/Hot Reload/Live
Testing/Context). Corner marks on every panel — registration ticks
like a blueprint.

**Signature**: the § numbered sections + the corner marks on panels
+ the faint grid behind the transcript. Every region is labeled and
indexed; nothing is decoration. The page reads as a technical drawing
on a dark surface, the way traces glow on a CRT.

**Risk**: this is the "engineer's dashboard" reading. If your sessions
ever deserve warmth, this design has no place to put it. The numbered
sections also imply a sequence — which is honest (you do go Output →
Evaluate → Result, in that order) but it could read as rigid.

**File**: `03-architectural-grid.html` · `design-03-dark-blueprint.png`

**Animation philosophy (resize)**: binary. No transition. The sidebar
is either at width 0 or at width N. Snap to the new size with
zero animation. Matches the engineering aesthetic.

---

### 4. Dark Studio
**Palette**: `#0F0D10` bg · `#181518` surface · `#EFE6DF` text · `#D97757`
terracotta accent · `#6E1F24` oxblood slab · `#C4A96B` warm signal ·
`#2E2528` rule.

**Type**: DM Serif Display for the wordmark. Inter for body. JetBrains
Mono for code. **Caveat** (a hand-drawn cursive) for accent labels —
the session cards each have a one-line italic caption ("★" on the
active card), and the output area is labeled with hand-set section
labels ("today, on the fsi", "an entry" over the eval band).

**Layout**: Asymmetric two-column (320px rail / 1fr work). The left
column is a "notebook" rail with session cards and toggle switches;
the right is the work. The rail has a tilted oxblood slab at the top
(clip-path triangle), and the main column has a tilted terracotta bar
floating off the right edge. Controls are physical switches, not
buttons. The eval band has a terracotta left-border and a hand-script
label floating above it.

**Signature**: the hand-set Caveat labels. Every section has a caption
written as if by a person — "notes", "an entry", "today, on the fsi".
The tilted oxblood slab and the floating terracotta bar are the
asymmetry that breaks the grid. This is the only design that
explicitly puts a human voice into the UI.

**Risk**: Caveat is a *script* font, and it's a strong choice. If the
audience is allergic to "cute" UI, the whole thing reads as performative
warmth. The hand-written labels are not decorative — they encode the
session's intent — but they will be misread as decoration by some.

**File**: `04-studio-oblique.html` · `design-04-dark-studio.png`

**Animation philosophy (resize)**: spring. A 200ms cubic-bezier with
a slight overshoot — the sidebar "lands" rather than arrives. Matches
the soft, hand-made feel.

---

## Comparison

All four are dark. The question is what kind of dark.

| | Terminal Noir | Dark Editorial | Dark Blueprint | Dark Studio |
|---|---|---|---|---|
| Surface | Pure black | Deep ink | Charcoal | Warm charcoal |
| Type system | 1 face (mono) | 4 faces (display serif, body serif, mono, utility) | 2 faces (display, mono) | 4 faces (+ hand script) |
| Sidebar | Compact, monospace rows | Editorial "ledger" rows | Numbered section with corner-marked lists | Asymmetric, oxblood-slabbed cards with hand labels |
| Signature | Blinking block cursor | Brass rule + italic captions | § numbered sections + corner marks + grid | Caveat script captions + tilted oxblood slab |
| Aesthetic risk | Monospace *everywhere* | Serif for a developer tool | Pure engineering drawing | Hand-written script in a REPL |
| Resize anim | Snappy (0ms) | Soft (120ms ease) | Binary (snap) | Spring (overshoot) |
| Best for | "I want a terminal" | "I want a notebook" | "I want a control panel" | "I want a studio" |
| Accent color | Mint green | Brass/copper | Signal green | Terracotta + oxblood |

## What I'd ask you next

Three things, in order of how much they shape the design:

1. **Which direction feels right for the work you do most in SageFs?**
   Writing a quick function and seeing the result is *Terminal Noir* or
   *Dark Blueprint*. Iterating on a domain over hours is *Dark
   Editorial* or *Dark Studio*.

2. **Do you keep the right-hand sidebar at all?** All four mocks keep
   it (it's the "resize the sessions panel" performance problem you
   flagged). But honestly, sessions belong in the top tab strip with
   the active one expanded inline. That's a different design entirely
   — let me know if you want to see it.

3. **What does the eval input want to be?** A textarea is the default
   everywhere on the web, but for a REPL it might be wrong. Possibilities:
   a Vim-mode textbox (Helix-like), a structured form (each `let`/`type`
   is a row), or a terminal-style prompt that grows into a multi-line
   block. Tell me which is closest to how you actually work.
