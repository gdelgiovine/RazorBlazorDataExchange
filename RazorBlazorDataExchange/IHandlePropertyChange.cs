namespace WebNetProBlazorComponents.Components
{
    /// <summary>
    /// Legacy compatibility interface retained to avoid breaking existing consumers.
    /// </summary>
    public interface IHandlePropertyChange
    {
        void NotifyPropertyChanged(string propertyName);
    }

    /// <summary>
    /// Legacy compatibility interface retained to avoid breaking existing consumers.
    /// </summary>
    public interface IRefreshableComponent
    {
        void Refresh();
    }
}
