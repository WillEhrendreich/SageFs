/// Pure decisions behind the Type Explorer's listing: how big the set was, and what the tree says when it
/// shows only part of it.
///
/// WHY this module exists: `POST /api/completions` cut every reply at 50 members and said nothing, so the tree
/// listed 50 of `System.IO`'s 212 and looked complete. The reply now carries `total` and `totalKind` (`exact`
/// or `atLeast`, the daemon's `CompletionTotal.label`), and the explorer asks for as many as it will show
/// (`limit`). When the set is larger than the rows, the last row says so.
///
/// No Fable dependency; tested under `dotnet fsi` (tests/TypeExplorerContractTests.fsx).
module SageFs.Vscode.TypeExplorerPure

/// The size a reply states for the set it drew from.
type SetSize =
  | ExactSize of int
  | AtLeastSize of int
  /// The reply did not state a size (it carried no `total`).
  | SizeNotStated

/// What the tree shows after the members.
type ListingTail =
  | NothingMore
  | PartialListing of shown: int * size: SetSize

/// The members the explorer asks for in one reply. The tree has always shown at most this many.
[<Literal>]
let RowsRequested = 200

/// The words of `totalKind` are `LadderSizeKind`'s (SageFs.Core/LadderContract.fs, compiled into this project), the
/// same ones the daemon's `CompletionTotal.label` writes. A word the contract does not know states no size.
let parseSetSize (total: int option) (totalKind: string) : SetSize =
  match total, SageFs.LadderSizeKind.tryParse totalKind with
  | Some n, Some SageFs.LadderSizeKind.Exact -> ExactSize n
  | Some n, Some SageFs.LadderSizeKind.AtLeast -> AtLeastSize n
  | _ -> SizeNotStated

/// A floor always means more may exist; an exact size means more exists only when it exceeds the rows shown.
let tailOf (shown: int) (size: SetSize) : ListingTail =
  match size with
  | ExactSize n when n > shown -> PartialListing(shown, size)
  | AtLeastSize _ -> PartialListing(shown, size)
  | ExactSize _
  | SizeNotStated -> NothingMore

/// The label and description of the tree row for a partial listing.
let tailRow (tail: ListingTail) : (string * string) list =
  match tail with
  | NothingMore -> []
  | PartialListing(shown, ExactSize n) -> [ (sprintf "Showing %d of %d" shown n, "the rest are not listed here") ]
  | PartialListing(shown, AtLeastSize n) -> [ (sprintf "Showing %d of at least %d" shown n, "the daemon stopped listing there") ]
  | PartialListing(shown, SizeNotStated) -> [ (sprintf "Showing %d" shown, "") ]
