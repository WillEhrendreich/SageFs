/// Evaluate, as a small button that pops a box open. It is not the main workflow: the live bindings took the bottom region, and
/// this is for checking something quick. Closed until the user opens it (the button, or key `e`), closed again by its close
/// button or Escape, and it keeps nothing between page loads.
///
/// Behavior is exactly what the old bottom bar did: the same textarea, the same `/dashboard/eval` post, the same reset, hard
/// reset, cancel and load-file controls, and the answer lands in `#eval-result` and in the output panel as before. It is a native
/// `<details>` whose `open` the morph is told to leave alone (the Evaluate area has always been one), so the one `#main` morph
/// never closes it under the user. The box itself is fixed to the viewport, so it can never be clipped by the panel it sits in
/// or run off the screen at any width.
module SageFs.Server.EvaluatePopover

open Falco.Markup
open Falco.Datastar
open SageFs.Server.DashboardTypes

/// The shortcut that opens the popover from anywhere on the page that is not a text field.
let [<Literal>] OpenKey = "e"

let private testid (name: string) = Attr.create "data-testid" name

/// The keydown handler of the textarea. Datastar evaluates it with `this` unbound, so the element is reached through
/// `event.target`. Alt+Enter evaluates, Ctrl+L clears the output, Tab indents, and Escape dismisses the completion list first
/// (and keeps the page-level Escape, which closes the popover, from also firing).
let private keydownScript =
  sprintf
    "var t=event.target; if(event.altKey && event.key === 'Enter') { event.preventDefault(); @post('/dashboard/eval') } if(event.ctrlKey && event.key === 'l') { event.preventDefault(); @post('/dashboard/clear-output') } if(event.key === 'Tab') { event.preventDefault(); var s=t.selectionStart; var e=t.selectionEnd; t.value=t.value.substring(0,s)+'  '+t.value.substring(e); t.selectionStart=t.selectionEnd=s+2; t.dispatchEvent(new Event('input')) } if(event.key === 'Escape') { var dd=document.getElementById('%s'); if(dd.style.display==='block') { dd.style.display='none'; event.stopPropagation() } }"
    DomIds.CompletionDropdown

let private actionButton (cls: string) (id: string) (label: string) (glyph: string) (text: string) (post: string) =
  Elem.button
    [ Attr.class' cls
      testid id
      Attr.create "aria-label" label
      Ds.indicator Signals.ActionLoading
      Ds.attr' ("disabled", "$actionLoading")
      Ds.onClick (Ds.post post) ]
    [ Elem.span [ Ds.show "$actionLoading" ] [ Text.raw "⏳ " ]
      Elem.span [ Ds.show "!$actionLoading" ] [ Text.raw glyph ]
      Text.raw text ]

