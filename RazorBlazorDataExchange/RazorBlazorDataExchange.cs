
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using System.Collections.Concurrent;
using System.ComponentModel;


/// <summary>
/// Eventi di modifica proprietà con informazioni sul sessionId, setter e getters.
/// </summary>
public class DataChangeWithActorEventArgs : PropertyChangedEventArgs
{
    /// <summary>
    /// Identificatore univoco della sessione.
    /// </summary>
    public string SessionId { get; set; } = "";

    /// <summary>
    /// Identifica chi ha modificato il valore.
    /// </summary>
    public string Setter { get; set; }

    /// <summary>
    /// Il nuovo valore della proprietà.
    /// </summary>
    public object Value { get; set; }

    /// <summary>
    /// Il tipo del valore.
    /// </summary>
    public Type Type { get; set; }

    /// <summary>
    /// Lista di chi ha richiesto notifiche sui cambiamenti.
    /// </summary>
    public List<string> Getters { get; set; } = new List<string>();

    /// <summary>
    /// Indica se questa notifica fa parte di un ciclo di elaborazione in corso.
    /// Utile per prevenire ricorsione infinita nelle notifiche.
    /// </summary>
    public bool IsProcessingNotification { get; set; }

    /// <summary>
    /// Inizializza una nuova istanza della classe <see cref="DataChangeWithActorEventArgs"/>.
    /// </summary>
    /// <param name="sessionId">Identificatore univoco della sessione.</param>
    /// <param name="propertyName">Nome della proprietà modificata.</param>
    /// <param name="value">Il nuovo valore della proprietà.</param>
    /// <param name="type">Il tipo del valore.</param>
    /// <param name="setter">Identifica chi ha modificato il valore.</param>
    /// <param name="getters">Lista di chi ha richiesto notifiche sui cambiamenti.</param>
    /// <param name="isProcessingNotification">Indica se questa notifica fa parte di un ciclo di elaborazione in corso.</param>
    public DataChangeWithActorEventArgs(
        string sessionId,
        string propertyName,
        object value,
        Type type,
        string setter,
        List<string> getters,
        bool isProcessingNotification = false) : base(propertyName)
    {
        SessionId = sessionId;
        Setter = setter;
        Getters = getters ?? new List<string>();
        Value = value;
        Type = Value.GetType();
        IsProcessingNotification = isProcessingNotification;
    }
}

/// <summary>
/// Eventi di modifica proprietà con informazioni sul sessionId, setter e getters.
/// </summary>
public class DataChangesWithActorEventArgs : PropertyChangedEventArgs
{
    /// <summary>
    /// Identificatore univoco della sessione.
    /// </summary>
    public string SessionId { get; set; } = "";

    /// <summary>
    /// Identifica chi ha modificato il valore.
    /// </summary>
    public string Setter { get; set; }

    /// <summary>
    /// Lista delle proprietà modificate 
    /// </summary>
    public List<string> Properties { get; set; } = new List<string>();


    /// <summary>
    /// Lista di chi ha richiesto notifiche sui cambiamenti.
    /// </summary>
    public List<string> Getters { get; set; } = new List<string>();

    /// <summary>
    /// Indica se questa notifica fa parte di un ciclo di elaborazione in corso.
    /// Utile per prevenire ricorsione infinita nelle notifiche.
    /// </summary>
    public bool IsProcessingNotification { get; set; }

    /// <summary>
    /// Inizializza una nuova istanza della classe <see cref="DataChangeWithActorEventArgs"/>.
    /// </summary>
    /// <param name="sessionId">Identificatore univoco della sessione.</param>
    /// <param name="propertyName">Nome della proprietà modificata.</param>
    /// <param name="value">Il nuovo valore della proprietà.</param>
    /// <param name="type">Il tipo del valore.</param>
    /// <param name="setter">Identifica chi ha modificato il valore.</param>
    /// <param name="getters">Lista di chi ha richiesto notifiche sui cambiamenti.</param>
    /// <param name="isProcessingNotification">Indica se questa notifica fa parte di un ciclo di elaborazione in corso.</param>
    public DataChangesWithActorEventArgs(
        string sessionId,
        List<string> properties,
        string setter,
        List<string> getters,
        bool isProcessingNotification = false) : base(string.Join(",", properties)) 
    {
        SessionId = sessionId;
        Setter = setter;
        Getters = getters ?? new List<string>();
        Properties = properties ?? new List<string>();  
        IsProcessingNotification = isProcessingNotification;
    }
}


