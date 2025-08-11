using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Reflection;

/// <summary>
/// La classe ObjectFactory fornisce metodi per creare istanze di oggetti dinamicamente da un assembly specificato.
/// </summary>
public class ObjectFactory
{
    /// <summary>
    /// Crea un'istanza di un oggetto da un assembly specificato e un nome di classe.
    /// </summary>
    /// <param name="assemblyFileName">Il nome del file dell'assembly da cui caricare il tipo.</param>
    /// <param name="className">Il nome completo della classe da istanziare.</param>
    /// <returns>Un'istanza dell'oggetto creato.</returns>
    /// <exception cref="InvalidOperationException">Sollevata se il tipo specificato non viene trovato nell'assembly.</exception>
    public static object CreateInstance(string assemblyFileName, string className, object[] args = null)
    {
        // Carica l'assembly dal file
        var assembly = Assembly.LoadFrom(assemblyFileName);

        // Ottieni il tipo della classe
        var type = assembly.GetType(className);
        if (type == null)
        {
            throw new InvalidOperationException($"Tipo '{className}' non trovato nell'assembly '{assemblyFileName}'.");
        }

        // Crea un'istanza dell'oggetto
        return Activator.CreateInstance(type, args);
    }

    public static DbContext CreateDbContext(DbContextInfo contextInfo)
    {
        try
        {
            // 1. Carica l'assembly
            var assembly = Assembly.LoadFrom(contextInfo.Location);

            // 2. Ottieni il tipo del DbContext
            var contextType = assembly.GetType(contextInfo.FullName);
            if (contextType == null)
            {
                throw new InvalidOperationException($"Tipo '{contextInfo.FullName}' non trovato nell'assembly '{contextInfo.Location}'.");
            }

            // 3. Crea il builder di options specifico per il tipo di DbContext
            var optionsBuilderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
            var optionsBuilder = (DbContextOptionsBuilder)Activator.CreateInstance(optionsBuilderType);

            // 4. Configura il provider
            switch (contextInfo.DatabaseProvider)
            {

                case "Microsoft.EntityFrameworkCore.SqlServer":
                    optionsBuilder.UseSqlServer(contextInfo.ConnectionString);
                    break;
                //case "Microsoft.EntityFrameworkCore.MySql":
                //    optionsBuilder.UseMySql(contextInfo.ConnectionString, ServerVersion.AutoDetect(contextInfo.ConnectionString));
                //    break;
                //case "Microsoft.EntityFrameworkCore.Npgsql":
                //    optionsBuilder.UseNpgsql(contextInfo.ConnectionString);
                //    break;
                default:
                    optionsBuilder.UseSqlServer(contextInfo.ConnectionString);
                    break;
            }

            // 5. Crea l'istanza del DbContext usando il costruttore appropriato
            var options = optionsBuilder.Options;
            var constructor = contextType.GetConstructor(new[] { typeof(DbContextOptions<>).MakeGenericType(contextType) });

            if (constructor == null)
            {
                throw new InvalidOperationException(
                    $"Nessun costruttore adatto trovato in {contextType.Name}. " +
                    $"Il DbContext deve avere un costruttore che accetta DbContextOptions<{contextType.Name}>.");
            }

            return (DbContext)constructor.Invoke(new[] { options });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Errore durante la creazione del DbContext: {ex.Message}", ex);
        }
    }

    public static TContext CreateDbContext<TContext>(DbContextInfo contextInfo) where TContext : DbContext
    {
        return (TContext)CreateDbContext(contextInfo);
    }
}