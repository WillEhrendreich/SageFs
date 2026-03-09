using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Xunit;

namespace SageFs.VisualStudio.Editor.Tests;

/// <summary>
/// P0 MEF attribute smoke tests — test 40, the one that should have been test 1.
///
/// These tests verify that all MEF tagger providers and adornment listeners
/// in SageFs.VisualStudio.Editor carry the correct [ContentType] and [Export]
/// attributes. They use reflection only — no live VS host required.
///
/// <para><b>Why "F#" (not "FSharp"):</b>
/// VS 2022's built-in F# language service registers the content type as <c>"F#"</c>.
/// Verifiable in the <c>Microsoft.FSharp.Editor</c> source on GitHub (Don Syme).
/// <c>"FSharp"</c> was the pre-Roslyn F# Power Tools content type and is WRONG for
/// extensions targeting the VS 2022 unified F# service.</para>
///
/// <para><b>Why both "F#" and "F# Script":</b>
/// VS does NOT walk the base-type chain for MEF tagger/factory/adornment exports.
/// .fsx files have content type <c>"F# Script"</c> — NOT a sub-type of "F#".
/// Every export that must handle both .fs and .fsx MUST list both explicitly.</para>
///
/// If any of these tests fail, NO MEF-based visual feature in the extension works —
/// glyphs, squiggles, and inline failure adornments all silently no-op.
/// Fix the ContentType attribute FIRST before investigating any other editor feature.
/// </summary>
public class MefAttributeTests
{
  // ── ITaggerProvider exports ───────────────────────────────────────────────

  [Theory]
  [InlineData(typeof(TestGlyphTaggerProvider))]
  [InlineData(typeof(SquiggleTaggerProvider))]
  public void TaggerProviders_ExportedAsTaggerProvider(Type providerType)
  {
    GetExportContractTypes(providerType)
      .Should().Contain(typeof(ITaggerProvider),
        because: $"{providerType.Name} must be exported as ITaggerProvider for VS MEF to discover it. " +
                 "Without this, no glyph or squiggle feature will activate.");
  }

  [Theory]
  [InlineData(typeof(TestGlyphTaggerProvider))]
  [InlineData(typeof(SquiggleTaggerProvider))]
  public void TaggerProviders_HaveContentTypeFSharp(Type providerType)
  {
    GetContentTypes(providerType)
      .Should().Contain("F#",
        because: $"{providerType.Name} must declare [ContentType(\"F#\")] so VS MEF composes it " +
                 "when opening .fs files. If wrong, glyphs/squiggles silently disappear. " +
                 "Correct value for VS 2022: 'F#' (not 'FSharp').");
  }

  [Theory]
  [InlineData(typeof(TestGlyphTaggerProvider))]
  [InlineData(typeof(SquiggleTaggerProvider))]
  public void TaggerProviders_HaveContentTypeFSharpScript(Type providerType)
  {
    GetContentTypes(providerType)
      .Should().Contain("F# Script",
        because: $"{providerType.Name} must declare [ContentType(\"F# Script\")] for .fsx files. " +
                 "VS does NOT walk the base-type chain for MEF tagger exports — both content types " +
                 "must be listed explicitly.");
  }

  // ── IGlyphFactoryProvider export ─────────────────────────────────────────

  [Fact]
  public void TestGlyphFactoryProvider_HasContentTypeFSharp()
  {
    GetContentTypes(typeof(TestGlyphFactoryProvider))
      .Should().Contain("F#",
        because: "TestGlyphFactoryProvider must match .fs files — glyph margin won't draw without this.");
  }

  [Fact]
  public void TestGlyphFactoryProvider_HasContentTypeFSharpScript()
  {
    GetContentTypes(typeof(TestGlyphFactoryProvider))
      .Should().Contain("F# Script",
        because: "TestGlyphFactoryProvider must match .fsx files — VS does not walk base-type chain for glyph factory exports.");
  }

  // ── IWpfTextViewCreationListener export ──────────────────────────────────

  [Fact]
  public void InlineFailureAdornmentListener_ExportedAsWpfTextViewCreationListener()
  {
    GetExportContractTypes(typeof(InlineFailureAdornmentListener))
      .Should().Contain(typeof(IWpfTextViewCreationListener),
        because: "InlineFailureAdornmentListener must be exported as IWpfTextViewCreationListener " +
                 "for VS to call TextViewCreated and create the adornment manager.");
  }

  [Fact]
  public void InlineFailureAdornmentListener_HasContentTypeFSharp()
  {
    GetContentTypes(typeof(InlineFailureAdornmentListener))
      .Should().Contain("F#",
        because: "InlineFailureAdornmentListener must compose with .fs files. " +
                 "Without [ContentType(\"F#\")], inline failure adornments never render.");
  }

  [Fact]
  public void InlineFailureAdornmentListener_HasContentTypeFSharpScript()
  {
    GetContentTypes(typeof(InlineFailureAdornmentListener))
      .Should().Contain("F# Script",
        because: "InlineFailureAdornmentListener must compose with .fsx files. " +
                 "VS does not walk base-type chain for IWpfTextViewCreationListener exports.");
  }

  // ── Permanent regression: content type completeness ──────────────────────

  [Fact]
  public void AllExportedMefTypes_HaveAtLeastOneContentTypeAttribute()
  {
    // Every type exported as a tagger, factory, or adornment listener must declare
    // at least one ContentType. A missing ContentType silently prevents composition.
    var mefExportedEditorTypes = typeof(TestGlyphTaggerProvider).Assembly
      .GetTypes()
      .Where(t => t.GetCustomAttributes(typeof(ExportAttribute), false)
                   .Cast<ExportAttribute>()
                   .Any(e => IsEditorExportType(e.ContractType)))
      .ToList();

    foreach (var type in mefExportedEditorTypes)
    {
      GetContentTypes(type)
        .Should().NotBeEmpty(
          because: $"{type.Name} is exported as an editor MEF component but has NO [ContentType] attribute. " +
                   "VS will never compose it. Add [ContentType(\"F#\")] and [ContentType(\"F# Script\")].");
    }
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  private static List<string> GetContentTypes(Type type) =>
    type.GetCustomAttributes(typeof(ContentTypeAttribute), inherit: false)
      .Cast<ContentTypeAttribute>()
      .Select(a => a.ContentTypes)  // ContentTypes is a string property (singular value despite plural name)
      .ToList();

  private static List<Type> GetExportContractTypes(Type type) =>
    type.GetCustomAttributes(typeof(ExportAttribute), inherit: false)
      .Cast<ExportAttribute>()
      .Where(e => e.ContractType is not null)
      .Select(e => e.ContractType!)
      .ToList();

  private static bool IsEditorExportType(Type? t) =>
    t == typeof(ITaggerProvider)
    || t == typeof(IWpfTextViewCreationListener)
    || (t?.Name?.Contains("GlyphFactory") == true);
}
