namespace OpenBox
{
    /// <summary>A page that does something of its own with B (the system's back request).</summary>
    internal interface IBackHandler
    {
        /// <summary>True if the page dealt with B, so openbox shouldn't go back home.</summary>
        bool HandleBack();
    }
}
