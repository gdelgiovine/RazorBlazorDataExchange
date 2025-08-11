using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.JSInterop;




namespace RazorBlazorDataExchangeTester.Pages
{
    //IDbContextFactory<WebnetPRO.DataAccessLayer.NewdisContext> DbFactory;
    public class IndexModel : PageModel
    {
        public RazorBlazorDataExchange DataExchange;
        public int XCounter { get; set; }
        public int Counter { get; set; } = 0;
        
        public string SessionId { get; set; }
        private string Setter = "Razor";
        private IJSRuntime JS;

        private const string CounterSessionKey = "XCounter";
        public bool CanCallJS = false;
        // Nel costruttore della classe serve  per passare il servizio IHttpContextAccessor e quindi accedere alla sessione
        private readonly IHttpContextAccessor _httpContextAccessor;


        public IndexModel(RazorBlazorDataExchange dataExchange, IJSRuntime js, IHttpContextAccessor httpContextAccessor)
        {
            DataExchange = dataExchange;
            DataExchange.DataChangeWithActor += OnDataChanged;
            JS = js;
            _httpContextAccessor = httpContextAccessor;
            this.SessionId = GetSessionID();
            //DataExchange.StoreValue(this.SessionId, "XCounter", XCounter, Setter);  
        }


        // Modifica il metodo GetSessionID per usare l'accessor
        private string GetSessionID(string Name = "RazorBlazorDataaExchangeSessionId")
        {
            // Verifica innanzitutto se HttpContext è disponibile tramite l'accessor
            if (_httpContextAccessor.HttpContext == null || _httpContextAccessor.HttpContext.Session == null)
            {
                // Se non è disponibile, genera un nuovo ID di sessione
                return Guid.NewGuid().ToString();
            }

            string sessionId = null;
            try
            {
                // Prova a recuperare il valore di sessione, gestendo possibili eccezioni
                sessionId = _httpContextAccessor.HttpContext.Session.GetString(Name);
            }
            catch
            {
                // In caso di errore, ritorna un nuovo ID
                return Guid.NewGuid().ToString();
            }

            // Se non esiste, creane uno nuovo e memorizzalo
            if (string.IsNullOrEmpty(sessionId))
            {
                sessionId = Guid.NewGuid().ToString();
                try
                {
                    _httpContextAccessor.HttpContext.Session.SetString(Name, sessionId);
                }
                catch
                {
                    // Ignora errori di scrittura nella sessione
                }
            }

            return sessionId;
        }

        private   void OnDataChanged(object sender, DataChangeWithActorEventArgs e)
        {
           
            if (!DataExchange.ShouldProcessEvent(this.SessionId, this.Setter, e))
                return;

            if (e.PropertyName == "XCounter")
            {
                XCounter = (int)e.Value;
                //DataExchange.NotifyDataChange(this.SessionId, "XCounter", XCounter, Setter,null ,false);

               

            }

            

        }


        public void OnPost()
        {

        }


        public void OnGet()
        {
            this.SessionId = GetSessionID();
            var x = DataExchange.GetValue(this.SessionId, "XCounter");
            if (x != null)
                XCounter = (int)DataExchange.GetValue(this.SessionId, "XCounter");
            else // manage il caso in cui non esista il valore  
                //DataExchange.StoreValue(this.SessionId, "XCounter", XCounter, this.Setter );
                DataExchange.NotifyDataChange(this.SessionId, "XCounter", XCounter, Setter);
        }

        public void Dispose()
        {
            DataExchange.DataChangeWithActor -= OnDataChanged;
        }

        public IActionResult OnPostIncrementa()
        {
            Incrementa();
            return Page();
        }

        public void Incrementa()
        {
            XCounter++;
            // Notifica il cambiamento a tutti gli altri componenti
            DataExchange.NotifyDataChange(this.SessionId, "XCounter", XCounter, Setter);
        }

        [IgnoreAntiforgeryToken]
        public JsonResult OnPostIncrementCounter()
        {
            var x = DataExchange.GetValue(this.SessionId, "XCounter");
            if (x != null)
            {
                XCounter = (int)x;
                XCounter++;
                // Notifica il cambiamento usando RazorBlazorDataExchange
                DataExchange.NotifyDataChange(this.SessionId, "XCounter", XCounter, Setter);
                

            }
            
            return new JsonResult(new { value = XCounter });
        }
    }

}