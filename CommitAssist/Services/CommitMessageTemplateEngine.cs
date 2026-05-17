using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CommitAssist.ML;
using CommitAssist.Models;

namespace CommitAssist.Services
{
    // ML model decides the commit TYPE. Roslyn decides the symbol NAMES.
    // This class just assembles them into three message variants.
    public sealed class CommitMessageTemplateEngine
    {
        private const int ScopeSpreadWarningThreshold = 4;

        public CommitSuggestionSet GenerateSuggestions(
            EnrichedDiffContext context,
            CommitTypePrediction prediction)
        {
            string type          = prediction.PredictedType;
            float  confidence    = GetTopScore(prediction);
            string scope         = DeriveScope(context);
            string primaryAction = DeriveSubjectVerb(context, type);

            bool scopeWarning = context.Features.ScopeSpread >= ScopeSpreadWarningThreshold;

            var suggestions = new List<CommitSuggestion>
            {
                BuildConcise(type, scope, primaryAction, confidence),
                BuildDetailed(type, scope, primaryAction, confidence, context),
                BuildTicketLinked(type, scope, primaryAction, confidence, context)
            };

            return new CommitSuggestionSet
            {
                Suggestions  = suggestions,
                ScopeWarning = scopeWarning,
                ScopeWarningMessage = scopeWarning
                    ? $"This diff touches {context.Features.ScopeSpread} directories. " +
                       "Consider splitting into smaller, focused commits."
                    : null
            };
        }

        private static CommitSuggestion BuildConcise(
            string type, string scope, string action, float confidence)
        {
            return new CommitSuggestion
            {
                Subject    = FormatSubject(type, scope, action),
                Confidence = confidence,
                StyleLabel = "Concise"
            };
        }

        private static CommitSuggestion BuildDetailed(
            string type, string scope, string action, float confidence,
            EnrichedDiffContext ctx)
        {
            return new CommitSuggestion
            {
                Subject    = FormatSubject(type, scope, action),
                Body       = BuildBody(ctx),
                Confidence = confidence,
                StyleLabel = "Detailed"
            };
        }

        private static CommitSuggestion BuildTicketLinked(
            string type, string scope, string action, float confidence,
            EnrichedDiffContext ctx)
        {
            return new CommitSuggestion
            {
                Subject    = FormatSubject(type, scope, action),
                Body       = BuildBody(ctx),
                Footer     = "Refs: #",
                Confidence = confidence,
                StyleLabel = "Ticket-linked"
            };
        }

        private static string FormatSubject(string type, string scope, string action)
        {
            return string.IsNullOrEmpty(scope)
                ? $"{type}: {action}"
                : $"{type}({scope}): {action}";
        }

        private static string BuildBody(EnrichedDiffContext ctx)
        {
            var sb     = new StringBuilder();
            var sym    = ctx.Symbols;
            var parsed = ctx.ParsedSummary;

            foreach (string m in sym.AddedMethods.Take(5))
                sb.AppendLine($"- add {m}");
            foreach (string c in sym.AddedClasses.Take(3))
                sb.AppendLine($"- add {c} class");
            foreach (string p in sym.AddedProperties.Take(3))
                sb.AppendLine($"- add {p} property");

            foreach (string m in sym.RemovedMethods.Take(3))
                sb.AppendLine($"- remove {m}");
            foreach (string c in sym.RemovedClasses.Take(2))
                sb.AppendLine($"- remove {c} class");

            foreach (string m in sym.ModifiedMethods.Take(3))
                sb.AppendLine($"- update {m}");

            if (parsed.HasMigration)    sb.AppendLine("- add database migration");
            if (parsed.HasCsprojChange) sb.AppendLine("- update project references");
            if (parsed.HasDocChange)    sb.AppendLine("- update documentation");

            sb.AppendLine();
            sb.Append($"{parsed.TotalLinesAdded} insertions(+), " +
                      $"{parsed.TotalLinesRemoved} deletions(-) " +
                      $"across {parsed.FileCount} file(s)");

            return sb.ToString().TrimEnd();
        }

        private static string DeriveScope(EnrichedDiffContext ctx)
        {
            if (!string.IsNullOrEmpty(ctx.Symbols.DetectedLayer) &&
                ctx.Symbols.DetectedLayer != "Other")
                return ctx.Symbols.DetectedLayer;

            if (!string.IsNullOrEmpty(ctx.ParsedSummary.PrimaryLayer) &&
                ctx.ParsedSummary.PrimaryLayer != "Other")
                return ctx.ParsedSummary.PrimaryLayer;

            return string.Empty;
        }

        private static string DeriveSubjectVerb(EnrichedDiffContext ctx, string type)
        {
            var sym = ctx.Symbols;

            string? primarySymbol =
                sym.AddedMethods.FirstOrDefault() ??
                sym.AddedClasses.FirstOrDefault() ??
                sym.ModifiedMethods.FirstOrDefault() ??
                sym.RemovedMethods.FirstOrDefault();

            if (primarySymbol is not null)
            {
                string verb = type switch
                {
                    "feat"     => "add",
                    "fix"      => "fix",
                    "refactor" => "refactor",
                    "test"     => "add tests for",
                    "docs"     => "document",
                    "chore"    => "update",
                    "perf"     => "optimise",
                    _          => "update"
                };
                return $"{verb} {FormatSymbolName(primarySymbol)}";
            }

            var parsed = ctx.ParsedSummary;
            if (parsed.HasMigration)    return "add database migration";
            if (parsed.HasCsprojChange) return "update project dependencies";
            if (parsed.HasDocChange)    return "update documentation";

            return type switch
            {
                "feat"     => "add new functionality",
                "fix"      => "fix bug",
                "refactor" => "refactor code",
                "test"     => "add tests",
                "docs"     => "update docs",
                "chore"    => "update configuration",
                _          => "update code"
            };
        }

        private static string FormatSymbolName(string name)
        {
            // .ctor(ClassName) → ClassName
            return name.StartsWith(".ctor(")
                ? name.Substring(6).TrimEnd(')')
                : name;
        }

        private static float GetTopScore(CommitTypePrediction prediction)
        {
            if (prediction.Score.Length == 0) return 0.5f;
            float max = 0f;
            foreach (float s in prediction.Score)
                if (s > max) max = s;
            return max;
        }
    }
}
