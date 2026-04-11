// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ConfigBoundNET.CodeFixes;

/// <summary>
/// Offers a one-click "Use '{Inferred}' as section name" fix for CB0002.
/// Strips common suffixes (<c>Config</c>, <c>Options</c>, <c>Settings</c>,
/// <c>Configuration</c>) from the type name to produce the section name.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(EmptySectionNameCodeFix))]
[Shared]
internal sealed class EmptySectionNameCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(DiagnosticDescriptors.EmptySectionName.Id);

    public override FixAllProvider? GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var node = root?.FindNode(diagnostic.Location.SourceSpan);

        if (node is not TypeDeclarationSyntax typeDecl)
        {
            return;
        }

        // Delegate to the shared helper in the generator project so the
        // suffix-stripping logic is defined in exactly one place.
        var inferredName = SectionNameHelper.InferSectionName(typeDecl.Identifier.Text);

        context.RegisterCodeFix(
            CodeAction.Create(
                title: $"Use '{inferredName}' as section name",
                createChangedDocument: ct => ReplaceSectionNameAsync(context.Document, typeDecl, inferredName, ct),
                equivalenceKey: "CB0002_InferSectionName"),
            diagnostic);
    }

    private static async Task<Document> ReplaceSectionNameAsync(
        Document document,
        TypeDeclarationSyntax typeDecl,
        string name,
        CancellationToken cancellationToken)
    {
        // Walk the attribute lists looking for [ConfigSection("...")].
        foreach (var attrList in typeDecl.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var attrName = attr.Name.ToString();
                if (!attrName.Contains("ConfigSection"))
                {
                    continue;
                }

                if (attr.ArgumentList is null || attr.ArgumentList.Arguments.Count == 0)
                {
                    continue;
                }

                var arg = attr.ArgumentList.Arguments[0];
                var newLiteral = SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(name));
                var newArg = arg.WithExpression(newLiteral);
                var newArgList = attr.ArgumentList.WithArguments(
                    SyntaxFactory.SeparatedList(new[] { newArg }));
                var newAttr = attr.WithArgumentList(newArgList);

                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                return document.WithSyntaxRoot(root!.ReplaceNode(attr, newAttr));
            }
        }

        return document;
    }
}
