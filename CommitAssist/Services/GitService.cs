using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CommitAssist.Models;
using LibGit2Sharp;

namespace CommitAssist.Services
{
    public sealed class GitService : IDisposable
    {
        private readonly Repository _repo;
        private bool _disposed;

        public GitService(string repoPath)
        {
            _repo = new Repository(repoPath);
        }

        public static string? DiscoverRepoRoot(string startDirectory)
        {
            try
            {
                var discovered = Repository.Discover(startDirectory);
                if (discovered is null) return null;

                using var repo = new Repository(discovered);
                return repo.Info.WorkingDirectory;
            }
            catch (RepositoryNotFoundException)
            {
                return null;
            }
        }

        // Equivalent to git diff --cached. headTree is null on an initial commit
        // (no HEAD yet), so we diff against an empty tree in that case.
        public IReadOnlyList<StagedFileDiff> GetStagedChanges()
        {
            Tree? headTree = _repo.Head?.Tip?.Tree;

            Patch patch = headTree is null
                ? _repo.Diff.Compare<Patch>(null, DiffTargets.Index)
                : _repo.Diff.Compare<Patch>(headTree, DiffTargets.Index);

            var result = new List<StagedFileDiff>();

            foreach (PatchEntryChanges entry in patch)
            {
                result.Add(new StagedFileDiff
                {
                    FilePath    = entry.Path,
                    OldFilePath = entry.OldPath ?? entry.Path,
                    ChangeKind  = MapChangeKind(entry.Status),
                    PatchText   = entry.Patch,
                    LinesAdded  = entry.LinesAdded,
                    LinesRemoved = entry.LinesDeleted
                });
            }

            return result;
        }

        // Walks the commit log (newest first) and yields only commits with a
        // Conventional Commit subject line. Used to build the initial training set.
        public IEnumerable<(Commit Commit, Patch Diff)> GetConventionalCommits(int maxCommits = 2000)
        {
            var filter = new CommitFilter
            {
                SortBy = CommitSortStrategies.Time
            };

            int count = 0;
            foreach (Commit commit in _repo.Commits.QueryBy(filter))
            {
                if (count >= maxCommits) yield break;

                string firstLine = commit.Message.Split('\n')[0].Trim();
                if (!IsConventionalCommit(firstLine)) continue;

                Patch? diff = null;
                try
                {
                    if (commit.Parents.Any())
                        diff = _repo.Diff.Compare<Patch>(commit.Parents.First().Tree, commit.Tree);
                }
                catch
                {
                    // Some edge-case commits (merges, root) can fail — skip them
                    continue;
                }

                if (diff is null) continue;

                yield return (commit, diff);
                count++;
            }
        }

        private static readonly Regex ConventionalPrefixRegex =
            new(@"^(feat|fix|chore|refactor|test|docs|style|perf|ci|build|revert)(\(.+\))?!?\s*:",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool IsConventionalCommit(string firstLine) =>
            ConventionalPrefixRegex.IsMatch(firstLine);

        public static (string Type, string Scope) ParseConventionalPrefix(string firstLine)
        {
            var match = ConventionalPrefixRegex.Match(firstLine);
            if (!match.Success) return (string.Empty, string.Empty);

            string type  = match.Groups[1].Value.ToLowerInvariant();
            string scope = match.Groups[2].Success
                ? match.Groups[2].Value.Trim('(', ')')
                : string.Empty;
            return (type, scope);
        }

        private static FileChangeKind MapChangeKind(ChangeKind kind) => kind switch
        {
            ChangeKind.Added    => FileChangeKind.Added,
            ChangeKind.Deleted  => FileChangeKind.Deleted,
            ChangeKind.Renamed  => FileChangeKind.Renamed,
            ChangeKind.Copied   => FileChangeKind.Copied,
            _                   => FileChangeKind.Modified
        };

        public void Dispose()
        {
            if (!_disposed)
            {
                _repo.Dispose();
                _disposed = true;
            }
        }
    }
}
