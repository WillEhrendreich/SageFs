using System.Linq;
using FluentAssertions;
using Xunit;

namespace SageFs.VisualStudio.Editor.Tests;

/// <summary>
/// Tests for <see cref="DiagnosticStateTracker"/> — parses /diagnostics SSE events.
/// JSON shape: [{codeHash, diagnostics: [{message, severity, range: {startLine,...}}]}]
/// </summary>
public sealed class DiagnosticStateTrackerTests
{
    private static DiagnosticStateTracker Track(string json)
    {
        var t = new DiagnosticStateTracker();
        t.ProcessEvent(new SseEvent("diagnostics", json));
        return t;
    }

    [Fact]
    public void SingleError_ParsedCorrectly()
    {
        const string json = """
            [{
              "codeHash": "abc",
              "diagnostics": [{
                "message": "The value 'x' is not defined",
                "severity": "error",
                "range": { "startLine": 5, "startColumn": 3, "endLine": 5, "endColumn": 4 }
              }]
            }]
            """;

        var t = Track(json);
        var diags = t.GetDiagnosticsForLine(5);
        diags.Should().HaveCount(1);
        diags[0].Message.Should().Contain("'x' is not defined");
        diags[0].Severity.Should().Be(DiagnosticSeverity.Error);
        diags[0].StartLine.Should().Be(5);
        diags[0].StartColumn.Should().Be(3);
    }

    [Fact]
    public void Warning_ParsedCorrectly()
    {
        const string json = """
            [{"codeHash":"h","diagnostics":[{"message":"incomplete pattern match","severity":"warning","range":{"startLine":2,"startColumn":0,"endLine":2,"endColumn":5}}]}]
            """;

        var t = Track(json);
        t.GetDiagnosticsForLine(2)[0].Severity.Should().Be(DiagnosticSeverity.Warning);
    }

    [Fact]
    public void MultipleDiagnostics_AllParsed()
    {
        const string json = """
            [{
              "codeHash": "h",
              "diagnostics": [
                {"message":"err1","severity":"error","range":{"startLine":1,"startColumn":0,"endLine":1,"endColumn":3}},
                {"message":"err2","severity":"error","range":{"startLine":3,"startColumn":0,"endLine":3,"endColumn":3}}
              ]
            }]
            """;

        var t = Track(json);
        t.GetDiagnosticsForLine(1).Should().HaveCount(1);
        t.GetDiagnosticsForLine(3).Should().HaveCount(1);
        t.GetDiagnosticsForLine(2).Should().BeEmpty();
    }

    [Fact]
    public void MultipleCodeHashes_AllFlattened()
    {
        const string json = """
            [
              {"codeHash":"h1","diagnostics":[{"message":"e1","severity":"error","range":{"startLine":1,"startColumn":0,"endLine":1,"endColumn":1}}]},
              {"codeHash":"h2","diagnostics":[{"message":"e2","severity":"error","range":{"startLine":2,"startColumn":0,"endLine":2,"endColumn":1}}]}
            ]
            """;

        var t = Track(json);
        t.GetAll().Should().HaveCount(2);
    }

