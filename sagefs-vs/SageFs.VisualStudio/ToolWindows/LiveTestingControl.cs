namespace SageFs.VisualStudio.ToolWindows;

using System.Threading;
using Microsoft.VisualStudio.Extensibility.UI;

internal class LiveTestingControl : RemoteUserControl
{
  public LiveTestingControl(object? dataContext, SynchronizationContext? synchronizationContext = null)
    : base(dataContext, synchronizationContext)
  {
  }
}
