namespace SageFs.VisualStudio.ToolWindows;

using System.Threading;
using Microsoft.VisualStudio.Extensibility.UI;

internal class TypeExplorerControl : RemoteUserControl
{
  public TypeExplorerControl(object? dataContext, SynchronizationContext? synchronizationContext = null)
    : base(dataContext, synchronizationContext)
  {
  }
}
