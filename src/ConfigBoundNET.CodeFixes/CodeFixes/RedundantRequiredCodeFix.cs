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
/// Offers a one-click "Remove redundant [Required]" fix for CB0009.
/// Deletes the <c>[Required]</c> attribute from the property, since the
/// property is already validated automatically due to being non-nullable.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RedundantRequiredCodeFix))]
[Shared]
internal sealed class RedundantRequiredCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(DiagnosticDescriptors.RedundantRequired.Id);

    public override FixAllProvider? GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];
        var node = root?.FindNode(diagnostic.Location.SourceSpan);

        // CB0009's location is on the type declaration. The diagnostic message
        // includes the property name as arg {0}. We need to find the property
        // that has [Required] by scanning the type's members.
        if (node is not TypeDeclarationSyntax typeDecl)
        {
            return;
        }

        // Extract the property name from the diagnostic message args.
        // The message format is: "[Required] on property '{0}' is redundant..."
        // We find all properties with [Required] and offer to remove each one.
        foreach (var member in typeDecl.Members)
        {
            if (member is not PropertyDeclarationSyntax property)
            {
                continue;
            }

            var requiredAttr = FindRequiredAttribute(property);
            if (requiredAttr is null)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: $"Remove redundant [Required] from '{property.Identifier.Text}'",
                    createChangedDocument: ct => RemoveRequiredAttributeAsync(
                        context.Document, property, requiredAttr.Value.attrList, requiredAttr.Value.attr, ct),
                    equivalenceKey: $"CB0009_Remove_{property.Identifier.Text}"),
                diagnostic);
        }
    }

    /// <summary>
    /// Finds the <c>[Required]</c> attribute on a property, if present.
    /// Returns both the attribute list and the attribute node so the caller
    /// can decide whether to remove the entire list or just the single attribute.
    /// </summary>
    private static (AttributeListSyntax attrList, AttributeSyntax attr)?
        FindRequiredAttribute(PropertyDeclarationSyntax property)
    {
        foreach (var attrList in property.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name.ToString();
                if (name is "Required" or "RequiredAttribute" or
                    "System.ComponentModel.DataAnnotations.Required" or
                    "System.ComponentModel.DataAnnotations.RequiredAttribute")
                {
                    return (attrList, attr);
                }
            }
        }

        return null;
    }

    private static async Task<Document> RemoveRequiredAttributeAsync(
        Document document,
        PropertyDeclarationSyntax property,
        AttributeListSyntax attrList,
        AttributeSyntax attr,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        SyntaxNode newRoot;

        if (attrList.Attributes.Count == 1)
        {
            // The attribute list contains only [Required] — remove the
            // entire list (brackets and all) to keep the source clean.
            newRoot = root.RemoveNode(attrList, SyntaxRemoveOptions.KeepNoTrivia)!;
        }
        else
        {
            // The list contains other attributes (e.g. [Required, Range(1,10)]).
            // Remove only the Required attribute and keep the rest.
            var newAttrList = attrList.RemoveNode(attr, SyntaxRemoveOptions.KeepNoTrivia)!;
            newRoot = root.ReplaceNode(attrList, newAttrList);
        }

        return document.WithSyntaxRoot(newRoot);
    }
}
