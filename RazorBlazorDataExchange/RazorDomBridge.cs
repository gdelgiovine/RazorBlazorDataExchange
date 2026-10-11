using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;

namespace RazorBlazorDataExchange.Dom;

/// <summary>
/// Request-scoped Razor Pages DOM-command bridge. PageModel .NET event handlers
/// enqueue typed DOM commands; a long-running Razor SSE request streams them to
/// the browser. No Blazor component or JSInterop dependency is required.
/// </summary>
public sealed class RazorDomBridge : IDisposable
{
    private readonly Channel<string> _frames = Channel.CreateBounded<string>(
        new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private int _disposed;
    private int _streamOpened;
    private long _sequence;

    public void SetText(string id, object? value) =>
        Batch(Command.Text(id, value));

    public void SetValue(string id, object? value) =>
        Batch(Command.Value(id, value));

    public void SetChecked(string id, bool value) =>
        Batch(Command.Checked(id, value));

    public void SetVisible(string id, bool visible) =>
        Batch(Command.Visible(id, visible));

    public void SetClass(string id, string className, bool enabled) =>
        Batch(Command.CssClass(id, className, enabled));

    public void SetAttribute(string id, string attributeName, string? value) =>
        Batch(Command.Attribute(id, attributeName, value));

    /// <summary>
    /// Enqueue related UI mutations as a single ordered SSE notification.
    /// The JavaScript dispatcher supports only a fixed whitelist of safe ops.
    /// </summary>
    public void Batch(params Command[] commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Length == 0 || Volatile.Read(ref _disposed) != 0)
            return;

        foreach (var command in commands)
        {
            if (string.IsNullOrWhiteSpace(command.Id) ||
                string.IsNullOrWhiteSpace(command.Op))
                throw new ArgumentException("DOM commands require a nonempty operation and element id.", nameof(commands));
        }

        var frame = JsonSerializer.Serialize(new
        {
            sequence = Interlocked.Increment(ref _sequence),
            commands
        }, JsonOptions);

        _frames.Writer.TryWrite(frame);
    }

    /// <summary>
    /// Serve the SSE stream for a single connected browser tab. The caller
    /// keeps its own IndexModel.DataChanged handler subscribed during this task.
    /// 'onConnected' supplies the current broker snapshot on initial connection
    /// and after an EventSource reconnection, without manufacturing an event.
    /// </summary>
    public async Task StreamAsync(
        HttpResponse response,
        CancellationToken cancellationToken,
        Action? onConnected = null)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (Interlocked.Exchange(ref _streamOpened, 1) != 0)
            throw new InvalidOperationException("A RazorDomBridge instance serves one SSE request.");

        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache, no-store";
        response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            await response.StartAsync(cancellationToken);
            await response.WriteAsync("event: ready\ndata: {}\n\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);

            onConnected?.Invoke();

            await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken))
            {
                await response.WriteAsync($"event: dom\ndata: {frame}\n\n", cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the browser navigates away or closes its SSE stream.
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on client disconnect.
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _frames.Writer.TryComplete();
    }

    public sealed record Command(string Op, string Id, object? Value, string? Name = null)
    {
        public static Command Text(string id, object? value) =>
            new("setText", id, value);

        public static Command Value(string id, object? value) =>
            new("setValue", id, value);

        public static Command Checked(string id, bool value) =>
            new("setChecked", id, value);

        public static Command Visible(string id, bool visible) =>
            new("setVisible", id, visible);

        public static Command CssClass(string id, string name, bool enabled) =>
            new("setClass", id, enabled, name);

        public static Command Attribute(string id, string name, string? value) =>
            new("setAttribute", id, value, name);
    }
}