/// <summary>
/// Gestore centrale per lo scambio dati tra componenti Razor e Blazor.
/// </summary>
public class RazorBlazorDataExchange : INotifyPropertyChanged
{
    /// <summary>
    /// Evento standard per notifiche di cambio proprietà.
    /// </summary>
    public event PropertyChangedEventHandler PropertyChanged;

    
    /// <summary>
    /// Dizionario che mantiene i valori degli oggetti con relativi metadati di modifica.
    /// La chiave è una combinazione di sessionId e propertyName.
    /// </summary>
    private readonly Dictionary<string, ObjectValueMetadata> _valueStore = new Dictionary<string, ObjectValueMetadata>();

    
    /// <summary>
    /// Evento di notifica modifica di una proprietà  con informazioni sul contesto.
    /// </summary>
    public event EventHandler<DataChangeWithActorEventArgs> DataChangeWithActor;

    /// <summary>
    /// Evento di notifica di modifica di un insieme di proprietà con informazioni sul contesto.
    /// </summary>
    public event EventHandler<DataChangesWithActorEventArgs> DataChangesWithActor;

  

    private DateTime _lastAccess = DateTime.UtcNow;
    private readonly object _lock = new object();
      
    
    ///// <summary>
    ///// Inizializza una nuova istanza della classe <see cref="RazorBlazorDataExchange"/> senza JavaScript Runtime.
    ///// </summary>
    //public RazorBlazorDataExchange()
    //{
       
    //}

    /// <summary>
    /// Ultimo accesso ai dati, protetto da lock per evitare problemi di concorrenza.
    /// </summary>
    public DateTime LastAccess
    {
        get
        {
            lock (_lock)
            {
                return _lastAccess;
            }
        }
        private set
        {
            lock (_lock)
            {
                _lastAccess = value;
            }
        }
    }

       

    /// <summary>
    /// Memorizza un valore nel dizionario con i relativi metadati.
    /// </summary>
    /// <param name="sessionId">Identificatore univoco della sessione.</param>
    /// <param name="propertyName">Nome della proprietà.</param>
    /// <param name="value">Il valore dell'oggetto.</param>
    /// <param name="setter">Chi ha modificato il valore.</param>
    public void StoreValue(string sessionId, string propertyName, object value, string setter)
    {
        lock (_lock)
        {
            string key = CreateKey(sessionId, propertyName);
            _valueStore[key] = new ObjectValueMetadata
            {
                Value = value,
                LastModifiedBy = setter,
                LastModifiedAt = DateTime.UtcNow,
                Type = value?.GetType() ?? typeof(object)
            };
        }
    }
    /// <summary>
    /// Recupera un valore dal dizionario con i relativi metadati.
    /// </summary>
    /// <param name="sessionId">Identificatore univoco della sessione.</param>
    /// <param name="propertyName">Nome della proprietà.</param>
    /// <returns>I metadati dell'oggetto se trovati, altrimenti null.</returns>
    public ObjectValueMetadata GetValueMetadata(string sessionId, string propertyName)
    {
        lock (_lock)
        {
            string key = CreateKey(sessionId, propertyName);
            return _valueStore.TryGetValue(key, out var metadata) ? metadata : null;
        }
    }
    /// <summary>
    /// Recupera solo il valore dal dizionario.
    /// </summary>
    /// <param name="sessionId">Identificatore univoco della sessione.</param>
    /// <param name="propertyName">Nome della proprietà.</param>
    /// <returns>Il valore dell'oggetto se trovato, altrimenti null.</returns>
    public object GetValue(string sessionId, string propertyName)
    {
        var metadata = GetValueMetadata(sessionId, propertyName);
     
