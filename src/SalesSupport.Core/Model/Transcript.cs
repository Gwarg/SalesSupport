namespace SalesSupport.Core.Model;

/// <summary>One finalized utterance from either channel. Index is the turn counter used across the picture.</summary>
public sealed record Utterance(int Index, Speaker Speaker, string Text, long TimestampMs = 0);
