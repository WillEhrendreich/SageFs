namespace SageFs.VisualStudio.Editor.StatusBar;

/// <summary>Thin wrapper for VS status bar text updates, callable from the SDK side.</summary>
public interface IStatusBarService
{
  void SetText(string text);
  void Clear();
}