        return metadata?.Value;
    }

    /// <summary>
    /// Crea una chiave univoca per il dizionario.
    /// </summary>
    private string CreateKey(string sessionId, string propertyName)
    {
        return $"{sessionId}:{propertyName}";
    }

    /// <summary>
    /// Notifica cambiamenti nelle proprietà.
    /// </summary>
    /// <param name="sessionId">Identificatore univoco della sessione.</param>
    /// <param name="property">Nome della proprietà modificata.</param>
    /// <param name="value">Il nuovo valore della proprietà.</param>
    /// <param name="setter">Identifica chi ha modificato il valore.</param>
    /// <param name="getters">Lista di chi ha richiesto notifiche sui cambiamenti.</param>
    /// <param name="isProcessingNotification">Indica se questa notifica fa parte di un ciclo di elaborazione in corso.</param>
    public void NotifyDataChange(string sessionId, string property, object value, string setter, List<string>? getters = null, bool isProcessingNotification = false)
    {
        string propertyName = $"{property}";
        Type type = value?.GetType() ?? typeof(object);
        StoreValue(sessionId, propertyName, value, setter);
        OnDataChange(sessionId, propertyName, value, type, setter, getters, isProcessingNotification);
    }

    /// <summary>
    /// Notifica cambiamenti nelle proprietà.
    /// </summary>
    /// <param name="sessionId">Identificatore univoco della sessione.</param>
    /// <param name="property">Nome della proprietà modificata.</param>
    /// <param name="value">Il nuovo valore della proprietà.</param>
    /// <param name="setter">Identifica chi ha modificato il valore.</param>
    /// <param name="getters">Lista di chi ha richiesto notifiche sui cambiamenti.</param>
    /// <param name="isProcessingNotification">Indica se questa notifica fa parte di un ciclo di elaborazione in corso.</param>
    public void NotifyDataChange(string sessionId, string property, string setter, List<string>? getters = null, bool isProcessingNotification = false)
    {
        var value = GetValue(sessionId, property);
        var type = value?.GetType() ?? typeof(object);
        StoreValue(sessionId, property, value, setter);
        OnDataChange(sessionId, property, value, type, setter, getters, isProcessingNotification);
    }


    /// <summary>
    /// Notifica cambiamenti nelle proprietà.
    /// </summary>
    /// <param name="sessionId">Identificatore univoco della sessione.</param>
    /// <param name="property">Nome della proprietà modificata.</param>
    /// <param name="value">Il nuovo valore della proprietà.</param>
    /// <param name="setter">Identifica chi ha modificato il valore.</param>
    /// <param name="getters">Lista di chi ha richiesto notifiche sui cambiamenti.</param>
    /// <param name="isProcessingNotification">Indica se questa notifica fa parte di un ciclo di elaborazione in corso.</param>
    public void NotifyDataChanges(string sessionId, List<string> properties,string setter, List<string>? getters = null, bool isProcessingNotification = false)
    {
        
        OnDataChanges(sessionId, properties, setter, getters, isProcessingNotification);
    }


    /// <summary>
    /// Confronta due setter.
    /// </summary>
    /// <param name="setter1">Primo setter.</param>
    /// <param name="setter2">Secondo setter.</param>
    /// <returns>True se i setter sono uguali, altrimenti false.</returns>
    public bool SameSetter(string setter1, string setter2)
    {
        bool IsSame= String.Equals(setter1, setter2, StringComparison.OrdinalIgnoreCase);
        return IsSame;
    }

    /// <summary>
    /// Confronta due sessionId.
    /// </summary>
    /// <param name="sessionId1">Primo sessionId.</param>
    /// <param name="sessionId2">Secondo sessionId.</param>
    /// <returns>True se i sessionId sono uguali, altrimenti false.</returns>
    public bool SameSession(string sessionId1, string sessionId2)
    {
        return String.Equals(sessionId1, sessionId2, StringComparison.OrdinalIgnoreCase);
    }


