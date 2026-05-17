using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommitAssist.Commands;
using CommitAssist.Integration;
using CommitAssist.ML;
using CommitAssist.Models;
using CommitAssist.Services;
using CommitAssist.UI;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace CommitAssist
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideToolWindow(
        typeof(CommitMessageToolWindow),
        Style       = VsDockStyle.Tabbed,
        Orientation = ToolWindowOrientation.Right,
        Window      = EnvDTE.Constants.vsWindowKindSolutionExplorer)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionOpening_string,
                     PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuidString)]
    public sealed partial class CommitAssistPackage : AsyncPackage
    {
        public const string PackageGuidString    = "A1B2C3D4-E5F6-7890-ABCD-EF1234567890";
        public const string CommandSetGuidString = "E5F6A7B8-C9D0-1234-EF01-345678901234";

        internal GitService?                 GitService                { get; private set; }
        internal DiffParserService           DiffParserService         { get; private set; } = null!;
        internal RoslynAnalyserService       RoslynAnalyserService     { get; private set; } = null!;
        internal FeatureBuilderService       FeatureBuilderService     { get; private set; } = null!;
        internal CommitTypePredictor         CommitTypePredictor       { get; private set; } = null!;
        internal CommitMessageTemplateEngine TemplateEngine            { get; private set; } = null!;
        internal BackgroundTrainingService   BackgroundTrainingService  { get; private set; } = null!;
        internal GitChangesIntegration?      GitChangesIntegration     { get; private set; }

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            // Expose static accessor so WPF controls can resolve the package
            RegisterInstance();

            // Pure-logic services — no UI thread required
            InitialiseServices();

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            await GenerateCommitMessageCommand.InitialiseAsync(this);

            if (await GetServiceAsync(typeof(IVsSolution)) is IVsSolution solution)
            {
                solution.GetSolutionInfo(out string solutionDir, out _, out _);
                if (!string.IsNullOrEmpty(solutionDir))
                    await OnSolutionOpenedAsync(solutionDir);

                new SolutionEventsSink(this).Advise(solution);
            }
        }

        private void InitialiseServices()
        {
            DiffParserService         = new DiffParserService();
            RoslynAnalyserService     = new RoslynAnalyserService();
            FeatureBuilderService     = new FeatureBuilderService(DiffParserService, RoslynAnalyserService);
            CommitTypePredictor       = new CommitTypePredictor();
            TemplateEngine            = new CommitMessageTemplateEngine();
            BackgroundTrainingService = new BackgroundTrainingService(this);
        }

        internal async Task OnSolutionOpenedAsync(string solutionDirectory)
        {
            await TaskScheduler.Default; // leave UI thread for I/O

            string? repoPath = Services.GitService.DiscoverRepoRoot(solutionDirectory);
            if (repoPath is null) return;

            GitService = new Services.GitService(repoPath);
            BackgroundTrainingService.SetRepositoryPath(repoPath);

            await JoinableTaskFactory.SwitchToMainThreadAsync();
            GitChangesIntegration = new GitChangesIntegration(this);
            GitChangesIntegration.Attach();

            _ = BackgroundTrainingService.RunInitialTrainingAsync();
            _ = BackgroundTrainingService.StartWatchLoopAsync(DisposalToken);
        }

        internal void OnSolutionClosed()
        {
            GitChangesIntegration?.Detach();
            GitChangesIntegration = null;
            GitService = null;
        }

        public async Task<CommitSuggestionSet?> GenerateCommitSuggestionsAsync()
        {
            if (GitService is null) return null;

            IReadOnlyList<StagedFileDiff> stagedDiffs = GitService.GetStagedChanges();
            if (stagedDiffs.Count == 0) return null;

            EnrichedDiffContext  context    = await FeatureBuilderService.BuildFeaturesAsync(stagedDiffs);
            CommitTypePrediction prediction = CommitTypePredictor.Predict(context);
            CommitSuggestionSet  results    = TemplateEngine.GenerateSuggestions(context, prediction);

            return results;
        }
    }
}
