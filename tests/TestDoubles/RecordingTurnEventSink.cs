using System.Collections.Generic;
using Aukenid.Core.Engine;

namespace Aukenid.Core.Tests.TestDoubles;

internal sealed class RecordingTurnEventSink : ITurnEventSink
{
    private readonly List<ITurnEvent> _events = new();

    public IReadOnlyList<ITurnEvent> Events => _events;

    public void Emit(ITurnEvent @event) => _events.Add(@event);
}

