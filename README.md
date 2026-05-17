# CommitAssist — Smart Commit Messages for Visual Studio

Generates [Conventional Commits](https://www.conventionalcommits.org/) messages from your **staged changes only**. No diff text is ever shown to the user. Everything runs locally — no API calls, no telemetry.

---

## How it works

```
git diff --cached
    └─► LibGit2Sharp ──► StagedFileDiff[]
                              │
                    ┌─────────┴──────────┐
                    │                    │
             DiffParserService    RoslynAnalyserService
             (file stats,         (added method names,
              layer detection)     class names, namespace)
                    │                    │
                    └────────┬───────────┘
                             │
                      FeatureBuilderService
                      (DiffFeatures record)
                             │
                    ┌────────┴────────┐
                    │                 │
              ML.NET model      Scope resolver
              (commit type)     (component name)
                    │                 │
                    └────────┬────────┘
                             │
                  CommitMessageTemplateEngine
                  3 variants: Concise / Detailed / Ticket-linked
                             │
              ┌──────────────┴──────────────┐
              │                             │
       CommitAssist              Git Changes panel
       tool window               (auto-filled)
```

---

## Prerequisites

| Requirement | Version |
|---|---|
| Visual Studio | 2022 (17.6+) |
| .NET | 8.0 (Windows) |
| Git | Any — accessed via LibGit2Sharp (bundled) |

---

## Building the VSIX

```bash
git clone https://github.com/anaya-upadhyay/CommitAssist.git
cd commitassist
dotnet restore
dotnet build -c Release
```

Output: `CommitAssist/bin/Release/CommitAssist.vsix`. Double-click to install.

---

## First-run training

On first solution open, CommitAssist scans up to **2,000 commits** in your repo history, extracts features from each diff, and trains an ML.NET multiclass classifier. This takes 30–120 seconds depending on repo size and runs in the background.

**Minimum viable training:** ~50 commits with Conventional Commit prefixes (`feat:`, `fix:`, `chore:`, etc.). Repos without Conventional Commits still work — the rule-based fallback is always active.

Training data and the model file are stored per-repo in:

```
%LOCALAPPDATA%\CommitAssist\<repo-name>\
  training_samples.ndjson   ← labelled feature vectors
  commit_type_model.zip     ← trained ML.NET model
  meta.json                 ← last-seen commit SHA
```

Nothing leaves this folder. No network calls are made.

---

## Incremental background training

Every 3 minutes CommitAssist checks for new commits. When 50 or more new labelled examples have accumulated, it retrains silently and hot-swaps the model — no restart required.

Over time the model picks up your team's specific patterns and conventions.

---

## Using CommitAssist

### Via the Git Changes panel (auto-fill)
1. Stage your files.
2. Switch to the Git Changes window.
3. CommitAssist detects the focus change, analyses staged changes, and pre-fills the commit message box with the top suggestion.
4. Edit, accept, or regenerate.

### Via the CommitAssist tool window
- **View → Other Windows → CommitAssist**, or
- **Tools → CommitAssist: Generate Commit Message**, or
- **Ctrl+Shift+G**

The tool window shows three variants. Click **Use this** to inject into Git Changes, or **Copy** to put it on the clipboard.

### Scope warning
If your staged diff spans four or more directories:

> ⚠ This diff touches 5 directories. Consider splitting into smaller, focused commits.

---

## Output format

```
feat(UserService): add GetUserById

- add GetUserById
- remove FetchUser (deprecated)
- update IUserRepository interface

12 insertions(+), 3 deletions(-) across 2 file(s)
```

| Variant | Subject | Body | Footer |
|---|---|---|---|
| Concise | ✓ | — | — |
| Detailed | ✓ | ✓ | — |
| Ticket-linked | ✓ | ✓ | `Refs: #` |

---

## Recognised commit types

`feat` · `fix` · `refactor` · `chore` · `test` · `docs` · `style` · `perf` · `ci` · `build`

---

## Confidence score

Each suggestion shows a confidence score. Before the model has been trained (or when fewer than 10 training samples exist), the rule-based fallback returns 100% — it's deterministic, not probabilistic.

Once the ML model is loaded, the score is the SdcaMaximumEntropy classifier's output probability for the winning class.

---

## Architecture

| File | What it does |
|---|---|
| `CommitAssistPackage.cs` | Async package entry point — bootstraps services, handles solution events |
| `Services/GitService.cs` | LibGit2Sharp wrapper — staged diffs and commit history |
| `Services/DiffParserService.cs` | Raw patch → structural signals (layers, file types, line counts) |
| `Services/RoslynAnalyserService.cs` | Added lines → symbol names via Roslyn partial parse |
| `Services/FeatureBuilderService.cs` | Combines parser + Roslyn into a `DiffFeatures` record |
| `ML/ModelTrainerAndPredictor.cs` | ML.NET pipeline — training, serialisation, inference |
| `Services/CommitMessageTemplateEngine.cs` | Assembles commit message variants |
| `Services/BackgroundTrainingService.cs` | Initial training + incremental watch loop |
| `UI/CommitMessageToolWindow*.cs/xaml` | WPF tool window with MVVM ViewModel |
| `Integration/GitChangesIntegration.cs` | Auto-fills VS Git Changes commit message box |
| `Commands/GenerateCommitMessageCommand.cs` | Menu and keyboard command registration |

---

## Contributing

Pull requests welcome. Before submitting:
- Ensure the VSIX builds cleanly against VS 2022 SDK 17.x.
- Add a `TrainingSample` test for any new feature signal.
- Do not add network calls — this must stay fully local.

---

## Licence

MIT
