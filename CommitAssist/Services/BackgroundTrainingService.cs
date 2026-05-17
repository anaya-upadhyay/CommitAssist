using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommitAssist.ML;
using CommitAssist.Models;
using CommitAssist.Services;
using Newtonsoft.Json;

namespace CommitAssist.Services
{
    // Two loops:
    //   RunInitialTrainingAsync  — scans the full commit history once and trains
    //                              the initial classifier (skipped if model exists).
    //   StartWatchLoopAsync      — polls for new commits every 3 minutes and
    //                              retrains silently when 50+ new samples accumulate.
    //
    // Everything runs off the UI thread. Training data lives in
    // %LOCALAPPDATA%\CommitAssist\<repo>\ and survives VS restarts.
    public sealed class BackgroundTrainingService
    {
        private const int  IncrementalRetriggerThreshold = 50;  // new samples before retrain
        private const int  PollingIntervalSeconds        = 180;
        private const int  MaxHistoryCommits             = 2000;

        private readonly CommitAssistPackage _package;
        private string?  _repoPath;
        private string?  _lastSeenCommitSha;

        private string DataDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CommitAssist",
            SanitiseRepoName(_repoPath ?? "default"));

        private string SamplesFile => Path.Combine(DataDirectory, "training_samples.ndjson");
        private string ModelFile   => Path.Combine(DataDirectory, "commit_type_model.zip");
        private string MetaFile    => Path.Combine(DataDirectory, "meta.json");

        public BackgroundTrainingService(CommitAssistPackage package)
        {
            _package = package;
        }

        public void SetRepositoryPath(string repoPath)
        {
            _repoPath = repoPath;
            Directory.CreateDirectory(DataDirectory);

            _package.CommitTypePredictor.TryLoadModel(ModelFile);
            _lastSeenCommitSha = LoadMeta()?.LastSeenSha;
        }

        public async Task RunInitialTrainingAsync()
        {
            if (_package.GitService is null) return;

            if (File.Exists(ModelFile) && File.Exists(SamplesFile)) return;

            await Task.Run(() =>
            {
                var samples = ExtractTrainingSamples(MaxHistoryCommits);
                if (samples.Count < 10) return;

                AppendSamples(samples);
                RetryTrain(LoadAllSamples());
            });
        }

