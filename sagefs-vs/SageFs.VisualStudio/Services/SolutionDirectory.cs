namespace SageFs.VisualStudio.Services;

using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.ProjectSystem.Query;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Helpers for resolving the currently open solution directory.
/// Uses the VS Extensibility SDK WorkspacesExtensibility.QuerySolutionAsync —
/// never falls back to Directory.GetCurrentDirectory().
/// </summary>
internal static class SolutionDirectory
{
  /// <summary>
  /// Returns the directory containing the current solution, or null if no solution is open.
  /// The returned path can be passed directly to DaemonTargetFinder.findTarget.
  /// </summary>
  public static async Task<string?> GetAsync(VisualStudioExtensibility extensibility, CancellationToken ct)
  {
    try
    {
      var results = await extensibility.Workspaces().QuerySolutionAsync(
        q => q.With(s => s.Directory),
        ct).ConfigureAwait(false);
      foreach (var solution in results)
      {
        if (solution.Directory is { Length: > 0 } dir)
          return dir;
      }
    }
    catch
    {
      // Swallow — no solution open or the query is unavailable.
      // Callers check for null and handle gracefully.
    }
    return null;
  }
}
