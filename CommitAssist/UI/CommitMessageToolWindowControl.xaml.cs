using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using CommitAssist.Integration;
using CommitAssist.Models;
using Microsoft.VisualStudio.Shell;

namespace CommitAssist.UI
{
    // ── Code-behind ───────────────────────────────────────────────────────────

    public partial class CommitMessageToolWindowControl : UserControl
    {
        private readonly CommitMessageViewModel _vm = new();

        public CommitMessageToolWindowControl()
        {
            InitializeComponent();
            DataContext = _vm;
        }

        private void GenerateButton_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                _vm.SetLoading();

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var package = CommitAssistPackage.Instance;
                if (package is null)
                {
                    _vm.SetEmpty("Extension not initialised.");
                    return;
                }

                var set = await package.GenerateCommitSuggestionsAsync();
                if (set is null || set.Suggestions.Count == 0)
                {
                    _vm.SetEmpty("No staged changes — stage files in Git Changes first.");
                    return;
                }

                _vm.LoadSuggestions(set);
            });
        }

        private void CopyPrimaryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.Suggestions.Count == 0) return;
            Clipboard.SetText(_vm.Suggestions[0].FullMessage);
            _vm.StatusMessage = "Copied to clipboard.";
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.Suggestions.Count == 0) return;

            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var package = CommitAssistPackage.Instance;
                package?.GitChangesIntegration?.InjectCommitMessage(
                    _vm.Suggestions[0].FullMessage);
                _vm.StatusMessage = "Applied to Git Changes panel.";
            });
        }

        private void UseThisButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is CommitSuggestionViewModel vm)
            {
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var package = CommitAssistPackage.Instance;
                    package?.GitChangesIntegration?.InjectCommitMessage(vm.FullMessage);
                    _vm.StatusMessage = $"Applied \"{vm.StyleLabel}\" to Git Changes panel.";
                });
            }
        }

        private void CopySuggestionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is CommitSuggestionViewModel vm)
            {
                Clipboard.SetText(vm.FullMessage);
                _vm.StatusMessage = "Copied to clipboard.";
            }
        }
    }

    // ── ViewModel ─────────────────────────────────────────────────────────────

    public sealed class CommitMessageViewModel : INotifyPropertyChanged
    {
        // ── Observable state ─────────────────────────────────────────────────

        private bool   _isLoading;
        private bool   _isEmpty = true;
        private string _emptyMessage = "Click Generate to analyse your staged changes.";
        private string _statusMessage = string.Empty;
        private string? _scopeWarningMessage;

        public ObservableCollection<CommitSuggestionViewModel> Suggestions { get; } = new();

        public bool HasSuggestions => Suggestions.Count > 0;

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); OnPropertyChanged(nameof(LoadingVisibility)); }
        }

        public bool IsEmpty
        {
            get => _isEmpty;
            set { _isEmpty = value; OnPropertyChanged(); OnPropertyChanged(nameof(EmptyVisibility)); OnPropertyChanged(nameof(SuggestionsVisibility)); }
        }

        public string EmptyMessage
        {
            get => _emptyMessage;
            set { _emptyMessage = value; OnPropertyChanged(); }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public string? ScopeWarningMessage
        {
            get => _scopeWarningMessage;
            set { _scopeWarningMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScopeWarningVisibility)); }
        }

        // ── Visibility converters ─────────────────────────────────────────────

        public Visibility LoadingVisibility     => IsLoading ? Visibility.Visible : Visibility.Collapsed;
        public Visibility EmptyVisibility       => (!IsLoading && IsEmpty) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SuggestionsVisibility => (!IsLoading && !IsEmpty) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ScopeWarningVisibility => !string.IsNullOrEmpty(ScopeWarningMessage)
                                                    ? Visibility.Visible : Visibility.Collapsed;

        // ── State transitions ─────────────────────────────────────────────────

        public void SetLoading()
        {
            IsLoading = true;
            IsEmpty   = false;
            Suggestions.Clear();
            StatusMessage = "Analysing…";
            ScopeWarningMessage = null;
            OnPropertyChanged(nameof(HasSuggestions));
        }

        public void SetEmpty(string message = "")
        {
            IsLoading     = false;
            IsEmpty       = true;
            EmptyMessage  = message;
            StatusMessage = string.Empty;
            Suggestions.Clear();
            OnPropertyChanged(nameof(HasSuggestions));
        }

        public void LoadSuggestions(CommitSuggestionSet set)
        {
            IsLoading = false;
            IsEmpty   = false;
            Suggestions.Clear();

            foreach (var s in set.Suggestions)
                Suggestions.Add(new CommitSuggestionViewModel(s));

            ScopeWarningMessage = set.ScopeWarningMessage;
            StatusMessage = $"{Suggestions.Count} suggestion(s) generated.";
            OnPropertyChanged(nameof(HasSuggestions));
        }

        // ── INotifyPropertyChanged ────────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ── Per-suggestion ViewModel ──────────────────────────────────────────────

    public sealed class CommitSuggestionViewModel
    {
        private readonly CommitSuggestion _model;

        public CommitSuggestionViewModel(CommitSuggestion model) => _model = model;

        public string  Subject         => _model.Subject;
        public string  Body            => _model.Body;
        public string  Footer          => _model.Footer;
        public string  FullMessage     => _model.FullMessage;
        public string  StyleLabel      => _model.StyleLabel;
        public string  ConfidenceLabel => $"{_model.Confidence:P0} confidence";

        public Visibility BodyVisibility =>
            string.IsNullOrWhiteSpace(Body) ? Visibility.Collapsed : Visibility.Visible;
    }
}
