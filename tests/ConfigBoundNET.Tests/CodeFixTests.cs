// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using ConfigBoundNET;
using ConfigBoundNET.CodeFixes;
using Xunit;

namespace ConfigBoundNET.Tests;

/// <summary>
/// Tests for the ConfigBoundNET CodeFix providers.
/// </summary>
/// <remarks>
/// The pure-logic helper <c>EmptySectionNameCodeFix.InferSectionName</c> is
/// tested directly with simple in/out assertions. The actual Roslyn
/// workspace-level code-fix application is harder to test in isolation
/// without a full <c>Microsoft.CodeAnalysis.Testing</c> harness, so we
/// verify indirectly: the diagnostics fire (proven by existing tests),
/// the code fix providers are wired to the right diagnostic IDs (proven by
/// <c>FixableDiagnosticIds</c> assertions below), and the transform logic
/// is trivial enough to be correct by inspection.
/// </remarks>
public sealed class CodeFixTests
{
    // ── InferSectionName tests ───────────────────────────────────────────

    [Theory]
    [InlineData("DbConfig", "Db")]
    [InlineData("PaymentsOptions", "Payments")]
    [InlineData("AppSettings", "App")]
    [InlineData("LoggingConfiguration", "Logging")]
    [InlineData("Foo", "Foo")]
    [InlineData("Config", "Config")]         // stripping would leave empty
    [InlineData("Options", "Options")]       // stripping would leave empty
    [InlineData("Settings", "Settings")]     // stripping would leave empty
    [InlineData("Configuration", "Configuration")] // stripping would leave empty
    [InlineData("MyConfigOptions", "MyConfig")]    // only strips the last suffix
    public void InferSectionName_returns_expected(string typeName, string expected)
    {
        Assert.Equal(expected, SectionNameHelper.InferSectionName(typeName));
    }

    // ── FixableDiagnosticIds wiring ──────────────────────────────────────

    [Fact]
    public void MustBePartialCodeFix_fixes_CB0001()
    {
        var fix = new MustBePartialCodeFix();
        Assert.Contains("CB0001", fix.FixableDiagnosticIds);
    }

    [Fact]
    public void EmptySectionNameCodeFix_fixes_CB0002()
    {
        var fix = new EmptySectionNameCodeFix();
        Assert.Contains("CB0002", fix.FixableDiagnosticIds);
    }

    [Fact]
    public void NestedTypeCodeFix_fixes_CB0003()
    {
        var fix = new NestedTypeCodeFix();
        Assert.Contains("CB0003", fix.FixableDiagnosticIds);
    }

    [Fact]
    public void InvalidTargetKindCodeFix_fixes_CB0005()
    {
        var fix = new InvalidTargetKindCodeFix();
        Assert.Contains("CB0005", fix.FixableDiagnosticIds);
    }

    [Fact]
    public void RedundantRequiredCodeFix_fixes_CB0009()
    {
        var fix = new RedundantRequiredCodeFix();
        Assert.Contains("CB0009", fix.FixableDiagnosticIds);
    }
}