        public async Task StartWatchLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(PollingIntervalSeconds), ct);
                    await CheckForNewCommitsAsync();
                }
                catch (TaskCanceledException) { break; }
                catch
                {
                    // Background training must never crash the extension
                }
            }
        }

        private async Task CheckForNewCommitsAsync()
        {
            if (_package.GitService is null) return;

            var newSamples = await Task.Run(() =>
                ExtractSince(_lastSeenCommitSha));

            if (newSamples.Count == 0) return;

            _lastSeenCommitSha = newSamples[0].CommitSha;
            SaveMeta(new MetaData { LastSeenSha = _lastSeenCommitSha });
            AppendSamples(newSamples);

            var all      = LoadAllSamples();
            int buffered = CountUnseen(all);
            if (buffered >= IncrementalRetriggerThreshold)
                RetryTrain(all);
        }

        private List<TrainingSample> ExtractTrainingSamples(int maxCommits)
        {
            var git     = _package.GitService!;
            var parser  = _package.DiffParserService;
            var roslyn  = _package.RoslynAnalyserService;
            var results = new List<TrainingSample>();

            foreach (var (commit, patch) in git.GetConventionalCommits(maxCommits))
            {
                try
                {
                    var (type, scope) = GitService.ParseConventionalPrefix(
                        commit.Message.Split('\n')[0]);

                    if (string.IsNullOrEmpty(type)) continue;

                    var stagedDiffs = PatchToStagedDiffs(patch);
                    var parsed      = parser.Parse(stagedDiffs);
                    var symbols     = roslyn.Analyse(parsed.AddedLinesByCsFile);

                    int   total = parsed.TotalLinesAdded + parsed.TotalLinesRemoved;
                    float dr    = total > 0 ? (float)parsed.TotalLinesAdded / total : 0.5f;

                    results.Add(new TrainingSample
                    {
                        CommitSha           = commit.Sha,
                        CommitType          = type,
                        Scope               = scope,
                        AddedMethodCount    = symbols.AddedMethods.Count,
                        RemovedMethodCount  = symbols.RemovedMethods.Count,
                        ModifiedMethodCount = symbols.ModifiedMethods.Count,
                        TestFileRatio       = parsed.TestFileRatio,
                        LineDeltaRatio      = dr,
                        TotalLinesChanged   = total,
                        FileCount           = parsed.FileCount,
                        ScopeSpread         = parsed.ScopeSpread,
                        HasMigration        = parsed.HasMigration,
                        HasCsprojChange     = parsed.HasCsprojChange,
                        HasDocChange        = parsed.HasDocChange,
                        IsOnlyTests         = parsed.TestFileRatio >= 1.0f,
                        PrimaryLayer        = string.IsNullOrEmpty(symbols.DetectedLayer)
                                                  ? parsed.PrimaryLayer
                                                  : symbols.DetectedLayer
                    });
                }
                catch
                {
                    // Skip malformed commits — one bad entry shouldn't abort the full scan
                }
            }

            return results;
        }

        private List<TrainingSample> ExtractSince(string? lastSha)
        {
            var all = ExtractTrainingSamples(200);
            if (lastSha is null) return all;

            var result = new List<TrainingSample>();
            foreach (var s in all)
            {
                if (s.CommitSha == lastSha) break;
                result.Add(s);
            }
            return result;
        }

        private void RetryTrain(List<TrainingSample> samples)
        {
            if (samples.Count < 10) return;

            try
            {
                var result = ModelTrainer.Train(samples, ModelFile);

                // Hot-swap: write to disk then reload so inference is never blocked
                _package.CommitTypePredictor.TryLoadModel(ModelFile);

                System.Diagnostics.Debug.WriteLine(
                    $"[CommitAssist] {result.Summary}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CommitAssist] Training failed: {ex.Message}");
            }
        }

        private void AppendSamples(IEnumerable<TrainingSample> samples)
        {
            using var writer = new StreamWriter(SamplesFile, append: true);
            foreach (var s in samples)
                writer.WriteLine(JsonConvert.SerializeObject(s));
        }

        private List<TrainingSample> LoadAllSamples()
        {
            if (!File.Exists(SamplesFile)) return new List<TrainingSample>();

            var result = new List<TrainingSample>();
            foreach (string line in File.ReadAllLines(SamplesFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { result.Add(JsonConvert.DeserializeObject<TrainingSample>(line)!); }
                catch { /* malformed line — skip */ }
            }
            return result;
        }

        private static int CountUnseen(List<TrainingSample> all) =>
            all.Count % IncrementalRetriggerThreshold == 0
                ? IncrementalRetriggerThreshold
                : all.Count % IncrementalRetriggerThreshold;

        private MetaData? LoadMeta()
        {
            if (!File.Exists(MetaFile)) return null;
            try { return JsonConvert.DeserializeObject<MetaData>(File.ReadAllText(MetaFile)); }
            catch { return null; }
        }

        private void SaveMeta(MetaData meta) =>
            File.WriteAllText(MetaFile, JsonConvert.SerializeObject(meta));

        private static IReadOnlyList<Models.StagedFileDiff> PatchToStagedDiffs(LibGit2Sharp.Patch patch)
        {
            var list = new List<Models.StagedFileDiff>();
            foreach (var entry in patch)
            {
                list.Add(new Models.StagedFileDiff
                {
                    FilePath     = entry.Path,
                    OldFilePath  = entry.OldPath ?? entry.Path,
                    PatchText    = entry.Patch,
                    LinesAdded   = entry.LinesAdded,
                    LinesRemoved = entry.LinesDeleted,
                    ChangeKind   = Models.FileChangeKind.Modified
                });
            }
            return list;
        }

        private static string SanitiseRepoName(string path) =>
            Path.GetFileName(path.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : "repo";

        private sealed class MetaData
        {
            public string? LastSeenSha { get; set; }
        }
    }
}
