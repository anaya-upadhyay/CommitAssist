using System;
using System.Windows;
using System.Windows.Controls;
using EnvDTE;
using Microsoft.VisualStudio.Shell;

namespace CommitAssist.Integration
{
    // VS doesn't expose a public API for the Git Changes commit message box,
    // so we find it by walking the WPF visual tree and matching the automation ID
    // "PART_CommitMessageTextBox". If Microsoft ever renames that control, injection
    // silently no-ops and the user falls back to copying from the tool window.
    public sealed class GitChangesIntegration
    {
        private readonly CommitAssistPackage _package;
        private bool _attached;

        public GitChangesIntegration(CommitAssistPackage package)
        {
            _package = package;
        }

        public void Attach()
        {
            if (_attached) return;
            _attached = true;

            // Window-activated fires whenever the Git Changes panel gets focus,
            // which is the natural moment staged files are up to date.
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (await _package.GetServiceAsync(typeof(DTE)) is DTE dte)
                {
                    dte.Events.WindowEvents.WindowActivated += OnWindowActivated;
                }
            });
        }

        public void Detach()
        {
            if (!_attached) return;
            _attached = false;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (await _package.GetServiceAsync(typeof(DTE)) is DTE dte)
                {
                    dte.Events.WindowEvents.WindowActivated -= OnWindowActivated;
                }
            });
        }

        private void OnWindowActivated(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus)
        {
            if (!IsGitChangesWindow(gotFocus)) return;

            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                var set = await _package.GenerateCommitSuggestionsAsync();
                if (set is null) return;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                InjectCommitMessage(set.Primary.FullMessage);
            });
        }

        private static bool IsGitChangesWindow(EnvDTE.Window? window)
        {
            if (window is null) return false;
            try
            {
                return window.Caption?.StartsWith("Git Changes",
                           StringComparison.OrdinalIgnoreCase) == true;
            }
            catch { return false; }
        }

        // UI thread only.
        public void InjectCommitMessage(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(message)) return;

            var textBox = FindGitChangesMessageBox();
            if (textBox is null) return;

            textBox.Text = message;
            textBox.CaretIndex = message.Length;
            textBox.Focus();
        }

        private static TextBox? FindGitChangesMessageBox()
        {
            if (Application.Current?.MainWindow is null) return null;

            return FindDescendant<TextBox>(
                Application.Current.MainWindow,
                tb =>
                {
                    // AutomationId is more stable across VS versions than Name
                    var aid = System.Windows.Automation.AutomationProperties
                                    .GetAutomationId(tb);
                    if (aid?.IndexOf("CommitMessage", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;

                    return tb.Name?.IndexOf("CommitMessage",
                               StringComparison.OrdinalIgnoreCase) >= 0;
                });
        }

        private static T? FindDescendant<T>(DependencyObject parent, Func<T, bool> predicate)
            where T : DependencyObject
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T typed && predicate(typed)) return typed;
                var result = FindDescendant(child, predicate);
                if (result is not null) return result;
            }
            return null;
        }
    }
}
