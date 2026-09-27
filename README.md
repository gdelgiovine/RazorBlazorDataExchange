# RazorBlazorDataExchange

`RazorBlazorDataExchange` is a .NET 8 library and sample solution that provides a **bidirectional in-process communication channel between ASP.NET Core Razor Pages/MVC requests and Blazor Server components**.

The main use case is **incremental Blazor adoption inside existing Razor Pages applications**: a traditional Razor page can host one or more Blazor Server components while Razor and Blazor continue to exchange shared state and change notifications in both directions.

Current core version: **0.5.0**.

## Why the broker is Singleton

The singleton lifetime is intentional and is part of the architecture.

A Razor/MVC request and a Blazor Server circuit do not share the same DI scope:

```text
Razor / MVC HTTP request                  Blazor Server circuit
-------------------------                 ---------------------
request scope                             circuit scope
       \                                      /
        \                                    /
         +---- RazorBlazorDataExchange -----+
                    SINGLETON
```

The singleton broker is therefore the common in-process rendezvous point between the HTTP request side and the Blazor Server/SignalR circuit side.

The broker is singleton, but **user data is not global**. Version 0.5 partitions state and subscriptions by `SessionId`.

## Bidirectional communication model

Razor and Blazor are symmetric broker participants: either side may publish or subscribe.

### Razor -> Blazor

```text
Razor HTTP request
      |
      | Publish / Update
      v
RazorBlazorDataExchange singleton
      |
      | session-aware fan-out
      v
one or more Blazor components
      |
      | InvokeAsync(StateHasChanged)
      v
Blazor Server renderer -> SignalR -> browser
```

A Razor publication is delivered to **all interested Blazor actors in the same session**, including multiple instances of the same component type and instances living in different Blazor Server circuits.

### Blazor -> Razor

```text
Blazor component
      |
      | Publish / Update
      v
RazorBlazorDataExchange singleton
      |
      +--> Razor/MVC server-side subscribers in the same session
      |
      +--> shared state available to subsequent Razor/MVC requests
      |
      +--> peer Blazor components in the same session
```

A Razor Page instance is not a live server-side UI after its HTTP response has been sent. For immediate updates of the already-rendered Razor DOM, the sample also uses `IJSRuntime`:

```text
Blazor component -> IJSRuntime -> JavaScript in Razor host page -> DOM
```

This JS interop path complements the broker; it does not replace the Blazor -> Razor shared-state path.

## Version 0.5 features

- Intentional singleton cross-scope broker
- Per-session state and subscription partitions
- Full Razor -> Blazor and Blazor -> Razor publication symmetry
- Fan-out to multiple Blazor components in the same session
- Fan-out across multiple Blazor Server/SignalR circuits that share the same exchange session
- Property-specific typed subscriptions
- Typed session-wide subscriptions
- Raw session-wide subscriptions with no property restriction
- Unique per-instance `ActorId` so multiple instances of the same logical component can communicate correctly, including across circuits
- Disposable subscription handles to prevent retained component/page references
- Typed `Publish<T>` / `PublishAsync<T>` APIs
- Atomic `Update<T>` / `UpdateAsync<T>` read-modify-write operations
- Typed `Get<T>` / `TryGet<T>` APIs
- `MessageId`, `CorrelationId`, per-property `Version`, and timestamp metadata
- Correlation-based recursive-loop suppression
- Null-safe message metadata
- Per-session locking instead of one application-wide state lock
- Subscriber exception isolation
- Synchronous and asynchronous dispatch paths
- `LastAccess` tracking and inactive-session cleanup
- Background cleanup service
- Runtime metrics snapshot
- `ILogger` diagnostics
- Backward-compatible legacy notification events
- Circuit registry that does not replace or scope the singleton broker
- Dependency-injection registration through `AddRazorBlazorDataExchange(...)`
- CI build, stress-test and browser end-to-end workflow

## Subscription model

### Property-specific typed subscription

```csharp
_subscription = exchange.Subscribe<int>(
    sessionId,
    "Counter",
    actorId,
    async (message, cancellationToken) =>
    {
        // React to Counter changes from any other actor in this session.
    });
```

### Session-wide typed subscription

```csharp
_subscription = exchange.SubscribeSession<int>(
    sessionId,
    actorId,
    message =>
    {
        // Receives every Int32 message in this session,
        // independently of the property name.
    });
```

