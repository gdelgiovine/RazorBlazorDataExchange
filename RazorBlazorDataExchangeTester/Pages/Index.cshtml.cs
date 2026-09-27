using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RazorBlazorDataExchangeTester.Pages
{
    public class IndexModel : PageModel
    {
        private const string CounterProperty = "XCounter";
        private const string SessionKey = "RazorBlazorDataExchangeSessionId";
        private const string ActorId = "Razor:/Index";

        private readonly IHttpContextAccessor _httpContextAccessor;

        public IndexModel(
            RazorBlazorDataExchange dataExchange,
            IHttpContextAccessor httpContextAccessor)
        {
            DataExchange = dataExchange;
            _httpContextAccessor = httpContextAccessor;
        }

        public RazorBlazorDataExchange DataExchange { get; }
        public int XCounter { get; set; }
        public string SessionId { get; set; } = string.Empty;

        public void OnGet()
        {
            SessionId = GetSessionId();

            if (DataExchange.TryGet<int>(SessionId, CounterProperty, out var current))
            {
                XCounter = current;
            }
            else
            {
                XCounter = 0;
                DataExchange.StoreValue(SessionId, CounterProperty, XCounter, ActorId);
            }
        }

        public IActionResult OnPostIncrementa()
        {
            SessionId = GetSessionId();
            var message = DataExchange.Update<int>(
                SessionId,
                CounterProperty,
                current => current + 1,
                ActorId);

            XCounter = message?.Value ?? DataExchange.Get<int>(SessionId, CounterProperty);
            return Page();
        }

        public async Task<JsonResult> OnPostIncrementCounter(CancellationToken cancellationToken)
        {
            SessionId = GetSessionId();

            var message = await DataExchange.UpdateAsync<int>(
                SessionId,
                CounterProperty,
                current => current + 1,
                ActorId,
                cancellationToken: cancellationToken);

            XCounter = message?.Value ?? DataExchange.Get<int>(SessionId, CounterProperty);
            return new JsonResult(new { value = XCounter, version = message?.Version });
        }

        private string GetSessionId()
        {
            var session = _httpContextAccessor.HttpContext?.Session
                ?? throw new InvalidOperationException("ASP.NET Core session is not available for this request.");

            var sessionId = session.GetString(SessionKey);
            if (!string.IsNullOrWhiteSpace(sessionId))
                return sessionId;

            sessionId = Guid.NewGuid().ToString("N");
            session.SetString(SessionKey, sessionId);
            return sessionId;
        }
    }
}
