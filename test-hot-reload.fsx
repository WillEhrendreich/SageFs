// Simple web app to test SageFs hot reload
open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Hosting

// Handler function that we'll hot reload
let mutable handleHome (ctx: HttpContext) =
    task {
        ctx.Response.ContentType <- "text/html"
        do! ctx.Response.WriteAsync("""
            <html>
            <body style='font-family: sans-serif; padding: 50px;'>
                <h1>SageFs Hot Reload Test</h1>
                <h2 style='color: blue;'>Version 1</h2>
                <p>This is the initial version</p>
            </body>
            </html>
        """)
    }

// Create and configure the app
let builder = WebApplication.CreateBuilder()
builder.WebHost.UseUrls("http://localhost:5555") |> ignore
let app = builder.Build()

app.MapGet("/", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> handleHome ctx)) |> ignore

printfn "Starting web server on http://localhost:5555"
printfn "After it starts, open browser to http://localhost:5555"
printfn "Then modify handleHome function and send it to FSI to see instant changes!"

// Start in background
let appTask = System.Threading.Tasks.Task.Run(fun () -> app.Run())
app
