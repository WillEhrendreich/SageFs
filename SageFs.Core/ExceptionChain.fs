namespace SageFs

open System

/// What went wrong, down to the cause.
///
/// `exn.Message` of a wrapper says the wrapper's own words and leaves out the ones that say why. An AggregateException reads
/// "One or more errors occurred. (An error occurred while sending the request.)", and that HttpRequestException's cause is
/// "The response ended prematurely" over "Connection reset by peer", which is what a person has to act on. A failure that
/// reaches a user or an agent as a message carries the whole chain.
module ExceptionChain =

  /// The exceptions from the outermost to the root cause. An AggregateException is its inner exceptions (its own message only
  /// repeats theirs), and one with nothing inside is itself.
  let rec causes (ex: exn) : exn list =
    match ex with
    | null -> []
    | :? AggregateException as aggregate ->
      match aggregate.InnerExceptions.Count with
      | 0 -> [ ex ]
      | _ -> aggregate.InnerExceptions |> Seq.toList |> List.collect causes
    | _ -> ex :: causes ex.InnerException

  /// `Type: message`, outermost first, joined with " <- " so the cause is read at the end.
  let describe (ex: exn) : string =
    causes ex
    |> List.map (fun cause -> sprintf "%s: %s" (cause.GetType().Name) cause.Message)
    |> String.concat " <- "
