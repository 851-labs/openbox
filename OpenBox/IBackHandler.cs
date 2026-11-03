namespace OpenBox
{
    /// <summary>
    /// A page that does something with B (the system's back request). B belongs to the page: on
    /// other pages it does nothing, and View is what goes home.
    /// </summary>
    internal interface IBackHandler
    {
        /// <summary>True if the page did something with B.</summary>
        bool HandleBack();
    }
}