### Session-wide unrestricted subscription

```csharp
_subscription = exchange.SubscribeSession(
    sessionId,
    actorId,
    message =>
    {
        // Receives every property/value type published in this session.
    });
```

The session partition is an isolation boundary, not a direction or circuit restriction. A session-wide subscriber can be implemented by Razor/MVC infrastructure, a Blazor component, or another server-side participant.

## Publishing and atomic updates

Publish a value:

```csharp
await exchange.PublishAsync(
    sessionId,
    "Status",
    "Ready",
    actorId);
```

Atomically modify a shared value:

```csharp
var message = await exchange.UpdateAsync<int>(
    sessionId,
    "Counter",
    current => current + 1,
    actorId);
```

The atomic update avoids the classic concurrent sequence:

```text
Get -> modify -> Store
```

where two HTTP requests or circuits could otherwise read the same old value and overwrite one another.

## Message envelope and actor identity

Typed publications expose an immutable `ExchangeMessage<T>` envelope containing:

- `SessionId`
- `PropertyName`
- `Value`
- `ActorId`
- `MessageId`
- `CorrelationId`
- `Version`
- `Timestamp`

`ActorId` identifies the **concrete participant instance**, not merely its component type or logical component name. This distinction is required because the same logical component can exist in multiple tabs/circuits at the same time.

The sample therefore keeps `ComponentId` as the logical host identifier but builds a Blazor actor identity from both the logical id and a per-instance GUID:

```text
Blazor:CounterComponent:CounterComponent1:<instance-guid>
```

Without the per-instance portion, two `CounterComponent1` instances in separate SignalR circuits would incorrectly classify each other's publications as self-notifications and suppress legitimate cross-circuit communication.

`CorrelationId` identifies a notification chain and is used to suppress recursive A -> B -> A re-publication loops without disabling normal communication between different actors.

## Legacy API compatibility

The original APIs remain available, including:

- `StoreValue`
- `GetValue`
- `GetValueMetadata`
- `NotifyDataChange`
- `NotifyDataChanges`
- `DataChangeWithActor`
- `DataChangesWithActor`
- `ShouldProcessEvent`

The legacy events continue to be raised for publications so existing integrations can migrate incrementally to the typed subscription API.

## Dependency injection

Register the broker with:

```csharp
services.AddRazorBlazorDataExchange(options =>
{
    options.SessionIdleTimeout = TimeSpan.FromMinutes(30);
    options.CleanupInterval = TimeSpan.FromMinutes(5);
    options.CorrelationRetention = TimeSpan.FromMinutes(2);
    options.MaxModificationHistory = 50;
});
```

The extension registers:

- `RazorBlazorDataExchange` as singleton
- `RazorBlazorCircuitHandler` as the Blazor `CircuitHandler`
- `RazorBlazorDataExchangeProvider`
- automatic inactive-session cleanup
- required HTTP context access

The circuit handler tracks circuits but **does not create one exchange instance per circuit**. All circuits and Razor/MVC requests resolve the same singleton broker.

## Solution structure

### `RazorBlazorDataExchange`

Core broker library.

Important files/classes include:

- `RazorBlazorDataExchange`
- `ExchangeMessage` / `ExchangeMessage<T>`
- `RazorBlazorDataExchangeOptions`
- `RazorBlazorCircuitHandler`
- `RazorBlazorDataExchangeProvider`
- `RazorBlazorDataExchangeCleanupService`
- DI service-collection extensions

### `BlazorComponents`

Sample Razor Class Library containing `CounterComponent.razor`.

The component demonstrates:

- logical component identity separated from concrete actor identity
- typed subscription
- subscription disposal
- atomic updates
- UI updates on the Blazor dispatcher
- JS interop for immediate Blazor -> Razor DOM updates

### `RazorBlazorDataExchangeTester`

ASP.NET Core Razor Pages application demonstrating the complete hosting scenario.

The home page intentionally hosts two `CounterComponent` instances so Razor -> Blazor, Blazor -> Razor and Blazor -> Blazor fan-out can be observed directly.

The sample uses the classic Blazor Server hosting model because its purpose is embedding Blazor Server components in an existing Razor Pages application.

### `RazorBlazorDataExchange.StressTests`

Dependency-free executable test harness covering:

