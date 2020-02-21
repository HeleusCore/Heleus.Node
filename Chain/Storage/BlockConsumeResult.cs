namespace Heleus.Chain.Storage
{
    public enum BlockConsumeResult
    {
        Ok,
        NotActive,
        SyncRequired,
        InvalidHash,
        MissingBlock
    }
}