    public bool ShouldProcessEvent(string SessionId,string Setter, DataChangeWithActorEventArgs e)
    {
        // Verifica se l'evento NON è per la sessione corrente
        if (!this.SameSession(SessionId, e.SessionId))
            return false;

        // Verifica se l'evento è dallo stesso setter (da ignorare)
        if (this.SameSetter(Setter, e.Setter))
            return false;

        // Verifica se si tratta di una notifica in elaborazione (evita cicli infiniti)
        if (e.IsProcessingNotification)
            return false;

        // Se tutte le verifiche sono passate, l'evento deve essere elaborato
        return true;
    }


    /// <summary>
    /// Notifica il cambiamento di una proprietà.
    /// </summary>
    /// <param name="SessionId">Identificatore univoco della sessione.</param>
    /// <param name="propertyName">Nome della proprietà modificata.</param>
    /// <param name="value">Il nuovo valore della proprietà.</param>
    /// <param name="type">Il tipo del valore.</param>
    /// <param name="setter">Identifica chi ha modificato il valore.</param>
    /// <param name="getters">Lista di chi ha richiesto notifiche sui cambiamenti.</param>
    /// <param name="isProcessingNotification">Indica se questa notifica fa parte di un ciclo di elaborazione in corso.</param>
    protected void OnDataChange(string SessionId, string propertyName, object value, Type type, string setter, List<string> getters, bool isProcessingNotification = false)
    {
        getters = getters ?? new List<string>();
        DataChangeWithActor?.Invoke(this, new DataChangeWithActorEventArgs(SessionId, propertyName, value, type, setter, getters, isProcessingNotification));
    }

    // <summary>
    /// Notifica il cambiamento di una proprietà.
    /// </summary>
    /// <param name="SessionId">Identificatore univoco della sessione.</param>
    /// <param name="propertyName">Nome della proprietà modificata.</param>
    /// <param name="value">Il nuovo valore della proprietà.</param>
    /// <param name="type">Il tipo del valore.</param>
    /// <param name="setter">Identifica chi ha modificato il valore.</param>
    /// <param name="getters">Lista di chi ha richiesto notifiche sui cambiamenti.</param>
    /// <param name="isProcessingNotification">Indica se questa notifica fa parte di un ciclo di elaborazione in corso.</param>
    protected void OnDataChanges(string SessionId, List<string> properties,string setter, List<string> getters, bool isProcessingNotification = false)
    {
        getters = getters ?? new List<string>();
        DataChangesWithActor?.Invoke(this, new DataChangesWithActorEventArgs(SessionId, properties,setter, getters , isProcessingNotification));
    }

}

