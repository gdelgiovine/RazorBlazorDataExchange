namespace WebNetProBlazorComponents.Components
{
    public interface IHandlePropertyChange
    {
        void NotifyPropertyChanged(string propertyName);
    }

        public interface IRefreshableComponent
        {
            void Refresh();
        }

}
