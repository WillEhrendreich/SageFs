using FluentAssertions;
using Xunit;

namespace SageFs.VisualStudio.Editor.Tests;

/// <summary>
/// Tests for <see cref="StatusBarText.FormatStatusBarText"/>.
/// Pure function — no VS SDK required.
/// </summary>
public sealed class StatusBarTextTests
{
  [Fact]
  public void Connected_FormatsCorrectly()
  {
    var text = StatusBarText.FormatStatusBarText(connected: true, passingTests: 3, latencyMs: 42);
    text.Should().Be("⬤ SageFs Connected  3 passing  42ms");
  }

  [Fact]
  public void Disconnected_ShowsDisconnectedMessage()
  {
    var text = StatusBarText.FormatStatusBarText(connected: false, passingTests: 0, latencyMs: 0);
    text.Should().Be("⬤ SageFs Disconnected");
  }

  [Fact]
  public void Connected_ZeroTests_FormatsCorrectly()
  {
    var text = StatusBarText.FormatStatusBarText(connected: true, passingTests: 0, latencyMs: 5);
    text.Should().Contain("0 passing");
    text.Should().StartWith("⬤ SageFs Connected");
  }

  [Fact]
  public void Connected_HighLatency_ShowsLatency()
  {
    var text = StatusBarText.FormatStatusBarText(connected: true, passingTests: 10, latencyMs: 1500);
    text.Should().Contain("1500ms");
  }
}
