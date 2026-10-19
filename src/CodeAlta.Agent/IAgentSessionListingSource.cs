namespace CodeAlta.Agent;

// A session store that can give the listed form of one session, as its full listing gives it: a catalog that holds a
// snapshot reads a changed session again alone.
internal interface IAgentSessionListingSource
{
    // Known: false when the store cannot say it the way its listing would (the session is not in its listing cache); the
    // caller then lists everything again. A known session that is no longer listed is (true, null).
    Task<(bool Known, AgentSessionMetadata? Session)> TryGetListedSessionAsync(string sessionId, CancellationToken cancellationToken);
}
