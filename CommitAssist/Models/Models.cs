using System.Collections.Generic;

namespace CommitAssist.Models
{
    public sealed class StagedFileDiff
    {
        public string   FilePath     { get; init; } = string.Empty;
        public string   OldFilePath  { get; init; } = string.Empty; // non-empty on rename
        public FileChangeKind ChangeKind { get; init; }
        public string   PatchText    { get; init; } = string.Empty;
        public int      LinesAdded   { get; init; }
        public int      LinesRemoved { get; init; }
    }

    public enum FileChangeKind { Added, Modified, Deleted, Renamed, Copied }

    public sealed class SymbolChangeSummary
    {
        public List<string> AddedMethods    { get; init; } = new();
        public List<string> RemovedMethods  { get; init; } = new();
        public List<string> ModifiedMethods { get; init; } = new();
        public List<string> AddedClasses    { get; init; } = new();
        public List<string> RemovedClasses  { get; init; } = new();
        public List<string> AddedProperties { get; init; } = new();
        public string       PrimaryNamespace { get; init; } = string.Empty;
        public string       DetectedLayer   { get; init; } = string.Empty;
    }

    public sealed class CommitSuggestion
    {
        public string Subject     { get; init; } = string.Empty;
        public string Body        { get; init; } = string.Empty;
        public string Footer      { get; init; } = string.Empty;
        public float  Confidence  { get; init; }
        public string StyleLabel  { get; init; } = string.Empty;

        public string FullMessage =>
            string.IsNullOrWhiteSpace(Body)
                ? Subject
                : $"{Subject}\n\n{Body}" + (string.IsNullOrWhiteSpace(Footer) ? "" : $"\n\n{Footer}");
    }

    public sealed class CommitSuggestionSet
    {
        public List<CommitSuggestion> Suggestions    { get; init; } = new();
        public CommitSuggestion       Primary        => Suggestions.Count > 0 ? Suggestions[0] : new();
        public bool                   ScopeWarning   { get; init; }
        public string?                ScopeWarningMessage { get; init; }
    }

    // Persisted as NDJSON under %LOCALAPPDATA%\CommitAssist\<repo>\training_samples.ndjson
    // so training data survives VS restarts.
    public sealed class TrainingSample
    {
        public string CommitSha  { get; set; } = string.Empty;
        public string CommitType { get; set; } = string.Empty;
        public string Scope      { get; set; } = string.Empty;

        public float AddedMethodCount   { get; set; }
        public float RemovedMethodCount { get; set; }
        public float ModifiedMethodCount { get; set; }
        public float TestFileRatio      { get; set; }
        public float LineDeltaRatio     { get; set; }
        public float TotalLinesChanged  { get; set; }
        public float FileCount          { get; set; }
        public float ScopeSpread        { get; set; }
        public bool  HasMigration       { get; set; }
        public bool  HasCsprojChange    { get; set; }
        public bool  HasDocChange       { get; set; }
        public bool  IsOnlyTests        { get; set; }
        public string PrimaryLayer      { get; set; } = string.Empty;
    }
}
