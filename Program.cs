// XML Documentation to Markdown Converter using Roslyn and System.Xml.Linq
// Usage: Run `dotnet run` from the project's root directory.

using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.SymbolDisplay; // Required for SymbolDisplayFormat

// --- Top-level statements start here ---

// 1. Setup
string projectRoot = Directory.GetCurrentDirectory();
string outputMarkdownFile = Path.Combine(projectRoot, "documentation.md");
var markdownBuilder = new StringBuilder();

markdownBuilder.AppendLine("# Project Documentation");
markdownBuilder.AppendLine($"_Generated on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC_");
markdownBuilder.AppendLine();

try
{
    // 2. Find all C# files in the project directory, excluding build/obj folders.
    Console.WriteLine($"Scanning for .cs files in '{projectRoot}'...");
    var csharpFiles = Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                               .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                                           !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                                           !Path.GetFileName(p).Equals(Path.GetFileName(Environment.GetCommandLineArgs()[0]), StringComparison.OrdinalIgnoreCase));

    if (!csharpFiles.Any())
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("No C# source files found to document.");
        Console.ResetColor();
        return 0;
    }

    // 3. Create Roslyn Compilation
    Console.WriteLine("Creating Roslyn compilation...");
    var syntaxTrees = new List<SyntaxTree>();
    foreach (var filePath in csharpFiles)
    {
        string sourceCode = await File.ReadAllTextAsync(filePath);
        syntaxTrees.Add(CSharpSyntaxTree.ParseText(sourceCode, path: filePath));
    }

    // For .NET 5+ (including .NET 9), many common framework assemblies are implicitly referenced by the SDK.
    // We only need to explicitly add the 'object' assembly location to get started,
    // and Roslyn will typically find other necessary framework references automatically.
    var references = new List<MetadataReference>
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
    };

    CSharpCompilation compilation = CSharpCompilation.Create("DocumentationCompilation")
        .AddSyntaxTrees(syntaxTrees)
        .AddReferences(references);

    // 4. Process each syntax tree in the compilation
    foreach (var tree in syntaxTrees)
    {
        string relativePath = Path.GetRelativePath(projectRoot, tree.FilePath);
        Console.WriteLine($"Processing '{relativePath}'...");

        markdownBuilder.AppendLine($"# File: `{relativePath}`");
        markdownBuilder.AppendLine();

        // Get the semantic model for this tree
        SemanticModel semanticModel = compilation.GetSemanticModel(tree);

        // Find members with documentation
        var membersToProcess = tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(m => m.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)))
            .ToList();

        if (membersToProcess.Any())
        {
            foreach (var member in membersToProcess)
            {
                ProcessMember(member, semanticModel, markdownBuilder);
            }
        }
        else
        {
            markdownBuilder.AppendLine("_No XML comments found in this file._");
            markdownBuilder.AppendLine();
            markdownBuilder.AppendLine("---");
        }
    }

    // 5. Write the consolidated Markdown File
    await File.WriteAllTextAsync(outputMarkdownFile, markdownBuilder.ToString());

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"Successfully generated consolidated Markdown documentation at '{outputMarkdownFile}'");
    Console.ResetColor();
    return 0;
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"An unexpected error occurred: {ex.Message}");
    Console.ResetColor();
    return 1;
}

// --- Helper Methods ---

/// <summary>
/// Processes a single member, gets its documentation via the Semantic Model,
/// and appends it to the Markdown string builder.
/// This version focuses only on <summary>, <param>, and <returns> tags.
/// </summary>
/// <param name="member">The syntax member to process.</param>
/// <param name="semanticModel">The semantic model for the member's syntax tree.</param>
/// <param name="sb">The StringBuilder to append the Markdown to.</param>
void ProcessMember(MemberDeclarationSyntax member, SemanticModel semanticModel, StringBuilder sb)
{
    ISymbol? symbol = semanticModel.GetDeclaredSymbol(member);
    if (symbol == null) return;

    string? xmlDocs = symbol.GetDocumentationCommentXml();
    if (string.IsNullOrEmpty(xmlDocs)) return;

    // --- Convert XML to Markdown using System.Xml.Linq ---
    // Wrap the XML documentation in a dummy root element to make it a valid XML document.
    // This is necessary because GetDocumentationCommentXml() returns a fragment, not a full document.
    XDocument doc;
    try
    {
        doc = XDocument.Parse($"<root>{xmlDocs}</root>");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning: Could not parse XML documentation for member '{symbol.Name}'. Error: {ex.Message}");
        Console.WriteLine($"XML Content: {xmlDocs}");
        return; // Skip this member if XML is malformed
    }

    // Use Roslyn's ToDisplayString for a robust signature
    string memberSignature = symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
    sb.AppendLine($"## `{memberSignature}`");
    sb.AppendLine();

    // <summary> -> Main description
    var summaryElement = doc.Root?.Element("summary");
    if (summaryElement != null)
    {
        sb.AppendLine(ConvertXmlContentToMarkdown(summaryElement));
        sb.AppendLine();
    }

    // <param> -> Parameters table
    var paramElements = doc.Root?.Elements("param");
    if (paramElements != null && paramElements.Any())
    {
        sb.AppendLine("### Parameters");
        sb.AppendLine("| Name | Description |");
        sb.AppendLine("|------|-------------|");
        foreach (var param in paramElements)
        {
            string name = param.Attribute("name")?.Value.Trim() ?? "N/A";
            string description = ConvertXmlContentToMarkdown(param);
            sb.AppendLine($"| `{name}` | {description} |");
        }
        sb.AppendLine();
    }

    // <returns> -> Returns section
    var returnsElement = doc.Root?.Element("returns");
    if (returnsElement != null)
    {
        sb.AppendLine("### Returns");
        sb.AppendLine(ConvertXmlContentToMarkdown(returnsElement));
        sb.AppendLine();
    }

    // Separator for the next member
    sb.AppendLine("---");
}

