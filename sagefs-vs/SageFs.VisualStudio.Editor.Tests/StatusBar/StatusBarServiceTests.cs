using FluentAssertions;
using Xunit;

namespace SageFs.VisualStudio.Editor.Tests;

/// <summary>
/// Tests for <see cref="StatusBarService"/>.
/// Runs without a VS host — verifies that SetText/Clear never propagate exceptions
/// when called outside the VS process (where ThreadHelper throws).
/// </summary>
public sealed class StatusBarServiceTests
{
  [Fact]
  public void SetText_DoesNotThrow_WhenCalledOutsideVsHost()
  {
    var service = new SageFs.VisualStudio.Editor.StatusBar.StatusBarService();
    // ThreadHelper.ThrowIfNotOnUIThread() throws outside VS — must be swallowed.
    var ex = Record.Exception(() => service.SetText("SageFs — 5 passing  12ms"));
    ex.Should().BeNull(because: "StatusBarService.SetText must not propagate VS-host exceptions");
  }

  [Fact]
  public void Clear_DoesNotThrow_WhenCalledOutsideVsHost()
  {
    var service = new SageFs.VisualStudio.Editor.StatusBar.StatusBarService();
    var ex = Record.Exception(() => service.Clear());
    ex.Should().BeNull(because: "StatusBarService.Clear must not propagate VS-host exceptions");
  }

  [Fact]
  public void Constructor_RegistersInstanceAsStatusBarBridgeCurrent()
  {
    var service = new SageFs.VisualStudio.Editor.StatusBar.StatusBarService();
    SageFs.VisualStudio.Editor.StatusBar.StatusBarBridge.Current
      .Should().Be(service, because: "constructor must register itself so MEF components can call it");
  }

  [Fact]
  public void SetText_EmptyString_DoesNotThrow()
  {
    var service = new SageFs.VisualStudio.Editor.StatusBar.StatusBarService();
    var ex = Record.Exception(() => service.SetText(""));
    ex.Should().BeNull();
  }
}
