namespace WebNetProBlazorComponents.Components

{
    public class ErrorHandlingService
    {
        public event System.Action<System.Exception> OnError;

        public void HandleError(System.Exception ex)
        {
            OnError?.Invoke(ex);
        }
    }
}
