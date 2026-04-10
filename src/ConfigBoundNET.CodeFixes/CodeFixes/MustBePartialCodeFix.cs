// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

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
/// Offers a one-click "Add 'partial' modifier" fix for CB0001.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MustBePartialCodeFix))]
[Shared]
internal sealed class MustBePartialCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(DiagnosticDescriptors.MustBePartial.Id);

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

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Add 'partial' modifier",
                createChangedDocument: ct => AddPartialModifierAsync(context.Document, typeDecl, ct),
                equivalenceKey: "CB0001_AddPartial"),
            diagnostic);
    }

    private static async Task<Document> AddPartialModifierAsync(
        Document document,
        TypeDeclarationSyntax typeDecl,
        CancellationToken cancellationToken)
    {
        // Insert `partial` after any existing access modifiers but before
        // the type keyword. SyntaxTokenList.Add appends, which places
        // `partial` right before the keyword — the idiomatic C# position.
        var partialToken = SyntaxFactory.Token(SyntaxKind.PartialKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);

        var newTypeDecl = typeDecl.AddModifiers(partialToken);

        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        return document.WithSyntaxRoot(root!.ReplaceNode(typeDecl, newTypeDecl));
    }
}
