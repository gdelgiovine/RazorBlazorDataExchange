namespace WebNetProBlazorComponents.Components
{
    /// <summary>
    /// Legacy compatibility error notification service.
    /// </summary>
    public class ErrorHandlingService
    {
        public event Action<Exception>? OnError;

        public void HandleError(Exception ex)
        {
            ArgumentNullException.ThrowIfNull(ex);
            OnError?.Invoke(ex);
        }
    }
}