    [Fact]
    public void EmptyArray_ClearsState()
    {
        // First push some diagnostics
        const string json1 = """[{"codeHash":"h","diagnostics":[{"message":"e","severity":"error","range":{"startLine":1,"startColumn":0,"endLine":1,"endColumn":1}}]}]""";
        var t = new DiagnosticStateTracker();
        t.ProcessEvent(new SseEvent("diagnostics", json1));
        t.GetAll().Should().HaveCount(1);

        // Empty array clears
        t.ProcessEvent(new SseEvent("diagnostics", "[]"));
        t.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void StateChanged_FiredAfterParse()
    {
        int fired = 0;
        var t = new DiagnosticStateTracker();
        t.StateChanged += (_, _) => fired++;
        t.ProcessEvent(new SseEvent("diagnostics", "[]"));
        fired.Should().Be(1);
    }

    [Fact]
    public void NonDiagnosticsEvent_Ignored()
    {
        int fired = 0;
        var t = new DiagnosticStateTracker();
        t.StateChanged += (_, _) => fired++;
        t.ProcessEvent(new SseEvent("state", "{}"));
        fired.Should().Be(0);
        t.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void MalformedJson_DoesNotThrow()
    {
        var t = new DiagnosticStateTracker();
        var act = () => t.ProcessEvent(new SseEvent("diagnostics", "not-json"));
        act.Should().NotThrow();
    }
}

/// <summary>
/// Tests for <see cref="FileAnnotationTracker"/> — parses file_annotations SSE events.
/// </summary>
public sealed class FileAnnotationTrackerTests
{
    private static FileAnnotationTracker TrackAnnotations(string json)
    {
        var t = new FileAnnotationTracker();
        t.ProcessEvent(new SseEvent("file_annotations", json));
        return t;
    }

    [Fact]
    public void InlineFailure_AssertionDiff_ParsedCorrectly()
    {
        const string json = """
            {
              "FilePath": "C:/code/Tests.fs",
              "TestAnnotations": [],
              "InlineFailures": [{
                "Line": 10,
                "TestId": { "Fields": ["t1"] },
                "TestName": "should add numbers",
                "Duration": "00:00:00.012",
                "Failure": { "Case": "AssertionDiff", "Fields": ["1", "2"] }
              }],
              "CodeLenses": [],
              "CoverageAnnotations": []
            }
            """;

        var t = TrackAnnotations(json);
        var failures = t.GetFailuresForLine("C:\\code\\Tests.fs", 10);
        failures.Should().HaveCount(1);
        failures[0].TestName.Should().Be("should add numbers");
        failures[0].Presentation.Should().Contain("Expected: 1");
        failures[0].Presentation.Should().Contain("Actual: 2");
    }

    [Fact]
    public void InlineFailure_ExceptionMessage_ParsedCorrectly()
    {
        const string json = """
            {
              "FilePath": "src/Specs.fs",
              "InlineFailures": [{
                "Line": 5,
                "TestName": "myTest",
                "Failure": { "Case": "ExceptionMessage", "Fields": ["NullReferenceException: Object reference not set", "at Tests.fs:5"] }
              }]
            }
            """;

        var t = TrackAnnotations(json);
        t.GetFailuresForLine("src/Specs.fs", 5)[0].Presentation
            .Should().Contain("NullReferenceException");
    }

    [Fact]
    public void InlineFailure_ToInlineText_FormatsCorrectly()
    {
        var display = new InlineFailureDisplay("my test", "Expected: 42  Actual: 99");
        display.ToInlineText().Should().Be("⊘ my test — Expected: 42  Actual: 99");
    }

    [Fact]
    public void InlineFailure_NoPresentation_FormatsWithJustName()
    {
        var display = new InlineFailureDisplay("my test", "");
        display.ToInlineText().Should().Be("⊘ my test");
    }

    [Fact]
    public void FileAnnotations_ForwardSlashPath_NormalizesToBackslash()
    {
        const string json = """
            {
              "FilePath": "src/tests/Suite.fs",
              "InlineFailures": [{
                "Line": 3,
                "TestName": "t",
                "Failure": { "Case": "RawMessage", "Fields": ["fail"] }
              }]
            }
            """;

        var t = TrackAnnotations(json);
        t.GetFailuresForLine("src\\tests\\Suite.fs", 3).Should().HaveCount(1);
        t.GetFailuresForLine("src/tests/Suite.fs",  3).Should().HaveCount(1);
    }

    [Fact]
    public void FileAnnotations_CaseInsensitivePath()
    {
        const string json = """{"FilePath": "C:/Code/Tests.FS", "InlineFailures": [{"Line": 1, "TestName": "t", "Failure": {"Case": "RawMessage", "Fields": ["x"]}}]}""";
        var t = TrackAnnotations(json);
        t.GetFailuresForLine("c:\\code\\tests.fs", 1).Should().HaveCount(1);
    }

    [Fact]
    public void FileAnnotations_HasAnyForFile_TrueWhenAnnotationsPresent()
    {
        const string json = """{"FilePath": "test.fs", "InlineFailures": [{"Line": 1, "TestName": "t", "Failure": {"Case": "RawMessage", "Fields": ["x"]}}]}""";
        var t = TrackAnnotations(json);
        t.HasAnyForFile("test.fs").Should().BeTrue();
        t.HasAnyForFile("other.fs").Should().BeFalse();
    }

    [Fact]
    public void NonFileAnnotationsEvent_Ignored()
    {
        var t = new FileAnnotationTracker();
        t.ProcessEvent(new SseEvent("test_results_batch", "{}"));
        t.HasAnyForFile("anything.fs").Should().BeFalse();
    }

    [Fact]
    public void MalformedJson_DoesNotThrow()
    {
        var t = new FileAnnotationTracker();
        var act = () => t.ProcessEvent(new SseEvent("file_annotations", "bad-json"));
        act.Should().NotThrow();
    }

    [Fact]
    public void FileAnnotationsUpdated_FiredWithCorrectPath()
    {
        string? firedPath = null;
        var t = new FileAnnotationTracker();
        t.FileAnnotationsUpdated += (_, p) => firedPath = p;

        const string json = """{"FilePath": "c:/code/test.fs", "InlineFailures": []}""";
        t.ProcessEvent(new SseEvent("file_annotations", json));

        firedPath.Should().NotBeNull();
        firedPath!.Should().Contain("test.fs");
    }
}
