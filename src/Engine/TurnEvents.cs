namespace Aukenid.Core.Engine;

using System.Collections.Generic;
using Aukenid.Core.Contracts;

public enum TurnEventKind
{
    Pending = 0,
    Token = 1,
    ThreadTopicShift = 2,
    ThreadsList = 3,
    DocumentOpen = 4,
    DocumentVersionsList = 5,
    DocumentProgress = 6,
}

public interface ITurnEvent
{
    TurnEventKind Kind { get; }

    string ThreadId { get; }
}

public interface ITurnEventSink
{
    void Emit(ITurnEvent @event);
}

public sealed class NullTurnEventSink : ITurnEventSink
{
    public static readonly NullTurnEventSink Instance = new();

    private NullTurnEventSink() { }

    public void Emit(ITurnEvent @event) { }
}

public sealed record PendingEvent(
    string ThreadId,
    string Key,
    int? PageFrom = null,
    int? PageTo = null) : ITurnEvent
{
    public TurnEventKind Kind => TurnEventKind.Pending;
}

public sealed record TokenEvent(
    string ThreadId,
    string MessageId,
    string Text) : ITurnEvent
{
    public TurnEventKind Kind => TurnEventKind.Token;
}

public sealed record ThreadTopicShiftEvent(
    string ThreadId,
    string MessageId) : ITurnEvent
{
    public TurnEventKind Kind => TurnEventKind.ThreadTopicShift;
}

public sealed record ThreadsListEvent(
    string ThreadId,
    IReadOnlyList<ThreadDto> Threads) : ITurnEvent
{
    public TurnEventKind Kind => TurnEventKind.ThreadsList;
}

public sealed record DocumentOpenEvent(
    string ThreadId,
    string Content,
    bool Exists) : ITurnEvent
{
    public TurnEventKind Kind => TurnEventKind.DocumentOpen;
}

public sealed record DocumentVersionsListEvent(
    string ThreadId,
    IReadOnlyList<DocumentVersionDto> Versions) : ITurnEvent
{
    public TurnEventKind Kind => TurnEventKind.DocumentVersionsList;
}

public sealed record DocumentProgressEvent(
    string ThreadId,
    int Chars) : ITurnEvent
{
    public TurnEventKind Kind => TurnEventKind.DocumentProgress;
}

