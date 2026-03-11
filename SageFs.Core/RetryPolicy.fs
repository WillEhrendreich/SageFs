module SageFs.RetryPolicy

open System
open SageFs.Measures

type RetryConfig = {
  MaxRetries: int
  BaseDelayMs: int<ms>
}

type RetryOutcome =
  | Success
  | RetryAfter of delayMs: int<ms>
  | GiveUp of exn

let defaults = { MaxRetries = 3; BaseDelayMs = 50<ms> }

/// Calculate backoff with jitter: base * (attempt + 1) ± 50%
let backoffMs (config: RetryConfig) (attempt: int) : int<ms> =
  let baseDelay = config.BaseDelayMs * (attempt + 1)
  let jitterRange = baseDelay / 2
  match jitterRange = 0<ms> with
  | true -> baseDelay
  | false -> baseDelay - jitterRange + System.Random.Shared.Next(int jitterRange * 2) * 1<ms>

/// Whether more retries are available
let shouldRetry (config: RetryConfig) (attempt: int) : bool =
  attempt < config.MaxRetries

/// Pure decision: given a retryability predicate, config, attempt number, and exception, decide what to do.
let decide (isRetryable: exn -> bool) (config: RetryConfig) (attempt: int) (ex: exn) : RetryOutcome =
  match isRetryable ex with
  | false -> GiveUp ex
  | true ->
    match shouldRetry config attempt with
    | false -> GiveUp ex
    | true -> RetryAfter (backoffMs config attempt)
