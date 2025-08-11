using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

/// <summary>
/// Classe di supporto per Entity Framework.
/// </summary>
public static class EFHelper
{
    /// <summary>
    /// Ottiene il contesto del database associato a un DbSet.
    /// </summary>
    /// <typeparam name="T">Il tipo di entità del DbSet.</typeparam>
    /// <param name="dbSet">Il DbSet da cui ottenere il contesto del database.</param>
    /// <returns>Il contesto del database associato al DbSet.</returns>
    public static DbContext GetDbContext<T>(this DbSet<T> dbSet) where T : class
    {
        //var infrastructure = dbSet as IInfrastructure<IServiceProvider>;
        //var serviceProvider = infrastructure.Instance;
        //var currentDbContext = serviceProvider.GetService(typeof(ICurrentDbContext)) as ICurrentDbContext;
        //return currentDbContext.Context;

        var serviceProvider = (dbSet as IInfrastructure<IServiceProvider>)?.Instance;
        return serviceProvider?.GetService<ICurrentDbContext>()?.Context
            ?? throw new InvalidOperationException("Impossibile ottenere il DbContext dal DbSet.");

    }

    /// <summary>
    /// Ottiene le informazioni sul contesto del database.
    /// </summary>
    /// <param name="dbContext">Il contesto del database da cui ottenere le informazioni.</param>
    /// <returns>Un oggetto DbContextInfo contenente le informazioni sul contesto del database.</returns>
    public static DbContextInfo GetDbContextInfo(DbContext dbContext)
    {
        var info = new DbContextInfo();
        var assembly = dbContext.GetType().Assembly;
        info.Name = dbContext.GetType().Name;
        info.FullName = dbContext.GetType().FullName;
        info.Location = assembly.Location;
      
        info.ConnectionString = dbContext.Database.GetDbConnection().ConnectionString;
        info.DatabaseProvider = dbContext.Database.ProviderName;

        return info;
    }

    /// <summary>
    /// Ottiene le informazioni su un DbSet.
    /// </summary>
    /// <typeparam name="T">Il tipo di entità del DbSet.</typeparam>
    /// <param name="dbSet">Il DbSet da cui ottenere le informazioni.</param>
    /// <returns>Un oggetto DbSetInfo contenente le informazioni sul DbSet.</returns>
    public static DbSetInfo GetDbSetInfo<T>(DbSet<T> dbSet) where T : class
    {
        var info = new DbSetInfo();
        var assembly = dbSet.GetType().Assembly;
        var dbContext = GetDbContext(dbSet);
        info.DbContextInfo = GetDbContextInfo(dbContext);
        info.Name = dbSet.GetType().Name;
        info.TableName = dbSet.GetTableName();
        info.QuerySQL = dbSet.ToQueryString();
        info.QueryLinq = dbSet.ToString();

        var (sqlQuery, parameters) = GetSqlQueryAndParameters(dbSet);

        info.QuerySQL = sqlQuery;
        info.Parameters = parameters;
        return info;
    }

    /// <summary>
    /// Ottiene il nome della tabella associata a un DbSet.
    /// </summary>
    /// <typeparam name="T">Il tipo di entità del DbSet.</typeparam>
    /// <param name="dbSet">Il DbSet da cui ottenere il nome della tabella.</param>
    /// <returns>Il nome della tabella associata al DbSet.</returns>
    public static string GetTableName<T>(this DbSet<T> dbSet) where T : class
    {
        var dbContext = dbSet.GetDbContext();
        var entityType = dbContext.Model.FindEntityType(typeof(T));
        return entityType?.GetTableName() ?? typeof(T).Name;
    }

    /// <summary>
    /// Ottiene la query SQL e i parametri associati a una query LINQ.
    /// </summary>
    /// <typeparam name="T">Il tipo di entità della query.</typeparam>
    /// <param name="query">La query LINQ da cui ottenere la query SQL e i parametri.</param>
    /// <returns>Una tupla contenente la query SQL e un dizionario dei parametri.</returns>
    public static (string SqlQuery, Dictionary<string, object> Parameters) GetSqlQueryAndParameters<T>(this IQueryable<T> query) where T : class
    {
        var sqlQuery = query.ToQueryString();
        var parameters = new Dictionary<string, object>();

        // Analizza la query SQL per identificare i parametri
        // Nota: Questo è un esempio semplificato e potrebbe non coprire tutti i casi
        var parameterMatches = System.Text.RegularExpressions.Regex.Matches(sqlQuery, @"@p\d+");
        foreach (System.Text.RegularExpressions.Match match in parameterMatches)
        {
            var parameterName = match.Value;
            var parameterValue = query.GetParameterValue(parameterName);
            parameters[parameterName] = parameterValue;
        }

        return (sqlQuery, parameters);
    }

