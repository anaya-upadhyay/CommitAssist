using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommitAssist.Models;
using CommitAssist.Services;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace CommitAssist.ML
{
    // ML.NET pipeline:
    //   PrimaryLayer (string) → FeaturizeText (bag-of-chars on a single token)
    //   Numeric columns       → Concatenate → NormalizeMinMax
    //   All                   → Concatenate → Features
    //   SdcaMaximumEntropy multiclass trainer
    //   MapKeyToValue         → PredictedLabel
    public static class ModelTrainer
    {
        private static readonly string[] NumericColumns =
        {
            nameof(DiffFeatures.AddedMethodCount),
            nameof(DiffFeatures.RemovedMethodCount),
            nameof(DiffFeatures.ModifiedMethodCount),
            nameof(DiffFeatures.TestFileRatio),
            nameof(DiffFeatures.LineDeltaRatio),
            nameof(DiffFeatures.TotalLinesChanged),
            nameof(DiffFeatures.FileCount),
            nameof(DiffFeatures.ScopeSpread),
            nameof(DiffFeatures.HasMigration),
            nameof(DiffFeatures.HasCsprojChange),
            nameof(DiffFeatures.HasDocChange),
            nameof(DiffFeatures.IsOnlyTests)
        };

        public static TrainingResult Train(
            IEnumerable<TrainingSample> samples,
            string modelPath,
            float testFraction = 0.15f)
        {
            var mlContext = new MLContext(seed: 42);

            var diffFeatures = samples.Select(s => new DiffFeatures
            {
                PrimaryLayer        = s.PrimaryLayer,
                AddedMethodCount    = s.AddedMethodCount,
                RemovedMethodCount  = s.RemovedMethodCount,
                ModifiedMethodCount = s.ModifiedMethodCount,
                TestFileRatio       = s.TestFileRatio,
                LineDeltaRatio      = s.LineDeltaRatio,
                TotalLinesChanged   = s.TotalLinesChanged,
                FileCount           = s.FileCount,
                ScopeSpread         = s.ScopeSpread,
                HasMigration        = s.HasMigration  ? 1f : 0f,
                HasCsprojChange     = s.HasCsprojChange ? 1f : 0f,
                HasDocChange        = s.HasDocChange  ? 1f : 0f,
                IsOnlyTests         = s.IsOnlyTests   ? 1f : 0f,
                CommitType          = s.CommitType
            }).ToList();

            var data  = mlContext.Data.LoadFromEnumerable(diffFeatures);
            var split = mlContext.Data.TrainTestSplit(data, testFraction: testFraction);

            var pipeline = BuildPipeline(mlContext);
            var model    = pipeline.Fit(split.TrainSet);

            var predictions = model.Transform(split.TestSet);
            var metrics     = mlContext.MulticlassClassification.Evaluate(predictions,
                                  labelColumnName: "Label",
                                  predictedLabelColumnName: "PredictedLabel");

            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            mlContext.Model.Save(model, data.Schema, modelPath);

            return new TrainingResult
            {
                SampleCount           = diffFeatures.Count,
                MicroAccuracy         = (float)metrics.MicroAccuracy,
                MacroAccuracy         = (float)metrics.MacroAccuracy,
                LogLossReduction      = (float)metrics.LogLossReduction
            };
        }

        private static IEstimator<ITransformer> BuildPipeline(MLContext mlContext)
        {
            var pipeline = mlContext.Transforms.Conversion
                .MapValueToKey("Label", nameof(DiffFeatures.CommitType))

                .Append(mlContext.Transforms.Text.FeaturizeText(
                    "PrimaryLayerFeaturized",
                    nameof(DiffFeatures.PrimaryLayer)))

                // Concatenate numerics into a single vector then normalise
                .Append(mlContext.Transforms.Concatenate("NumericFeatures",
                    NumericColumns))
                .Append(mlContext.Transforms.NormalizeMinMax("NumericFeatures"))

                .Append(mlContext.Transforms.Concatenate("Features",
                    "PrimaryLayerFeaturized", "NumericFeatures"))

                // SDCA handles small and imbalanced datasets better than most alternatives
                .Append(mlContext.MulticlassClassification.Trainers
                    .SdcaMaximumEntropy(
                        labelColumnName: "Label",
                        featureColumnName: "Features"))

                .Append(mlContext.Transforms.Conversion
                    .MapKeyToValue("PredictedLabel"));

            return pipeline;
        }
    }

    public sealed class TrainingResult
    {
        public int   SampleCount      { get; init; }
        public float MicroAccuracy    { get; init; }
        public float MacroAccuracy    { get; init; }
        public float LogLossReduction { get; init; }
        public string Summary =>
            $"Trained on {SampleCount} samples. " +
            $"Micro-accuracy: {MicroAccuracy:P1}, Macro-accuracy: {MacroAccuracy:P1}";
    }

    // Loads a saved model and runs inference.
    // Falls back to rule-based heuristics if no model file exists yet —
    // so the extension is useful from day one, before any training has run.
    public sealed class CommitTypePredictor
    {
        private readonly MLContext _mlContext = new(seed: 42);
        private PredictionEngine<DiffFeatures, CommitTypePrediction>? _engine;

        public static readonly string[] KnownTypes =
            { "feat", "fix", "refactor", "chore", "test", "docs", "style", "perf", "ci", "build" };

        public bool TryLoadModel(string modelPath)
        {
            if (!File.Exists(modelPath)) return false;

            try
            {
                var model = _mlContext.Model.Load(modelPath, out _);
                _engine = _mlContext.Model
                    .CreatePredictionEngine<DiffFeatures, CommitTypePrediction>(model);
                return true;
            }
            catch
            {
                _engine = null;
                return false;
            }
        }

        public CommitTypePrediction Predict(EnrichedDiffContext context)
        {
            var features = context.Features;

            if (_engine is not null)
                return _engine.Predict(features);

            return RuleBasedFallback(features);
        }

        private static CommitTypePrediction RuleBasedFallback(DiffFeatures f)
        {
            string type;

            if (f.IsOnlyTests >= 1f)
                type = "test";
            else if (f.HasMigration >= 1f)
                type = "chore";
            else if (f.HasCsprojChange >= 1f && f.AddedMethodCount == 0)
                type = "build";
            else if (f.HasDocChange >= 1f && f.AddedMethodCount == 0)
                type = "docs";
            else if (f.RemovedMethodCount > f.AddedMethodCount && f.AddedMethodCount > 0)
                type = "refactor";
            else if (f.AddedMethodCount > 0 && f.RemovedMethodCount == 0)
                type = "feat";
            else if (f.ModifiedMethodCount > 0 && f.AddedMethodCount == 0)
                type = "fix";
            else if (f.TotalLinesChanged < 20)
                type = "fix";
            else
                type = "chore";

            // Mock score: 1.0 on winning slot, 0 elsewhere
            var scores = new float[KnownTypes.Length];
            int idx = Array.IndexOf(KnownTypes, type);
            if (idx >= 0) scores[idx] = 1.0f;

            return new CommitTypePrediction { PredictedType = type, Score = scores };
        }
    }
}
