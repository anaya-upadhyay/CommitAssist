using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommitAssist.Models;
using CommitAssist.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommitAssist.Tests
{
    [TestClass]
    public class DiffParserServiceTests
    {
        private readonly DiffParserService _sut = new();

        [TestMethod]
        public void Parse_DetectsControllerLayer()
        {
            var diffs = new List<StagedFileDiff>
            {
                new() { FilePath = "Controllers/UserController.cs", LinesAdded = 10, LinesRemoved = 2, PatchText = "" }
            };
            var result = _sut.Parse(diffs);
            Assert.AreEqual("Controller", result.PrimaryLayer);
        }

        [TestMethod]
        public void Parse_DetectsMigrationFile()
        {
            var diffs = new List<StagedFileDiff>
            {
                new() { FilePath = "Data/Migrations/20240101_AddUserTable.cs", LinesAdded = 30, PatchText = "" }
            };
            var result = _sut.Parse(diffs);
            Assert.IsTrue(result.HasMigration);
        }

        [TestMethod]
        public void Parse_DetectsTestFiles()
        {
            var diffs = new List<StagedFileDiff>
            {
                new() { FilePath = "Tests/UserServiceTests.cs", LinesAdded = 50, PatchText = "" }
            };
            var result = _sut.Parse(diffs);
            Assert.AreEqual(1.0f, result.TestFileRatio, 0.001f);
        }

        [TestMethod]
        public void Parse_ComputesCorrectLineDelta()
        {
            var diffs = new List<StagedFileDiff>
            {
                new() { FilePath = "Services/FooService.cs", LinesAdded = 30, LinesRemoved = 10, PatchText = "" }
            };
            var result = _sut.Parse(diffs);
            Assert.AreEqual(40, result.TotalLinesAdded + result.TotalLinesRemoved);
        }

        [TestMethod]
        public void ExtractAddedLines_OnlyReturnsPlusLines()
        {
            string patch = "--- a/foo.cs\n+++ b/foo.cs\n-removed line\n+added line\n context line\n";
            var lines = DiffParserService.ExtractAddedLines(patch);
            Assert.AreEqual(1, lines.Count);
            Assert.AreEqual("added line", lines[0]);
        }
    }

    [TestClass]
    public class RoslynAnalyserServiceTests
    {
        private readonly RoslynAnalyserService _sut = new();

        [TestMethod]
        public void Analyse_ExtractsAddedMethodNames()
        {
            var addedLines = new List<string>
            {
                "public class UserService {",
                "    public User GetUserById(int id) { return null; }",
                "    public void DeleteUser(int id) { }",
                "}"
            };

            var files = new Dictionary<string, IReadOnlyList<string>>
            {
                ["Services/UserService.cs"] = addedLines
            };

            var result = _sut.Analyse(files);

            CollectionAssert.Contains(result.AddedMethods, "GetUserById");
            CollectionAssert.Contains(result.AddedMethods, "DeleteUser");
        }

        [TestMethod]
        public void Analyse_DetectsServiceLayer()
        {
            var addedLines = new List<string>
            {
                "namespace MyApp.Services { public class OrderService { } }"
            };
            var files = new Dictionary<string, IReadOnlyList<string>>
            {
                ["Services/OrderService.cs"] = addedLines
            };

            var result = _sut.Analyse(files);
            Assert.AreEqual("Service", result.DetectedLayer);
        }

        [TestMethod]
        public void Analyse_ClassifiesModifiedVsNewMethods()
        {
            var added   = new List<string> { "public void ProcessOrder() { }" };
            var removed = new List<string> { "public void ProcessOrder() { }" };

            var addedFiles   = new Dictionary<string, IReadOnlyList<string>> { ["A.cs"] = added };
            var removedFiles = new Dictionary<string, IReadOnlyList<string>> { ["A.cs"] = removed };

            var result = _sut.Analyse(addedFiles, removedFiles);

            CollectionAssert.Contains(result.ModifiedMethods, "ProcessOrder");
            Assert.AreEqual(0, result.AddedMethods.Count);
        }
    }

    [TestClass]
    public class GitServiceTests
    {
        [TestMethod]
        public void IsConventionalCommit_RecognisesValidPrefixes()
        {
            Assert.IsTrue(GitService.IsConventionalCommit("feat: add user login"));
            Assert.IsTrue(GitService.IsConventionalCommit("fix(auth): correct token expiry"));
            Assert.IsTrue(GitService.IsConventionalCommit("chore!: drop support for Node 6"));
            Assert.IsTrue(GitService.IsConventionalCommit("refactor(core): simplify state machine"));
        }

        [TestMethod]
        public void IsConventionalCommit_RejectsFreeformMessages()
        {
            Assert.IsFalse(GitService.IsConventionalCommit("fixed the bug"));
            Assert.IsFalse(GitService.IsConventionalCommit("WIP"));
            Assert.IsFalse(GitService.IsConventionalCommit(""));
        }

        [TestMethod]
        public void ParseConventionalPrefix_ExtractsTypeAndScope()
        {
            var (type, scope) = GitService.ParseConventionalPrefix("feat(UserService): add GetById");
            Assert.AreEqual("feat", type);
            Assert.AreEqual("UserService", scope);
        }

        [TestMethod]
        public void ParseConventionalPrefix_HandlesScopelesPrefix()
        {
            var (type, scope) = GitService.ParseConventionalPrefix("chore: update dependencies");
            Assert.AreEqual("chore", type);
            Assert.AreEqual(string.Empty, scope);
        }
    }

    [TestClass]
    public class CommitMessageTemplateEngineTests
    {
        private readonly CommitMessageTemplateEngine _sut = new();

        [TestMethod]
        public void GenerateSuggestions_ProducesThreeVariants()
        {
            var context = BuildContext("Service", addedMethods: new[] { "GetUserById" });
            var prediction = new ML.CommitTypePrediction { PredictedType = "feat", Score = new float[] { 1f } };

            var result = _sut.GenerateSuggestions(context, prediction);

            Assert.AreEqual(3, result.Suggestions.Count);
        }

        [TestMethod]
        public void GenerateSuggestions_SubjectContainsCommitType()
        {
            var context = BuildContext("Service", addedMethods: new[] { "ProcessPayment" });
            var prediction = new ML.CommitTypePrediction { PredictedType = "feat", Score = new float[] { 1f } };

            var result = _sut.GenerateSuggestions(context, prediction);

            StringAssert.StartsWith(result.Primary.Subject, "feat");
        }

        [TestMethod]
        public void GenerateSuggestions_RaisesScopeWarningOnWideSpread()
        {
            var context = BuildContext("Service", scopeSpread: 5);
            var prediction = new ML.CommitTypePrediction { PredictedType = "chore", Score = new float[] { 1f } };

            var result = _sut.GenerateSuggestions(context, prediction);

            Assert.IsTrue(result.ScopeWarning);
            Assert.IsNotNull(result.ScopeWarningMessage);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static EnrichedDiffContext BuildContext(
            string layer,
            string[]? addedMethods = null,
            int scopeSpread = 1)
        {
            return new EnrichedDiffContext
            {
                Features = new ML.DiffFeatures
                {
                    PrimaryLayer    = layer,
                    AddedMethodCount = addedMethods?.Length ?? 0,
                    ScopeSpread     = scopeSpread
                },
                Symbols = new SymbolChangeSummary
                {
                    DetectedLayer = layer,
                    AddedMethods  = new List<string>(addedMethods ?? System.Array.Empty<string>())
                },
                ParsedSummary = new ParsedDiffSummary
                {
                    PrimaryLayer = layer,
                    FileCount    = 1,
                    ScopeSpread  = scopeSpread
                }
            };
        }
    }

    [TestClass]
    public class CommitTypePredictorTests
    {
        private static EnrichedDiffContext MakeContext(ML.DiffFeatures features) => new()
        {
            Features      = features,
            Symbols       = new SymbolChangeSummary(),
            ParsedSummary = new ParsedDiffSummary()
        };

        [TestMethod]
        public void Predict_FallsBackToRules_WhenNoModelLoaded()
        {
            var predictor = new ML.CommitTypePredictor();
            var result    = predictor.Predict(MakeContext(new ML.DiffFeatures { AddedMethodCount = 1 }));

            Assert.IsNotNull(result.PredictedType);
            CollectionAssert.Contains(ML.CommitTypePredictor.KnownTypes, result.PredictedType);
        }

        [TestMethod]
        public void Predict_ReturnsTest_WhenIsOnlyTests()
        {
            var predictor = new ML.CommitTypePredictor();
            Assert.AreEqual("test",
                predictor.Predict(MakeContext(new ML.DiffFeatures { IsOnlyTests = 1f })).PredictedType);
        }

        [TestMethod]
        public void Predict_ReturnsChore_WhenHasMigration()
        {
            var predictor = new ML.CommitTypePredictor();
            Assert.AreEqual("chore",
                predictor.Predict(MakeContext(new ML.DiffFeatures { HasMigration = 1f })).PredictedType);
        }

        [TestMethod]
        public void Predict_ReturnsFeat_WhenOnlyMethodsAdded()
        {
            var predictor = new ML.CommitTypePredictor();
            Assert.AreEqual("feat",
                predictor.Predict(MakeContext(new ML.DiffFeatures { AddedMethodCount = 3 })).PredictedType);
        }

        [TestMethod]
        public void Predict_ReturnsFix_WhenOnlyMethodsModified()
        {
            var predictor = new ML.CommitTypePredictor();
            Assert.AreEqual("fix",
                predictor.Predict(MakeContext(new ML.DiffFeatures { ModifiedMethodCount = 2 })).PredictedType);
        }

        [TestMethod]
        public void Predict_ScoreLength_MatchesKnownTypesCount()
        {
            var predictor = new ML.CommitTypePredictor();
            var result    = predictor.Predict(MakeContext(new ML.DiffFeatures { HasMigration = 1f }));
            Assert.AreEqual(ML.CommitTypePredictor.KnownTypes.Length, result.Score.Length);
        }
    }

    [TestClass]
    public class ModelTrainerTests
    {
        private static TrainingSample MakeSample(string type,
            float added = 1, float removed = 0,
            bool isTests = false, bool hasMigration = false) => new()
        {
            CommitType          = type,
            PrimaryLayer        = "Service",
            AddedMethodCount    = added,
            RemovedMethodCount  = removed,
            ModifiedMethodCount = 0,
            TestFileRatio       = isTests ? 1f : 0f,
            LineDeltaRatio      = 0.5f,
            TotalLinesChanged   = 20,
            FileCount           = 1,
            ScopeSpread         = 1,
            HasMigration        = hasMigration,
            HasCsprojChange     = false,
            HasDocChange        = false,
            IsOnlyTests         = isTests
        };

        [TestMethod]
        public void Train_SampleCountMatchesInput()
        {
            var samples = Enumerable.Range(0, 5).Select(_ => MakeSample("feat"))
                .Concat(Enumerable.Range(0, 5).Select(_ => MakeSample("fix",  added: 0, removed: 1)))
                .Concat(Enumerable.Range(0, 5).Select(_ => MakeSample("test", isTests: true)))
                .Concat(Enumerable.Range(0, 5).Select(_ => MakeSample("chore", hasMigration: true)))
                .ToList();

            var dir       = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var modelPath = Path.Combine(dir, "model.zip");
            try
            {
                var result = ML.ModelTrainer.Train(samples, modelPath, testFraction: 0.1f);
                Assert.AreEqual(20, result.SampleCount);
                Assert.IsTrue(File.Exists(modelPath));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        [TestMethod]
        public void Train_ProducesLoadableModel()
        {
            var samples = Enumerable.Range(0, 12).Select(i =>
                i % 2 == 0
                    ? MakeSample("feat")
                    : MakeSample("fix", added: 0, removed: 1)).ToList();

            var dir       = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var modelPath = Path.Combine(dir, "model.zip");
            try
            {
                ML.ModelTrainer.Train(samples, modelPath, testFraction: 0.1f);

                var predictor = new ML.CommitTypePredictor();
                Assert.IsTrue(predictor.TryLoadModel(modelPath));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
    }
}
