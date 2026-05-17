using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CommitAssist.Models;

namespace CommitAssist.Services
{
    // Turns raw unified-diff patch strings into structural signals.
    // Pure string processing — no git process or file I/O involved.
    public sealed class DiffParserService
    {
        private static readonly (string Pattern, string Layer)[] LayerPatterns =
        {
            (@"[/\\]Controllers?[/\\]",        "Controller"),
            (@"[/\\]Services?[/\\]",           "Service"),
            (@"[/\\]Repositor(y|ies)[/\\]",    "Repository"),
            (@"[/\\]Data[/\\]|Migrations?[/\\]","Data"),
            (@"[/\\]Models?[/\\]",             "Model"),
            (@"[/\\]ViewModels?[/\\]",         "ViewModel"),
            (@"[/\\]Views?[/\\]",              "View"),
            (@"[/\\]Middleware[/\\]",          "Middleware"),
            (@"[/\\]Handlers?[/\\]",           "Handler"),
            (@"[/\\]Extensions?[/\\]",         "Extension"),
            (@"[/\\]Helpers?[/\\]",            "Helper"),
        };

        public ParsedDiffSummary Parse(IReadOnlyList<StagedFileDiff> stagedDiffs)
        {
            var summary = new ParsedDiffSummary();

            foreach (var diff in stagedDiffs)
            {
                string ext = Path.GetExtension(diff.FilePath).ToLowerInvariant();

                summary.TotalLinesAdded   += diff.LinesAdded;
                summary.TotalLinesRemoved += diff.LinesRemoved;
                summary.FileCount++;

                if (IsTestFile(diff.FilePath))        summary.TestFileCount++;
                if (IsMigrationFile(diff.FilePath))   summary.HasMigration = true;
                if (IsCsprojFile(diff.FilePath))      summary.HasCsprojChange = true;
                if (IsDocumentationFile(diff.FilePath)) summary.HasDocChange = true;

                string layer = DetectLayer(diff.FilePath);
                if (!string.IsNullOrEmpty(layer))
                    summary.LayerFrequency[layer] = summary.LayerFrequency.GetValueOrDefault(layer) + 1;

                string dir = Path.GetDirectoryName(diff.FilePath)?.Replace('\\', '/') ?? "";
                if (!string.IsNullOrEmpty(dir))
                    summary.TouchedDirectories.Add(dir);

                if (ext == ".cs")
                    summary.AddedLinesByCsFile[diff.FilePath] = ExtractAddedLines(diff.PatchText);
            }

            summary.PrimaryLayer = summary.LayerFrequency.Count > 0
                ? summary.LayerFrequency.OrderByDescending(kv => kv.Value).First().Key
                : "Unknown";

            summary.TestFileRatio = summary.FileCount > 0
                ? (float)summary.TestFileCount / summary.FileCount
                : 0f;

            summary.ScopeSpread = summary.TouchedDirectories.Count;

            return summary;
        }

        // Strips context lines and the +++ header; returns only the added content lines.
        public static IReadOnlyList<string> ExtractAddedLines(string patchText)
        {
            var lines = new List<string>();
            foreach (string line in patchText.Split('\n'))
            {
                if (line.StartsWith("+++", StringComparison.Ordinal)) continue;
                if (line.StartsWith("+",  StringComparison.Ordinal))
                    lines.Add(line.Substring(1));
            }
            return lines;
        }

        public static bool IsTestFile(string path) =>
            Regex.IsMatch(path, @"[/\\]Tests?[/\\]|\.Tests?\.", RegexOptions.IgnoreCase) ||
            path.EndsWith("Tests.cs",   StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("Spec.cs",    StringComparison.OrdinalIgnoreCase);

        public static bool IsMigrationFile(string path) =>
            Regex.IsMatch(path, @"Migrations?[/\\]|_Migration|\.migr", RegexOptions.IgnoreCase);

        public static bool IsCsprojFile(string path) =>
            path.EndsWith(".csproj",   StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".sln",      StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("packages.config", StringComparison.OrdinalIgnoreCase);

        public static bool IsDocumentationFile(string path) =>
            path.EndsWith(".md",   StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".txt",  StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".xml",  StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(path,    @"docs?[/\\]", RegexOptions.IgnoreCase);

        public static string DetectLayer(string path)
        {
            foreach (var (pattern, layer) in LayerPatterns)
                if (Regex.IsMatch(path, pattern, RegexOptions.IgnoreCase))
                    return layer;
            return "Other";
        }
    }

    public sealed class ParsedDiffSummary
    {
        public int   TotalLinesAdded   { get; set; }
        public int   TotalLinesRemoved { get; set; }
        public int   FileCount         { get; set; }
        public int   TestFileCount     { get; set; }
        public bool  HasMigration      { get; set; }
        public bool  HasCsprojChange   { get; set; }
        public bool  HasDocChange      { get; set; }
        public string PrimaryLayer     { get; set; } = string.Empty;
        public float  TestFileRatio    { get; set; }
        public int    ScopeSpread      { get; set; }

        public Dictionary<string, int>              LayerFrequency     { get; } = new();
        public HashSet<string>                       TouchedDirectories { get; } = new();
        public Dictionary<string, IReadOnlyList<string>> AddedLinesByCsFile { get; } = new();
    }
}
