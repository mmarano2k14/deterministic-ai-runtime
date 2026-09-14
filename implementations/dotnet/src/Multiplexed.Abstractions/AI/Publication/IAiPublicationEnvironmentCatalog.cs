namespace Multiplexed.Abstractions.AI.Publication
{
    /// <summary>
    /// Trusted host catalog. Exact references only; never resolve latest or accept an image
    /// address from tenant code. Removal/change of a pinned entry makes it unavailable.
    /// </summary>
    public interface IAiPublicationEnvironmentCatalog
    {
        AiPublicationEnvironment? Find(string reference);
    }
}
