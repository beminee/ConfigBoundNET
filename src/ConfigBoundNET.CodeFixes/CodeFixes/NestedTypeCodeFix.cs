// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ConfigBoundNET.CodeFixes;

/// <summary>
/// Offers a one-click "Move type to namespace scope" fix for CB0003.
/// Extracts the nested type declaration from its containing type and places
/// it as a sibling at the same namespace level.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(NestedTypeCodeFix))]
[Shared]
internal sealed class NestedTypeCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(DiagnosticDescriptors.NestedTypeNotSupported.Id);

    public override FixAllProvider? GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var node = root?.FindNode(diagnostic.Location.SourceSpan);

        if (node is not TypeDeclarationSyntax nestedType)
        {
            return;
        }

        // Only offer the fix when there's a clear containing type to extract from.
        if (nestedType.Parent is not TypeDeclarationSyntax)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Move type to namespace scope",
                createChangedDocument: ct => MoveToNamespaceScopeAsync(context.Document, nestedType, ct),
                equivalenceKey: "CB0003_MoveToNamespace"),
            diagnostic);
    }

    private static async Task<Document> MoveToNamespaceScopeAsync(
        Document document,
        TypeDeclarationSyntax nestedType,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || nestedType.Parent is not TypeDeclarationSyntax containingType)
        {
            return document;
        }

        // Remove the nested type from its container.
        var newContainingType = containingType.RemoveNode(nestedType, SyntaxRemoveOptions.KeepNoTrivia)!;

        // Determine where to insert: right after the containing type at the
        // same parent scope. The parent could be a namespace or the compilation unit.
        var containingTypeParent = containingType.Parent;
        if (containingTypeParent is null)
        {
            return document;
        }

        // Build the new root: replace the old containing type with the trimmed
        // one, then insert the extracted type after it.
        var newRoot = root.ReplaceNode(containingType, newContainingType);

        // Re-find the containing type in the new tree (the reference changed after ReplaceNode).
        var updatedContainingType = newRoot.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == containingType.Identifier.Text);

        if (updatedContainingType is null)
        {
            return document;
        }

        // Add leading newlines so the extracted type doesn't run into the
        // containing type on the same line.
        var extractedType = nestedType.WithLeadingTrivia(
            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.CarriageReturnLineFeed,
            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.CarriageReturnLineFeed);

        newRoot = newRoot.InsertNodesAfter(updatedContainingType, new[] { extractedType });

        return document.WithSyntaxRoot(newRoot);
    }
}
