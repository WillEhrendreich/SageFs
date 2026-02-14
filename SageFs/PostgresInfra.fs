module SageFs.Server.PostgresInfra

open System
open Testcontainers.PostgreSql

/// Auto-start Postgres if no explicit connection string is provided.
/// .WithReuse(true) means the container survives SageFs process exit
/// and is reused on next launch (instant restart, no 2s startup).
let getOrStartPostgres () =
  match Environment.GetEnvironmentVariable("SageFs_CONNECTION_STRING") with
  | null | "" ->
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
    connStr
  | connectionString ->
    printfn "✓ Event store: PostgreSQL (explicit connection)"
    connectionString
