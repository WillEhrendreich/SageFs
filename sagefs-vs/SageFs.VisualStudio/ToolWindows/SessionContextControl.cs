namespace SageFs.VisualStudio.ToolWindows;

using System.Threading;
using Microsoft.VisualStudio.Extensibility.UI;

internal class SessionContextControl : RemoteUserControl
{
  public SessionContextControl(object? dataContext, SynchronizationContext? synchronizationContext = null)
    : base(dataContext, synchronizationContext)
  {
  }
}
