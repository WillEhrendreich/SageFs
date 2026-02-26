module SageFs.Server.PostgresInfra

open System
open Testcontainers.PostgreSql

/// Auto-start Postgres if no explicit connection string is provided.
/// .WithReuse(true) means the container survives SageFs process exit
/// and is reused on next launch (instant restart, no 2s startup).
let getOrStartPostgres () =
  match Environment.GetEnvironmentVariable("SageFs_CONNECTION_STRING") with
  | s when System.String.IsNullOrEmpty s ->
    try
      let container =
        PostgreSqlBuilder()
          .WithDatabase("SageFs")
          .WithUsername("postgres")
          .WithPassword("SageFs")
          .WithImage("postgres:18")
          .WithReuse(true)
          .WithVolumeMount("sagefs-pgdata", "/var/lib/postgresql")
          .Build()
      container.StartAsync().GetAwaiter().GetResult()
      let connStr = container.GetConnectionString()
      printfn "✓ Event store: PostgreSQL (auto-started via Docker)"
      Some connStr
    with ex ->
      eprintfn "⚠ Docker is not available — session history will not be persisted."
      eprintfn "  Install and start Docker for persistent session resume across restarts."
      eprintfn "  Alternatively, set SageFs_CONNECTION_STRING to an existing PostgreSQL instance."
      eprintfn "  Detail: %s" ex.Message
      None
  | connectionString ->
    printfn "✓ Event store: PostgreSQL (explicit connection)"
    Some connectionString
