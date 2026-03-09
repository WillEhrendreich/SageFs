using FluentAssertions;
using Xunit;

namespace SageFs.VisualStudio.Editor.Tests;

/// <summary>
/// Tests for <see cref="BlockHelpers.IsTestElement"/> — the pure string-based
/// variant that can be called without VS SDK types.
/// </summary>
public sealed class TestElementDetectionTests
{
  [Fact]
  public void FactAttribute_IsTestElement_ReturnsTrue()
  {
    BlockHelpers.IsTestElement("Function", "let myTest () = ()", new[] { "Fact" })
      .Should().BeTrue();
  }

  [Fact]
  public void TestCaseKeyword_InText_IsTestElement_ReturnsTrue()
  {
    BlockHelpers.IsTestElement("Function", "testCase \"myTest\" <| fun () -> ()", new string[0])
      .Should().BeTrue();
  }

  [Fact]
  public void WrongKind_Namespace_IsNotTestElement()
  {
    BlockHelpers.IsTestElement("Namespace", "testCase \"x\" <| fun () -> ()", new string[0])
      .Should().BeFalse();
  }

  [Fact]
  public void PlainFunction_NoAttributes_IsNotTestElement()
  {
    BlockHelpers.IsTestElement("Function", "let normal () = 42", new string[0])
      .Should().BeFalse();
  }

  [Fact]
  public void TheoryAttribute_IsTestElement_ReturnsTrue()
  {
    BlockHelpers.IsTestElement("Method", "let paramTest x = ()", new[] { "Theory" })
      .Should().BeTrue();
  }

  [Fact]
  public void TestListKeyword_IsTestElement_ReturnsTrue()
  {
    BlockHelpers.IsTestElement("Function", "testList \"suite\" [ ]", new string[0])
      .Should().BeTrue();
  }
}
