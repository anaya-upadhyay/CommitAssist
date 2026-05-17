using System.Collections.Generic;
using System.Threading.Tasks;
using CommitAssist.ML;
using CommitAssist.Models;
using CommitAssist.Services;
using Microsoft.ML.Data;

namespace CommitAssist.Services
{
    public sealed class FeatureBuilderService
    {
        private readonly DiffParserService     _parser;
        private readonly RoslynAnalyserService _roslyn;

        public FeatureBuilderService(DiffParserService parser, RoslynAnalyserService roslyn)
        {
            _parser = parser;
            _roslyn = roslyn;
        }

        public Task<EnrichedDiffContext> BuildFeaturesAsync(IReadOnlyList<StagedFileDiff> stagedDiffs)
        {
            // Both services are CPU-bound but fast; Task.Run keeps the API
            // async-friendly without forcing callers onto the thread pool themselves.
            return Task.Run(() =>
            {
                var parsed  = _parser.Parse(stagedDiffs);
                var symbols = _roslyn.Analyse(parsed.AddedLinesByCsFile);

                int  total  = parsed.TotalLinesAdded + parsed.TotalLinesRemoved;
                float deltaR = total > 0 ? (float)parsed.TotalLinesAdded / total : 0.5f;

                var features = new DiffFeatures
                {
                    PrimaryLayer       = string.IsNullOrEmpty(symbols.DetectedLayer)
                                             ? parsed.PrimaryLayer
                                             : symbols.DetectedLayer,
                    AddedMethodCount   = symbols.AddedMethods.Count,
                    RemovedMethodCount = symbols.RemovedMethods.Count,
                    ModifiedMethodCount= symbols.ModifiedMethods.Count,
                    TestFileRatio      = parsed.TestFileRatio,
                    LineDeltaRatio     = deltaR,
                    TotalLinesChanged  = total,
                    FileCount          = parsed.FileCount,
                    ScopeSpread        = parsed.ScopeSpread,
                    HasMigration       = parsed.HasMigration  ? 1f : 0f,
                    HasCsprojChange    = parsed.HasCsprojChange ? 1f : 0f,
                    HasDocChange       = parsed.HasDocChange  ? 1f : 0f,
                    IsOnlyTests        = parsed.TestFileRatio >= 1.0f ? 1f : 0f
                };

                return new EnrichedDiffContext
                {
                    Features       = features,
                    Symbols        = symbols,
                    ParsedSummary  = parsed
                };
            });
        }
    }
}

namespace CommitAssist.ML
{
    // All inputs are numeric except PrimaryLayer, which gets one-hot encoded
    // via FeaturizeText on a single-token string before training.
    public sealed class DiffFeatures
    {
        [LoadColumn(0)]
        public string PrimaryLayer { get; set; } = "Other";

        [LoadColumn(1)]  public float AddedMethodCount    { get; set; }
        [LoadColumn(2)]  public float RemovedMethodCount  { get; set; }
        [LoadColumn(3)]  public float ModifiedMethodCount { get; set; }
        [LoadColumn(4)]  public float TestFileRatio       { get; set; }
        [LoadColumn(5)]  public float LineDeltaRatio      { get; set; }
        [LoadColumn(6)]  public float TotalLinesChanged   { get; set; }
        [LoadColumn(7)]  public float FileCount           { get; set; }
        [LoadColumn(8)]  public float ScopeSpread         { get; set; }
        [LoadColumn(9)]  public float HasMigration        { get; set; }
        [LoadColumn(10)] public float HasCsprojChange     { get; set; }
        [LoadColumn(11)] public float HasDocChange        { get; set; }
        [LoadColumn(12)] public float IsOnlyTests         { get; set; }

        // Label present during training, ignored during inference
        [LoadColumn(13)]
        public string CommitType { get; set; } = string.Empty;
    }

    public sealed class CommitTypePrediction
    {
        [ColumnName("PredictedLabel")]
        public string PredictedType { get; set; } = string.Empty;

        public float[] Score { get; set; } = System.Array.Empty<float>();
    }
}

namespace CommitAssist.Services
{
    public sealed class EnrichedDiffContext
    {
        public CommitAssist.ML.DiffFeatures     Features      { get; init; } = null!;
        public CommitAssist.Models.SymbolChangeSummary Symbols { get; init; } = null!;
        public ParsedDiffSummary                ParsedSummary { get; init; } = null!;
    }
}
