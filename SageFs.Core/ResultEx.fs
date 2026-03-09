namespace SageFs

/// Railway-oriented programming combinators for Result<'T, SageFsError>.
/// These extend the standard Result module with SageFs-specific operations
/// that make error handling composable and explicit.
[<RequireQualifiedAccess>]
module ResultEx =

  /// Apply a function to the Ok value, preserving errors.
  let inline map f = function
    | Ok v -> Ok (f v)
    | Error e -> Error e

  /// Apply a function that may fail to the Ok value.
  let inline bind f = function
    | Ok v -> f v
    | Error e -> Error e

  /// Apply a function to the Error value, preserving successes.
  let inline mapError f = function
    | Ok v -> Ok v
    | Error e -> Error (f e)

  /// Unwrap Ok or apply a default function to Error.
  let inline defaultWith f = function
    | Ok v -> v
    | Error e -> f e

  /// Unwrap Ok or return a default value.
  let inline defaultValue d = function
    | Ok v -> v
    | Error _ -> d

  /// Convert Option to Result with the given error.
  let inline ofOption error = function
    | Some v -> Ok v
    | None -> Error error

  /// Convert Result to Option (discards error).
  let toOption = function
    | Ok v -> Some v
    | Error _ -> None

  /// Combine two Results — both must succeed.
  let inline zip r1 r2 =
    match r1, r2 with
    | Ok a, Ok b -> Ok (a, b)
    | Error e, _ -> Error e
    | _, Error e -> Error e

  /// Apply a Result-wrapped function to a Result-wrapped value.
  let inline apply fResult xResult =
    match fResult, xResult with
    | Ok f, Ok x -> Ok (f x)
    | Error e, _ -> Error e
    | _, Error e -> Error e

  /// Tap into the Ok value without changing the result.
  let inline tap f = function
    | Ok v -> f v; Ok v
    | Error e -> Error e

  /// Tap into the Error value without changing the result.
  let inline tapError f = function
    | Ok v -> Ok v
    | Error e -> f e; Error e

  /// Collect Results from a list — all must succeed or first error wins.
  let sequence (results: Result<'T, 'E> list) : Result<'T list, 'E> =
    let rec go acc = function
      | [] -> Ok (List.rev acc)
      | Ok v :: rest -> go (v :: acc) rest
      | Error e :: _ -> Error e
    go [] results

  /// Map each element and collect — all must succeed.
  let traverse (f: 'T -> Result<'U, 'E>) (items: 'T list) : Result<'U list, 'E> =
    items |> List.map f |> sequence

  /// Partition results into successes and failures.
  let partition (results: Result<'T, 'E> list) : 'T list * 'E list =
    let mutable oks = []
    let mutable errs = []
    for r in List.rev results do
      match r with
      | Ok v -> oks <- v :: oks
      | Error e -> errs <- e :: errs
    (oks, errs)

  /// True if the result is Ok.
  let isOk = function
    | Ok _ -> true
    | Error _ -> false

  /// True if the result is Error.
  let isError = function
    | Ok _ -> false
    | Error _ -> true

  /// Describe a Result<'T, SageFsError> for logging.
  let describe (describeOk: 'T -> string) (result: Result<'T, SageFsError>) : string =
    match result with
    | Ok v -> sprintf "Ok: %s" (describeOk v)
    | Error e -> sprintf "Error: %s" (SageFsError.describe e)