- session isolation
- actor filtering
- Razor -> multiple Blazor fan-out
- Blazor -> Razor session-wide delivery
- Blazor -> peer Blazor delivery
- persistence of Blazor-originated state for subsequent Razor requests
- 24,000 concurrent atomic updates
- correlation-loop suppression
- subscriber failure isolation
- cleanup and metrics

### `RazorBlazorDataExchange.E2ETests`

Playwright/Chromium end-to-end test harness that runs the real Razor Pages + Blazor Server application and validates the complete HTTP/SignalR/browser path.

The browser suite covers:

- Razor -> two Blazor components on the same page
- Blazor component #1 -> Razor DOM + Blazor component #2
- Blazor component #2 -> Razor DOM + Blazor component #1
- persistence of Blazor-originated state across a later Razor request/reload
- two tabs sharing the same ASP.NET session but using independent Blazor Server circuits
- Razor publication fan-out across those circuits
- Blazor publication fan-out across those circuits
- unique actor identity for equal logical component ids in different circuits
- isolation between independent browser sessions

### `EFHelper`

Ancillary Entity Framework Core helper project. It is not required by the Razor/Blazor exchange broker itself.

## Concurrency and lifecycle

State synchronization is performed per session. Independent sessions therefore do not contend on one single application-wide state lock.

Subscriptions return `IDisposable` handles. Blazor components should dispose them when the component is destroyed:

```razor
@implements IDisposable
```

```csharp
public void Dispose()
{
    _subscription?.Dispose();
}
```

The automatic cleanup service does not remove an idle session that still contains active subscriptions unless explicitly configured to do so.

## Metrics

`GetMetrics()` returns a point-in-time snapshot containing:

- active sessions
- stored values
- active subscriptions
- published messages
- delivered messages
- suppressed recursive messages
- delivery failures
- removed sessions

The snapshot does not expose session identifiers or stored values.

## Running the sample and tests

Prerequisites:

- .NET 8 SDK
- Visual Studio 2022 or later, or the .NET CLI

Build the complete solution:

```bash
dotnet restore RazorBlazorDataExchange.sln
dotnet build RazorBlazorDataExchange.sln -c Release
```

Run the sample application:

```bash
dotnet run --project RazorBlazorDataExchangeTester
```

Run the 0.5 broker stress tests:

```bash
dotnet run --project RazorBlazorDataExchange.StressTests -c Release
```

For the browser end-to-end suite, build first and install the Playwright Chromium runtime:

```bash
pwsh RazorBlazorDataExchange.E2ETests/bin/Release/net8.0/playwright.ps1 install chromium
```

Start the tester on a known URL and run:

```bash
RBDX_BASE_URL=http://127.0.0.1:5087 \
  dotnet run --project RazorBlazorDataExchange.E2ETests -c Release --no-build
```

The GitHub Actions workflow performs build, stress tests, Chromium installation, tester startup and browser E2E validation automatically.

## Validated 0.5 communication matrix

| Publisher | Receiver | Same circuit | Different circuit, same session | Different session |
|---|---|---:|---:|---:|
| Razor/MVC | Blazor | yes | yes | isolated |
| Blazor | Blazor | yes | yes | isolated |
| Blazor | Razor/MVC broker subscriber | yes | yes | isolated |
| Blazor | subsequent Razor/MVC request via shared state | yes | yes | isolated |
| Blazor | already-rendered Razor DOM | JS interop in originating page | not treated as a live Razor server UI | isolated |

The different-circuit cases are validated using two browser tabs that share the same ASP.NET session cookie while maintaining independent Blazor Server/SignalR circuits.

## Scope and scaling

Version 0.5 is an **in-process broker**. The singleton lifetime is per ASP.NET Core process.

In a multi-node deployment, each server process has its own broker instance. A future distributed transport can be introduced behind the same exchange semantics if cross-node publication is required.

## Use cases

- Incremental migration from Razor Pages/MVC to Blazor
- Embedding interactive Blazor Server islands into existing Razor applications
- Synchronizing state between Razor requests and live Blazor circuits
- Coordinating multiple Blazor components hosted by one Razor page
- Coordinating components in multiple circuits/tabs belonging to the same exchange session
- Preserving a traditional Razor/MVC application while progressively replacing individual UI areas with Blazor

## License

Copyright (c) Gabriele Del Giovine 2025-2026.

Released under the GNU GPL v3 license.
