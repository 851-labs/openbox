namespace OpenBox
{
    /// <summary>
    /// A page that does something with B (the system's back request): openbox's own screens use
    /// it to go back, apps for themselves. On other pages it does nothing; View goes home.
    /// </summary>
    internal interface IBackHandler
    {
        /// <summary>True if the page did something with B.</summary>
        bool HandleBack();
    }
}