    /// <summary>
    /// Ottiene il valore di un parametro associato a una query LINQ.
    /// </summary>
    /// <typeparam name="T">Il tipo di entità della query.</typeparam>
    /// <param name="query">La query LINQ da cui ottenere il valore del parametro.</param>
    /// <param name="parameterName">Il nome del parametro di cui ottenere il valore.</param>
    /// <returns>Il valore del parametro, se trovato; altrimenti, null.</returns>
    private static object GetParameterValue<T>(this IQueryable<T> query, string parameterName) where T : class
    {
        //// Implementa la logica per ottenere il valore del parametro
        //// Nota: Questo è un esempio semplificato e potrebbe non coprire tutti i casi
        //var parameterValues = query.GetType().GetField("_parameterValues", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(query) as IReadOnlyDictionary<string, object>;
        //if (parameterValues != null && parameterValues.TryGetValue(parameterName, out var value))
        //{
        //    return value;
        //}
        //return null;


        try
        {
            // Tenta diversi approcci di reflection per ottenere i parametri
            var queryCompiler = typeof(EntityFrameworkQueryableExtensions)
                .GetField("_queryCompiler", BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null);

            if (queryCompiler == null)
                return null;

            var queryContext = queryCompiler.GetType()
                .GetMethod("CreateQueryContext", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(queryCompiler, new object[] { query.Expression, false });

            if (queryContext == null)
                return null;

            var parameterValues = queryContext.GetType()
                .GetProperty("ParameterValues", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(queryContext) as IDictionary<string, object>;

            if (parameterValues != null && parameterValues.TryGetValue(parameterName.TrimStart('@'), out var value))
            {
                return value;
            }
        }
        catch
        {
            // Ignora errori di reflection
        }

        return null;
    }
}



/// <summary>
/// Classe che rappresenta le informazioni su un contesto del database.
/// </summary>
public class DbContextInfo
{
    /// <summary>
    /// Ottiene o imposta la stringa di connessione del contesto del database.
    /// </summary>
    public string ConnectionString { get; set; }

    /// <summary>
    /// Ottiene o imposta il nome dell'istanza del contesto del database.
    /// </summary>
    public string InstanceName { get; set; }

    /// <summary>
    /// Ottiene o imposta il nome del contesto del database.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Ottiene o imposta il nome dell'assembly del contesto del database.
    /// </summary>
    public string AssemblyName { get; set; }

    /// <summary>
    /// Ottiene o imposta il nome completo del contesto del database.
    /// </summary>
    public string FullName { get; set; }

    /// <summary>
    /// Ottiene o imposta la posizione dell'assembly del contesto del database.
    /// </summary>
    public string Location { get; set; }


    public string DatabaseProvider { get; set; }
}

/// <summary>
/// Classe che rappresenta le informazioni su un DbSet.
/// </summary>
public class DbSetInfo
{
    /// <summary>
    /// Ottiene o imposta le informazioni sul contesto del database associato al DbSet.
    /// </summary>
    public DbContextInfo DbContextInfo { get; set; }

    /// <summary>
    /// Ottiene o imposta il nome della tabella associata al DbSet.
    /// </summary>
    public string TableName { get; set; }

    /// <summary>
    /// Ottiene o imposta il nome del DbSet.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Ottiene o imposta il nome completo del DbSet.
    /// </summary>
    public string FullName { get; set; }

    /// <summary>
    /// Ottiene o imposta il nome dell'entità associata al DbSet.
    /// </summary>
    public string EntityName { get; set; }

    /// <summary>
    /// Ottiene o imposta il tipo di entità associato al DbSet.
    /// </summary>
    public IEntityType EntityType { get; set; }

    /// <summary>
    /// Ottiene o imposta la query SQL associata al DbSet.
    /// </summary>
    public string QuerySQL { get; set; }

    /// <summary>
    /// Ottiene o imposta la query LINQ associata al DbSet.
    /// </summary>
    public string QueryLinq { get; set; }

    /// <summary>
    /// Ottiene o imposta i parametri associati alla query SQL del DbSet.
    /// </summary>
    public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
}