/// The popover. `keyboardHelp` is the shortcuts table the old bar carried behind its ⌨ toggle.
let render (keyboardHelp: XmlNode) : XmlNode =
  Elem.details [ Attr.id DomIds.EvaluateSection; Attr.class' "eval-pop"; Ds.preserveAttr "open" ] [
    Elem.summary
      [ Attr.class' "session-btn eval-pop-toggle"
        testid "evaluate-toggle"
        Attr.create "aria-label" (sprintf "Evaluate: open a quick box to check something (key %s)" OpenKey)
        Attr.create "title" (sprintf "Evaluate (key %s)" OpenKey) ]
      [ Text.raw "λ" ]
    Elem.div [ Attr.class' "eval-pop-body"; Attr.create "role" "dialog"; Attr.create "aria-labelledby" "eval-pop-title" ] [
      Elem.div [ Attr.class' "eval-pop-head" ] [
        Elem.span [ Attr.id "eval-pop-title"; Attr.class' "eval-pop-title" ] [ Text.raw "Evaluate" ]
        Elem.span [ Attr.class' "eval-pop-size meta"; Ds.text """$code ? ($code.split('\\n').length + 'L ' + $code.length + 'c') : ''""" ] []
        Elem.button
          [ Attr.class' "session-btn"
            Attr.type' "button"
            Attr.create "aria-label" "Toggle keyboard shortcuts help"
            Ds.onEvent ("click", "$helpVisible = !$helpVisible") ]
          [ Text.raw "⌨" ]
        Elem.button
          [ Attr.class' "session-btn"
            Attr.type' "button"
            testid "evaluate-close"
            Attr.create "aria-label" "Close Evaluate"
            Ds.onEvent ("click", sprintf "document.getElementById('%s').open = false" DomIds.EvaluateSection) ]
          [ Text.raw "✕" ]
      ]
      Elem.div [ Attr.id DomIds.KeyboardHelpWrapper; Ds.show "$helpVisible" ] [ keyboardHelp ]
      Elem.div [ Attr.class' "eval-pop-input" ] [
        Elem.textarea
          [ Attr.class' "eval-input"
            Attr.id DomIds.EvalTextarea
            Ds.bind Signals.Code
            Attr.create "placeholder" "Enter F# code... (Alt+Enter to eval, ;; auto-appended)"
            Ds.onEvent ("keydown", keydownScript)
            Ds.onEvent ("input.debounce_300ms", sprintf "var t=event.target; var c=t.value[t.selectionStart-1]; $%s = t.selectionStart; if(c==='.'||(c>='a'&&c<='z')||(c>='A'&&c<='Z')){@post('/dashboard/completions')}" Signals.CursorPos)
            Attr.create "spellcheck" "false" ]
          []
        Elem.div
          [ Attr.id DomIds.CompletionDropdown
            Attr.style "display:none; position:absolute; bottom:100%; left:0; max-height:200px; overflow-y:auto; background:var(--bg-default); border:1px solid var(--bg-selection); border-radius:0; z-index:100; min-width:200px; font-size:0.85em; box-shadow:0 -2px 8px rgba(0,0,0,0.3);" ]
          []
      ]
      Elem.div [ Attr.class' "eval-controls" ] [
        // Eval / reset / hard-reset share one in-flight signal so every action button is disabled while ANY of them is
        // running: a click can never double-fire a destructive reset behind an eval.
        actionButton "eval-btn" "eval" "Evaluate — run the code in the editor" "▶ " "[EVAL]" "/dashboard/eval"
        // Cancel appears the moment EVAL/RESET/HARD_RESET goes in-flight (the same $actionLoading that disables them), and it
        // must NOT bind its own `disabled` to that signal, or a running eval could never be reached to cancel it. Its own
        // $cancelLoading only guards against double-submitting the cancel request itself. Best-effort: it cooperatively
        // cancels (CTS + thread interrupt), which stops an eval blocked on I/O but cannot preempt a tight CPU loop with no
        // yield point; Hard Reset remains the guaranteed way out of that case.
        Elem.button
          [ Attr.class' "eval-btn eval-btn-cancel"
            testid "cancel-eval"
            Attr.create "aria-label" "Cancel — request cancellation of the in-flight evaluation (best-effort; cannot stop a tight loop with no I/O)"
            Ds.show "$actionLoading"
            Ds.indicator Signals.CancelLoading
            Ds.attr' ("disabled", "$cancelLoading")
            Ds.onClick (Ds.post "/dashboard/cancel-eval") ]
          [ Elem.span [ Ds.show "$cancelLoading" ] [ Text.raw "⏳ " ]
            Elem.span [ Ds.show "!$cancelLoading" ] [ Text.raw "⛔ " ]
            Text.raw "[CANCEL]" ]
        actionButton "eval-btn eval-btn-reset" "reset" "Reset — restart the FSI session, keeping loaded projects" "↻ " "[RESET]" "/dashboard/reset"
        actionButton "eval-btn eval-btn-reset eval-btn-hard" "hard-reset" "Hard Reset — rebuild and restart the FSI session from scratch" "✖ " "[HARD_RESET]" "/dashboard/hard-reset"
        Elem.label
          [ Attr.class' "eval-btn eval-btn-file"
            Attr.create "aria-label" "Load File — read a local .fs/.fsx/.fsi file into the editor" ]
          [ Elem.input
              [ Attr.type' "file"
                Attr.accept ".fs,.fsx,.fsi"
                Attr.style "display: none;"
                Ds.onEvent ("change", sprintf "var i=event.target; var f=i.files[0]; if(f){var r=new FileReader(); r.onload=function(){var ta=document.getElementById('%s'); ta.value=r.result; ta.dispatchEvent(new Event('input'))}; r.readAsText(f); i.value=''}" DomIds.EvalTextarea) ]
            Text.raw "📂 Load File" ]
      ]
      Elem.div [ Attr.id DomIds.EvalResult ] []
    ]
  ]
