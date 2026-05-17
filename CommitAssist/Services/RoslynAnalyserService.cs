using System;
using System.Collections.Generic;
using System.Linq;
using CommitAssist.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CommitAssist.Services
{
    // We parse only the *added* lines from the diff, not the full file.
    // The syntax tree ends up incomplete, but that's fine — we only need
    // member names, and Roslyn's tolerant parser handles fragments well.
    public sealed class RoslynAnalyserService
    {
        public SymbolChangeSummary Analyse(
            Dictionary<string, IReadOnlyList<string>> addedLinesByCsFile,
            Dictionary<string, IReadOnlyList<string>>? removedLinesByCsFile = null)
        {
            var addedMethods    = new List<string>();
            var removedMethods  = new List<string>();
            var modifiedMethods = new List<string>();
            var addedClasses    = new List<string>();
            var removedClasses  = new List<string>();
            var addedProperties = new List<string>();
            var namespaces      = new List<string>();
            var layers          = new List<string>();

            foreach (var (filePath, addedLines) in addedLinesByCsFile)
            {
                if (addedLines.Count == 0) continue;

                string addedSource = string.Join("\n", addedLines);
                var addedSymbols   = ExtractSymbols(addedSource);

                IReadOnlyList<string>? removedLines = null;
                removedLinesByCsFile?.TryGetValue(filePath, out removedLines);
                var removedSymbols = removedLines?.Count > 0
                    ? ExtractSymbols(string.Join("\n", removedLines))
                    : new SymbolSet();

                // Present in both added and removed → method was modified, not new
                foreach (string m in addedSymbols.Methods)
                {
                    if (removedSymbols.Methods.Contains(m))
                        modifiedMethods.Add(m);
                    else
                        addedMethods.Add(m);
                }
                foreach (string m in removedSymbols.Methods)
                    if (!addedSymbols.Methods.Contains(m))
                        removedMethods.Add(m);

                addedClasses.AddRange(addedSymbols.Classes);
                removedClasses.AddRange(removedSymbols.Classes.Except(addedSymbols.Classes));
                addedProperties.AddRange(addedSymbols.Properties);

                if (!string.IsNullOrEmpty(addedSymbols.Namespace))
                    namespaces.Add(addedSymbols.Namespace);
            }

            string primaryNamespace = namespaces
                .GroupBy(n => n)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key ?? string.Empty;

            string detectedLayer = InferLayer(primaryNamespace, addedClasses);

            return new SymbolChangeSummary
            {
                AddedMethods     = Deduplicated(addedMethods),
                RemovedMethods   = Deduplicated(removedMethods),
                ModifiedMethods  = Deduplicated(modifiedMethods),
                AddedClasses     = Deduplicated(addedClasses),
                RemovedClasses   = Deduplicated(removedClasses),
                AddedProperties  = Deduplicated(addedProperties),
                PrimaryNamespace = primaryNamespace,
                DetectedLayer    = detectedLayer
            };
        }

        private static SymbolSet ExtractSymbols(string source)
        {
            var tree = CSharpSyntaxTree.ParseText(source,
                CSharpParseOptions.Default.WithKind(SourceCodeKind.Script));

            var root = tree.GetRoot();
            var result = new SymbolSet();

            var nsSyntax = root.DescendantNodes()
                               .OfType<NamespaceDeclarationSyntax>()
                               .FirstOrDefault();
            if (nsSyntax is not null)
                result.Namespace = nsSyntax.Name.ToString();

            var fileScopedNs = root.DescendantNodes()
                                   .OfType<FileScopedNamespaceDeclarationSyntax>()
                                   .FirstOrDefault();
            if (fileScopedNs is not null)
                result.Namespace = fileScopedNs.Name.ToString();

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                result.Classes.Add(typeDecl.Identifier.Text);

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                result.Methods.Add(method.Identifier.Text);

            foreach (var ctor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
                result.Methods.Add($".ctor({ctor.Identifier.Text})");

            foreach (var prop in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
                result.Properties.Add(prop.Identifier.Text);

            return result;
        }

        private static string InferLayer(string ns, IEnumerable<string> classNames)
        {
            string combined = ns + " " + string.Join(" ", classNames);
            if (Matches(combined, "Controller"))  return "Controller";
            if (Matches(combined, "Service"))     return "Service";
            if (Matches(combined, "Repository"))  return "Repository";
            if (Matches(combined, "ViewModel"))   return "ViewModel";
            if (Matches(combined, "Migration"))   return "Data";
            if (Matches(combined, "Middleware"))  return "Middleware";
            if (Matches(combined, "Handler"))     return "Handler";
            return "Other";
        }

        private static bool Matches(string text, string keyword) =>
            text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;

        private static List<string> Deduplicated(IEnumerable<string> items) =>
            items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        private sealed class SymbolSet
        {
            public string       Namespace  { get; set; } = string.Empty;
            public HashSet<string> Classes    { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Methods    { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
