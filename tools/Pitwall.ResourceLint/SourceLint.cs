using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Pitwall.ResourceLint;

public sealed record Violation(string File, int Line, string Code);
public static class SourceLint
{
    private static readonly HashSet<string> UserFacingMembers = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Title", "Header", "PlaceholderText", "ToolTip", "Description", "Message", "HelpText", "Label", "AutomationName"
    };

    public static IReadOnlyList<Violation> Scan(string root)
    {
        var violations = new List<Violation>();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (Path.GetExtension(file) == ".cs") ScanCSharp(file, violations);
            else if (Path.GetExtension(file) == ".xaml") ScanXaml(file, violations);
        }
        return violations;
    }

    private static void ScanCSharp(string file, List<Violation> violations)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
        var syntax = tree.GetRoot();
        if (tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        { violations.Add(new(file, 1, "L10N_PARSE")); return; }
        var initializers = syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(variable => variable.Initializer is not null).GroupBy(variable => variable.Identifier.ValueText)
            .ToDictionary(group => group.Key, group => group.Select(variable => variable.Initializer!.Value).ToArray());
        foreach (var assignment in syntax.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            if (UserFacingMembers.Contains(MemberName(assignment.Left)) && HasLiteral(assignment.Right, initializers, [])) Add(assignment);
        foreach (var property in syntax.DescendantNodes().OfType<PropertyDeclarationSyntax>().Where(property => UserFacingMembers.Contains(property.Identifier.ValueText)))
        {
            if (property.Initializer is not null && HasLiteral(property.Initializer.Value, initializers, [])) Add(property.Initializer);
            if (property.ExpressionBody is not null && HasLiteral(property.ExpressionBody.Expression, initializers, [])) Add(property.ExpressionBody);
            foreach (var returned in property.DescendantNodes().OfType<ReturnStatementSyntax>())
                if (returned.Expression is not null && HasLiteral(returned.Expression, initializers, [])) Add(returned);
        }
        foreach (var argument in syntax.DescendantNodes().OfType<ArgumentSyntax>())
            if (argument.NameColon is not null && UserFacingMembers.Any(member => string.Equals(member, argument.NameColon.Name.Identifier.ValueText, StringComparison.OrdinalIgnoreCase)) && HasLiteral(argument.Expression, initializers, [])) Add(argument);
        foreach (var invocation in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = invocation.Expression.ToString();
            if (name is "Console.Write" or "Console.WriteLine" or "MessageBox.Show" or "AutomationProperties.SetName" or "AutomationProperties.SetHelpText" or "ToolTipService.SetToolTip")
                foreach (var argument in invocation.ArgumentList.Arguments)
                    if (HasLiteral(argument.Expression, initializers, [])) Add(argument);
        }
        void Add(SyntaxNode node) => violations.Add(new(file, tree.GetLineSpan(node.Span).StartLinePosition.Line + 1, "L10N_LITERAL"));
    }

    private static string MemberName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => ""
    };
    private static bool HasLiteral(ExpressionSyntax expression, Dictionary<string, ExpressionSyntax[]> initializers, HashSet<string> seen)
    {
        if (expression is InvocationExpressionSyntax invocation && invocation.Expression.ToString() == "ResourceCatalog.GetRequired") return false;
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)) return !string.IsNullOrWhiteSpace(literal.Token.ValueText);
        if (expression is InterpolatedStringExpressionSyntax interpolated && interpolated.Contents.OfType<InterpolatedStringTextSyntax>().Any(text => !string.IsNullOrWhiteSpace(text.TextToken.ValueText))) return true;
        if (expression is IdentifierNameSyntax identifier && seen.Add(identifier.Identifier.ValueText) && initializers.TryGetValue(identifier.Identifier.ValueText, out var values))
            return values.Any(value => HasLiteral(value, initializers, seen));
        return expression.ChildNodes().OfType<ExpressionSyntax>().Any(child => HasLiteral(child, initializers, seen));
    }

    private static void ScanXaml(string file, List<Violation> violations)
    {
        try
        {
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    var member = attribute.Name.LocalName.Split('.').Last();
                    if ((UserFacingMembers.Contains(member) || attribute.Name.LocalName is "AutomationProperties.Name" or "AutomationProperties.HelpText") && IsLiteralText(attribute.Value)) Add(attribute);
                }
                foreach (var text in element.Nodes().OfType<XText>()) if (IsLiteralText(text.Value)) Add(text);
            }
        }
        catch (System.Xml.XmlException) { violations.Add(new(file, 1, "L10N_PARSE")); }
        void Add(XObject node) => violations.Add(new(file, ((System.Xml.IXmlLineInfo)node).LineNumber, "L10N_LITERAL"));
    }
    private static bool IsLiteralText(string value) => !string.IsNullOrWhiteSpace(value) &&
        !(value.StartsWith("{Binding ", StringComparison.Ordinal) || value.StartsWith("{x:Bind ", StringComparison.Ordinal) ||
          value.StartsWith("{StaticResource ", StringComparison.Ordinal) || value.StartsWith("{ThemeResource ", StringComparison.Ordinal));
}

public static class LintCli
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length is < 1 or > 2 || !Directory.Exists(args[0])) return 2;
        var violations = SourceLint.Scan(Path.GetFullPath(args[0]));
        var report = JsonSerializer.Serialize(new { passed = violations.Count == 0, violations }, new JsonSerializerOptions { WriteIndented = true });
        if (args.Length == 2) await File.WriteAllTextAsync(args[1], report, cancellationToken);
        else await Console.Out.WriteLineAsync(report.AsMemory(), cancellationToken);
        return violations.Count == 0 ? 0 : 1;
    }
}
