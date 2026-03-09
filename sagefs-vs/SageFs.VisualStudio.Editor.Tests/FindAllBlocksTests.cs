using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace SageFs.VisualStudio.Editor.Tests;

/// <summary>
/// Tests for <see cref="BlockHelpers.FindAllBlocks"/>.
/// Uses the pure static implementation in the Editor (net472) layer.
/// </summary>
public sealed class FindAllBlocksTests
{
  [Fact]
  public void EmptyFile_ReturnsEmptyList()
  {
    BlockHelpers.FindAllBlocks("").Should().BeEmpty();
  }

  [Fact]
  public void SemicolonMode_ThreeBlocks_ReturnedInOrder()
  {
    const string text = "let a = 1;;\nlet b = 2;;\nlet c = 3;;";
    var blocks = BlockHelpers.FindAllBlocks(text);
    blocks.Should().HaveCount(3);
    blocks[0].Should().Contain("let a = 1");
    blocks[1].Should().Contain("let b = 2");
    blocks[2].Should().Contain("let c = 3");
  }

  [Fact]
  public void BlankLineMode_ThreeBlocks_ReturnedInOrder()
  {
    const string text = "let a = 1\n\nlet b = 2\n\nlet c = 3";
    var blocks = BlockHelpers.FindAllBlocks(text);
    blocks.Should().HaveCount(3);
    blocks[0].Should().Be("let a = 1");
    blocks[1].Should().Be("let b = 2");
    blocks[2].Should().Be("let c = 3");
  }

  [Fact]
  public void SingleBlock_NoTerminator_ReturnsSingleItem()
  {
    const string text = "let x = 42";
    var blocks = BlockHelpers.FindAllBlocks(text);
    blocks.Should().HaveCount(1);
    blocks[0].Should().Be("let x = 42");
  }

  [Fact]
  public void MixedWhitespaceBlocks_SkippedFromResults()
  {
    const string text = "let a = 1\n\n   \n\nlet b = 2";
    var blocks = BlockHelpers.FindAllBlocks(text);
    blocks.Should().HaveCount(2);
    blocks[0].Should().Be("let a = 1");
    blocks[1].Should().Be("let b = 2");
  }

  [Fact]
  public void TrailingSemicolonWithNoContent_Filtered()
  {
    const string text = "let a = 1;;\n;;";
    var blocks = BlockHelpers.FindAllBlocks(text);
    blocks.Should().HaveCount(1);
    blocks[0].Should().Contain("let a = 1");
  }
}