/// <summary>
/// Converts the content of an XElement (which can include mixed text and child elements like &lt;c&gt;, &lt;code&gt;, &lt;see&gt;)
/// into a Markdown formatted string.
/// </summary>
/// <param name="element">The XElement whose content is to be converted.</param>
/// <returns>A string containing the Markdown representation of the element's content.</returns>
string ConvertXmlContentToMarkdown(XElement element)
{
    var contentBuilder = new StringBuilder();
    foreach (XNode node in element.Nodes())
    {
        if (node is XText textNode)
        {
            // Normalize whitespace: replace multiple spaces/newlines with single space,
            // but preserve explicit paragraph breaks (double newlines).
            string normalizedText = textNode.Value.Replace("\r", "").Replace("\n\n", "\n\n").Replace("\n", " ").Trim();
            contentBuilder.Append(Regex.Replace(normalizedText, @"\s+", " "));
        }
        else if (node is XElement childElement)
        {
            switch (childElement.Name.LocalName.ToLowerInvariant())
            {
                case "c": // Inline code
                    contentBuilder.Append($"`{childElement.Value.Trim()}`");
                    break;
                case "code": // Code block
                    // Heuristic: If the code block contains newlines, treat it as a multi-line block.
                    // Otherwise, treat as inline code to avoid unnecessary block formatting.
                    string codeContent = childElement.Value.Trim();
                    if (codeContent.Contains('\n') || codeContent.Contains('\r'))
                    {
                        contentBuilder.AppendLine(); // Ensure new line before code block
                        contentBuilder.AppendLine("```csharp"); // Assuming C# for now, could be improved
                        contentBuilder.AppendLine(codeContent);
                        contentBuilder.AppendLine("```");
                        contentBuilder.AppendLine(); // Ensure new line after code block
                    }
                    else
                    {
                        contentBuilder.Append($"`{codeContent}`");
                    }
                    break;
                case "see": // Cross-reference
                    string cref = childElement.Attribute("cref")?.Value.Trim() ?? "";
                    string href = childElement.Attribute("href")?.Value.Trim() ?? "";
                    string langword = childElement.Attribute("langword")?.Value.Trim() ?? "";

                    if (!string.IsNullOrEmpty(cref))
                    {
                        contentBuilder.Append($"`{CleanCref(cref)}`"); // Simple display for now
                    }
                    else if (!string.IsNullOrEmpty(href))
                    {
                        contentBuilder.Append($"[{childElement.Value.Trim()}]({href})");
                    }
                    else if (!string.IsNullOrEmpty(langword))
                    {
                        contentBuilder.Append($"`{langword}`");
                    }
                    else
                    {
                        contentBuilder.Append(childElement.Value.Trim()); // Fallback
                    }
                    break;
                // For unknown or unhandled elements, recursively call ConvertXmlContentToMarkdown
                // to process their inner content. This ensures any nested content is still processed.
                default:
                    contentBuilder.Append(ConvertXmlContentToMarkdown(childElement));
                    break;
            }
        }
    }
    // Final trim to remove leading/trailing whitespace from the whole content block
    return contentBuilder.ToString().Trim();
}

/// <summary>
/// Cleans a cref string by removing the Roslyn-specific prefixes (T:, M:, P:, F:).
/// </summary>
/// <param name="cref">The cref string to clean.</param>
/// <returns>A cleaned cref string.</returns>
string CleanCref(string cref)
{
    return cref.Replace("T:", "").Replace("M:", "").Replace("P:", "").Replace("F:", "");
}
