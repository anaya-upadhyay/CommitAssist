using System;
using System.ComponentModel.Design;
using CommitAssist.UI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace CommitAssist.Commands
{
    // Two entry points, same pipeline:
    //   Tools menu  → "CommitAssist: Generate Commit Message"
    //   Keyboard    → Ctrl+Shift+G
    // Command IDs must match the values in Menus.vsct.
    internal sealed class GenerateCommitMessageCommand
    {
        public static readonly Guid CommandSet = new(CommitAssistPackage.CommandSetGuidString);
        public const int GenerateCommandId     = 0x0100;
        public const int ShowToolWindowId      = 0x0101;

        private readonly CommitAssistPackage _package;

        private GenerateCommitMessageCommand(CommitAssistPackage package,
                                             OleMenuCommandService commandService)
        {
            _package = package;

            var generateCmd = new OleMenuCommand(ExecuteGenerate,
                new CommandID(CommandSet, GenerateCommandId));
            generateCmd.BeforeQueryStatus += OnBeforeQueryStatus;
            commandService.AddCommand(generateCmd);

            commandService.AddCommand(new OleMenuCommand(ExecuteShowToolWindow,
                new CommandID(CommandSet, ShowToolWindowId)));
        }

        public static async Task InitialiseAsync(CommitAssistPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (await package.GetServiceAsync(typeof(IMenuCommandService))
                is OleMenuCommandService commandService)
            {
                _ = new GenerateCommitMessageCommand(package, commandService);
            }
        }

        private void ExecuteGenerate(object sender, EventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                // Show the window first so the user sees the loading state immediately
                await ExecuteShowToolWindowAsync();

                var set = await _package.GenerateCommitSuggestionsAsync();
                if (set is null) return;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var toolWindow = _package.FindToolWindow(typeof(CommitMessageToolWindow),
                                     0, create: false);
                if (toolWindow?.Content is CommitMessageToolWindowControl control)
                    ((CommitMessageViewModel)control.DataContext).LoadSuggestions(set);
            });
        }

        private void ExecuteShowToolWindow(object sender, EventArgs e)
            => _ = ThreadHelper.JoinableTaskFactory.RunAsync(ExecuteShowToolWindowAsync);

        private async Task ExecuteShowToolWindowAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var toolWindow = _package.FindToolWindow(
                typeof(CommitMessageToolWindow), 0, create: true);

            if (toolWindow?.Frame is IVsWindowFrame frame)
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
        }

        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            if (sender is OleMenuCommand cmd)
                cmd.Enabled = _package.GitService is not null;
        }
    }
}
