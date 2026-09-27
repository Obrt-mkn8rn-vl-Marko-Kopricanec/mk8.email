namespace mk8.email.Jmap;

internal sealed record JmapReplayState(JmapMethodResponse Response, Dictionary<string, string> CreatedIds);
