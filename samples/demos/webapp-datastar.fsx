// ============================================================
//  🌐  Falco.Datastar Live Webapp Demo
//  A real-time reactive web app — no JavaScript written by hand.
//  Save the file → SageFs hot-patches the running server → browser updates.
//  No restart. No manual refresh. Under 100ms.
// ============================================================
//
//  To run this demo:
//    1. Start SageFs:  sagefs
//    2. Load this script or create a project with these dependencies
//    3. Visit http://localhost:5000
//    4. Edit any handler below, save, watch the page change live
//
//  Dependencies (central version management in Directory.Packages.props):
//    Falco, Falco.Markup, Falco.Datastar, SageFs.DevReloadMiddleware

// #r "nuget: Falco"
// #r "nuget: Falco.Markup"
// #r "nuget: Falco.Datastar"

open Falco
open Falco.Markup
open Falco.Datastar
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection

// ── Domain model — pure F# ──
type TodoItem = {
  Id:        int
  Text:      string
  Completed: bool
}

// Simple in-memory store (would be a real DB in production)
let mutable todos: TodoItem list = [
  { Id = 1; Text = "Try SageFs";        Completed = false }
  { Id = 2; Text = "Edit and save";     Completed = false }
  { Id = 3; Text = "Watch the magic";   Completed = false }
]
let mutable nextId = 4

// ── Shared HTML fragments — composable Falco.Markup ──
// No template language. Just F# functions returning typed HTML nodes.
// Edit a fragment, save, the browser updates. No webpack, no bundler.

let todoItemView (todo: TodoItem) =
  Elem.li [ Attr.class' (if todo.Completed then "done" else "") ] [
    Elem.input [
      Attr.type' "checkbox"
      if todo.Completed then Attr.checked'
      // Datastar: on change → POST /todo/toggle/{id}, merge the response fragment
      Ds.onEvent ("change", sprintf "@post('/todo/toggle/%d')" todo.Id)
    ]
    Elem.span [] [ Text.raw todo.Text ]
    Elem.button [
      Attr.class' "delete"
      Ds.onClick (sprintf "@post('/todo/delete/%d')" todo.Id)
    ] [ Text.raw "✕" ]
  ]

let todoListView (items: TodoItem list) =
  Elem.div [ Attr.id "todo-list" ] [
    Elem.ul [] (items |> List.map todoItemView)
    Elem.p [ Attr.class' "stats" ] [
      Text.rawf "%d remaining" (items |> List.filter (fun t -> not t.Completed) |> List.length)
    ]
  ]

let pageLayout (content: XmlNode list) =
  Elem.html [] [
    Elem.head [] [
      Elem.title [] [ Text.raw "SageFs Todo Demo" ]
      // Datastar CDN — the only JS you'll ever write
      Elem.script [ Attr.type' "module"; Attr.src "https://cdn.jsdelivr.net/npm/@starfederation/datastar" ] []
      Elem.style [] [ Text.raw """
        body { font-family: system-ui; max-width: 600px; margin: 2rem auto; padding: 0 1rem; }
        .done span { text-decoration: line-through; opacity: 0.5; }
        li { display: flex; gap: 0.5rem; align-items: center; padding: 0.25rem 0; }
        button.delete { border: none; background: none; cursor: pointer; color: #e55; }
        .stats { color: #888; font-size: 0.9em; }
        form { display: flex; gap: 0.5rem; margin-bottom: 1rem; }
        input[type=text] { flex: 1; padding: 0.4rem; border: 1px solid #ccc; border-radius: 4px; }
        button[type=submit] { padding: 0.4rem 0.8rem; }
      """ ]
    ]
    Elem.body [] [
      Elem.h1 [] [ Text.raw "✅ Todo — powered by SageFs + Falco.Datastar" ]
      Elem.p [] [ Text.raw "Edit this file, save it. The page updates. No refresh." ]
      Elem.form [
        // Datastar: on submit → POST /todo/add, replace the #todo-list fragment
        Ds.onEvent ("submit", "@post('/todo/add'); this.reset()")
      ] [
        Elem.input [ Attr.type' "text"; Attr.name "text"; Attr.placeholder "What needs doing?" ]
        Elem.button [ Attr.type' "submit" ] [ Text.raw "Add" ]
      ]
      // Datastar: on init → fetch the todo list from the server
      Elem.div [ Ds.onInit (Ds.get "/todo/list") ] [
        todoListView todos
      ]
      yield! content
    ]
  ]

// ── Route handlers — pure functions, no controller classes ──

let getHome : HttpHandler =
  Response.ofHtml (pageLayout [])

let getTodoList : HttpHandler =
  Response.ofHtml (todoListView todos)

// ┌─ HOT RELOAD DEMO: edit the text below, save, watch the page change ─┐
let getStats : HttpHandler =
  let pending   = todos |> List.filter (fun t -> not t.Completed) |> List.length
  let completed = todos |> List.filter (fun t -> t.Completed) |> List.length
  Response.ofHtml (
    Elem.div [ Attr.id "stats" ] [
      Text.rawf "📋 %d pending · ✅ %d done" pending completed
    ])
// └─────────────────────────────────────────────────────────────────────┘

let postAddTodo : HttpHandler = fun ctx -> task {
  let! form = Request.getFormCollectionAsync ctx
  let text = form["text"] |> string
  if text.Trim() <> "" then
    todos <- todos @ [{ Id = nextId; Text = text.Trim(); Completed = false }]
    nextId <- nextId + 1
  return! Response.ofHtml (todoListView todos) ctx
}

let postToggleTodo (id: int) : HttpHandler = fun ctx -> task {
  todos <-
    todos |> List.map (fun t ->
      if t.Id = id then { t with Completed = not t.Completed } else t)
  return! Response.ofHtml (todoListView todos) ctx
}

let postDeleteTodo (id: int) : HttpHandler = fun ctx -> task {
  todos <- todos |> List.filter (fun t -> t.Id <> id)
  return! Response.ofHtml (todoListView todos) ctx
}

// ── Routing — no controller registration, no DI setup ──
let routes = [
  get  "/"                   getHome
  get  "/todo/list"          getTodoList
  get  "/todo/stats"         getStats
  post "/todo/add"           postAddTodo
  mapPost "/todo/toggle/{id}" (fun r -> r.GetString("id", "0")) (fun id -> postToggleTodo (int id))
  mapPost "/todo/delete/{id}" (fun r -> r.GetString("id", "0")) (fun id -> postDeleteTodo (int id))
]

// ── App bootstrap — the whole server in ~10 lines ──
[<EntryPoint>]
let main args =
  webHost args {
    // SageFs.DevReloadMiddleware: the magic that makes browser hot reload work.
    // When you save a .fs file, SageFs hot-patches the server and sends SSE to browsers.
    use_middleware SageFs.DevReloadMiddleware.middleware
    endpoints routes
  }
  0

// ── What just happened? ──
// • You have a reactive web app with no JavaScript written by hand.
// • Datastar handles DOM diffing client-side; your server sends HTML fragments.
// • SageFs hot-patches the server on save — no restart needed.
// • The browser auto-refreshes via SSE — no manual F5.
// • The whole app is ~100 lines of F#. No MVC, no ViewModel, no Controller.
//
// Try changing the page title, the CSS, or a handler.  Save.  Watch.
// This is what development should feel like.
