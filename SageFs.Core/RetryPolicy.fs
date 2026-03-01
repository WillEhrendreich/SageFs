module SageFs.RetryPolicy

open System

type RetryConfig = {
  MaxRetries: int
  BaseDelayMs: int
}

type RetryOutcome =
  | Success
  | RetryAfter of delayMs: int
  | GiveUp of exn

let defaults = { MaxRetries = 3; BaseDelayMs = 50 }

/// Classify whether an exception is a Marten/PostgreSQL version conflict
let isVersionConflict (ex: exn) : bool =
  match ex with
  | :? JasperFx.Events.EventStreamUnexpectedMaxEventIdException -> true
  | :? Marten.Exceptions.MartenCommandException as mce ->
    match mce.InnerException with
    | :? Npgsql.PostgresException as pe ->
      pe.SqlState = "23505"
    | _ -> false
  | _ -> false

/// Calculate backoff with jitter: base * (attempt + 1) ± 50%
let backoffMs (config: RetryConfig) (attempt: int) : int =
  let baseDelay = config.BaseDelayMs * (attempt + 1)
  let jitterRange = baseDelay / 2
  match jitterRange = 0 with
  | true -> baseDelay
  | false -> baseDelay - jitterRange + System.Random.Shared.Next(jitterRange * 2)

/// Whether more retries are available
let shouldRetry (config: RetryConfig) (attempt: int) : bool =
  attempt < config.MaxRetries

/// Pure decision: given config, attempt number, and exception, decide what to do
let decide (config: RetryConfig) (attempt: int) (ex: exn) : RetryOutcome =
  match isVersionConflict ex with
  | false -> GiveUp ex
  | true ->
    match shouldRetry config attempt with
    | false -> GiveUp ex
    | true -> RetryAfter (backoffMs config attempt)