/// <summary>
/// Gestisce le istanze di RazorBlazorDataExchange per i circuiti Blazor.
/// </summary>
public class RazorBlazorCircuitHandler : CircuitHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, RazorBlazorDataExchange> _instances = new();

    /// <summary>
    /// Inizializza una nuova istanza della classe <see cref="RazorBlazorCircuitHandler"/>.
    /// </summary>
    /// <param name="scopeFactory">Factory per creare scope di servizio.</param>
    public RazorBlazorCircuitHandler(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Crea nuova istanza quando si apre un circuito.
    /// </summary>
    /// <param name="circuit">Il circuito Blazor.</param>
    /// <param name="cancellationToken">Token di cancellazione.</param>
    /// <returns>Un'attività completata.</returns>
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var instance = scope.ServiceProvider.GetRequiredService<RazorBlazorDataExchange>();
        _instances.TryAdd(circuit.Id, instance);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Rimuove l'istanza alla chiusura del circuito.
    /// </summary>
    /// <param name="circuit">Il circuito Blazor.</param>
    /// <param name="cancellationToken">Token di cancellazione.</param>
    /// <returns>Un'attività completata.</returns>
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _instances.TryRemove(circuit.Id, out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Recupera l'istanza per un dato circuitId.
    /// </summary>
    /// <param name="circuitId">Identificatore del circuito.</param>
    /// <returns>L'istanza di <see cref="RazorBlazorDataExchange"/>.</returns>
    public RazorBlazorDataExchange GetInstance(string circuitId)
    {
        return _instances.TryGetValue(circuitId, out var instance) ? instance : null;
    }
}

/// <summary>
/// Provider per accedere alle istanze di RazorBlazorDataExchange.
/// </summary>
public class RazorBlazorDataExchangeProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly RazorBlazorCircuitHandler _blazorProvider;
    private static readonly ConcurrentDictionary<string, RazorBlazorDataExchange> _userInstances = new();

    /// <summary>
    /// Inizializza una nuova istanza della classe <see cref="RazorBlazorDataExchangeProvider"/>.
    /// </summary>
    /// <param name="httpContextAccessor">Accessor per il contesto HTTP.</param>
    /// <param name="blazorProvider">Provider per i circuiti Blazor.</param>
    public RazorBlazorDataExchangeProvider(IHttpContextAccessor httpContextAccessor, RazorBlazorCircuitHandler blazorProvider)
    {
        _httpContextAccessor = httpContextAccessor;
        _blazorProvider = blazorProvider;
    }

    /// <summary>
    /// Recupera o crea un'istanza per Blazor o Razor Pages.
    /// </summary>
    /// <param name="circuitId">Identificatore del circuito (opzionale).</param>
    /// <returns>L'istanza di <see cref="RazorBlazorDataExchange"/>.</returns>
    public RazorBlazorDataExchange GetOrCreate(string circuitId = null)
    {
        if (!string.IsNullOrEmpty(circuitId))
        {
            var instance = _blazorProvider.GetInstance(circuitId);
            if (instance != null) return instance;
        }

        var context = _httpContextAccessor.HttpContext;
        if (context == null) throw new InvalidOperationException("HttpContext non disponibile");

        if (_httpContextAccessor?.HttpContext == null || _httpContextAccessor.HttpContext.Session == null)
        {
            return new RazorBlazorDataExchange();
        }

        context.Session.SetString("SessionInitialized", "true");
        var sessionId = context.Session.Id;

        return _userInstances.GetOrAdd(sessionId, _ => new RazorBlazorDataExchange());
    }

    /// <summary>
    /// Rimuove sessioni inattive.
    /// </summary>
    /// <param name="timeout">Timeout per considerare una sessione inattiva.</param>
    public void CleanupInactiveSessions(TimeSpan timeout)
    {
        var now = DateTime.UtcNow;
        foreach (var key in _userInstances.Keys)
        {
            if (now - _userInstances[key].LastAccess > timeout)
            {
                _userInstances.TryRemove(key, out _);
            }
        }
    }
}

// <summary>
/// Classe che rappresenta i metadati associati a un valore memorizzato.
/// </summary>
public class ObjectValueMetadata
{
    /// <summary>
    /// Il valore dell'oggetto.
    /// </summary>
    public object Value { get; set; }

    /// <summary>
    /// Il tipo del valore.
    /// </summary>
    public Type Type { get; set; }

    /// <summary>
    /// Identificatore di chi ha modificato per ultimo il valore.
    /// </summary>
    public string LastModifiedBy { get; set; }

    /// <summary>
    /// Data e ora dell'ultima modifica.
    /// </summary>
    public DateTime LastModifiedAt { get; set; }

    /// <summary>
    /// Cronologia delle modifiche.
    /// </summary>
    public List<ModificationHistory> ModificationHistory { get; set; } = new List<ModificationHistory>();
}

/// <summary>
/// Classe che rappresenta una singola modifica nella cronologia.
/// </summary>
public class ModificationHistory
{
    /// <summary>
    /// Identificatore di chi ha modificato il valore.
    /// </summary>
    public string ModifiedBy { get; set; }

    /// <summary>
    /// Data e ora della modifica.
    /// </summary>
    public DateTime ModifiedAt { get; set; }

    /// <summary>
    /// Il valore precedente dell'oggetto (opzionale).
    /// </summary>
    public object PreviousValue { get; set; }
}