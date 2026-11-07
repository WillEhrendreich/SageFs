/// The public shape of a SageFs.Core build, as a program compiled against it sees it.
namespace SageFs

open System.Reflection

module CoreSurface =

  /// One digest per exported type, keyed by the type's full name.
  type Surface = Map<string, string>

  /// What a comparison of two surfaces found, the first difference in name order.
  [<RequireQualifiedAccess>]
  type SurfaceChange =
    | Unchanged
    | Changed of typeName: string
    | Added of typeName: string
    | Removed of typeName: string

  let surfaceOf (_assembly: Assembly) : Surface = Map.empty

  let surfaceOfFile (_path: string) : Result<Surface, string> = Ok Map.empty

  let compareSurfaces (_before: Surface) (_after: Surface) : SurfaceChange = SurfaceChange.Unchanged
