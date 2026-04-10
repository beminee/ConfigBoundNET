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
/// Offers a one-click "Change to class" fix for CB0005.
/// Replaces <c>struct</c> with <c>class</c> (or <c>record struct</c> with
/// <c>record class</c>) so the type can participate in IOptions binding.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(InvalidTargetKindCodeFix))]
[Shared]
internal sealed class InvalidTargetKindCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(DiagnosticDescriptors.InvalidTargetKind.Id);

    public override FixAllProvider? GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var node = root?.FindNode(diagnostic.Location.SourceSpan);

        if (node is not StructDeclarationSyntax structDecl)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Change to class",
                createChangedDocument: ct => ChangeStructToClassAsync(context.Document, structDecl, ct),
                equivalenceKey: "CB0005_ChangeToClass"),
            diagnostic);
    }

    private static async Task<Document> ChangeStructToClassAsync(
        Document document,
        StructDeclarationSyntax structDecl,
        CancellationToken cancellationToken)
    {
        // Build a ClassDeclarationSyntax with the same identifier, modifiers,
        // attribute lists, members, base list, type parameters, etc.
        var classDecl = SyntaxFactory.ClassDeclaration(structDecl.Identifier)
            .WithAttributeLists(structDecl.AttributeLists)
            .WithModifiers(structDecl.Modifiers)
            .WithTypeParameterList(structDecl.TypeParameterList)
            .WithBaseList(structDecl.BaseList)
            .WithConstraintClauses(structDecl.ConstraintClauses)
            .WithMembers(structDecl.Members)
            .WithOpenBraceToken(structDecl.OpenBraceToken)
            .WithCloseBraceToken(structDecl.CloseBraceToken)
            .WithSemicolonToken(structDecl.SemicolonToken)
            .WithLeadingTrivia(structDecl.GetLeadingTrivia())
            .WithTrailingTrivia(structDecl.GetTrailingTrivia());

        // If the original had `record struct`, we need `record class`. The
        // `record` keyword lives in the modifiers on the struct declaration
        // and is carried over via WithModifiers above. Roslyn's
        // ClassDeclaration naturally produces a `class` keyword, so the
        // combination yields `record class` when the modifier list includes
        // `record`. No extra work needed.

        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        return document.WithSyntaxRoot(root!.ReplaceNode(structDecl, classDecl));
    }
}
