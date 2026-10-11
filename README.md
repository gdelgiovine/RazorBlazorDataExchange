# RazorBlazorDataExchange

`RazorBlazorDataExchange` is a .NET 8 library and sample solution that provides a **bidirectional in-process communication channel between ASP.NET Core Razor Pages/MVC requests and Blazor Server components**.

The main use case is **incremental Blazor adoption inside existing Razor Pages applications**: a traditional Razor page can host one or more Blazor Server components while Razor and Blazor continue to exchange shared state and change notifications in both directions.

Current core version: **0.6.4**.

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

The broker is singleton, but **user data is not global**. State and subscriptions are partitioned by `SessionId`.

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

## 0.6.4 — Direct event handler in the Razor PageModel

The original sample's request-bound Razor `IndexModel.OnDataChanged` handler is restored and updated to the first-class `DataChanged` event API. The PageModel subscribes directly and uses session/actor/property filtering to update its `XCounter` model property when Blazor publishes a change **during that Razor HTTP request**.

The Razor PageModel implements `IDisposable` and is registered for end-of-response disposal. This is critical because the broker is Singleton: completed PageModels must unsubscribe or the broker would retain references to dead HTTP request objects.

The existing long-lived `RazorChangeEventObserver`, Razor label refresh, JS-bridge checkbox, and both Blazor component subscriptions are unchanged. The observer continues to handle events **after** the request finishes.

The restored handler updates **server-side PageModel state only**. It cannot directly update static HTML from a response that has already been delivered. With the JS checkbox unchecked, the counter inside the existing Razor DOM still does not update live; Blazor components, the server observer, and diagnostic labels continue to receive changes. This matches Razor Pages' request-scoped rendering lifecycle, including in the original implementation.

An integrated regression test now checks active-request Razor event receipt, rejection of other sessions and same-actor echoes, idempotent cleanup, and continued observer activity after PageModel disposal.

## First-class change events in Razor and Blazor

Version 0.6.1 makes change events a first-class, UI-technology-neutral contract:

```csharp
exchange.DataChanged += OnDataChanged;
```

`DataChanged` is raised for every committed single-property `Publish` / `Update`, independently of whether the publisher is Razor/MVC, Blazor, or another server-side participant. The event carries:

- `SessionId`
- `PropertyName`
- `Value` / `ValueType`
- `ActorId`
- `MessageId`
- `CorrelationId`
- `Version`
- `Timestamp`

Razor/MVC and Blazor can use the same filtering helper:

```csharp
if (exchange.ShouldProcessChange(sessionId, actorId, e, "Counter"))
{
    // process a change from another actor in this session
}
```

A Blazor component can subscribe directly and unsubscribe during `Dispose`. A Razor `PageModel` is request-scoped and therefore cannot remain alive after its HTTP response; long-lived Razor/MVC event consumption belongs in a server-side service/observer used by subsequent PageModel requests.

JavaScript is **not required for the change event path**. The sample keeps JS interop as an independent optional last-mile bridge when a Blazor-originated change should immediately mutate DOM that was already rendered by Razor:

```text
application/state change:
Blazor -> DataChanged -> Razor/MVC server observer     (no JS)

optional live DOM bridge:
Blazor -> IJSRuntime -> existing Razor-rendered DOM    (JS)
```

By default, **both** Blazor counters enable the optional JS DOM bridge, so either button also updates the already-rendered Razor counter immediately. The event-only mode remains independently testable by opening the tester with `/?disableCounter2JsBridge=true`: this disables only Counter2's JS bridge while keeping `DataChanged` active for Razor/MVC server observers and Blazor subscribers. Browser E2E tests cover both modes.

## 0.6.3 — Razor change-event labels and live JS bridge checkbox

The tester renders `Actor`, `Property`, `Value`, and `Version` directly from the **Razor server-side `RazorChangeEventObserver`**, not from JS callback parameters. A read-only Razor Page handler, `GET /Index?handler=LastChange`, exposes the last recorded event for the current ASP.NET session. Browser-side diagnostic labels refresh approximately every 400 ms; this polling is only for presenting the server observer's state, **not** for broker communication.

The Razor page includes the checkbox **Use JavaScript bridge to update the Razor counter from Blazor**. It is checked by default. Changing it calls `POST /Index?handler=SetJsBridge` using the existing anti-forgery token; the server publishes `JsDomBridgeEnabled` through the Singleton broker, and both Blazor components consume that property using the same `DataChanged` event as other state changes.

- **Checked:** Razor/Blazor event communication remains active, and both Blazor buttons immediately update the already-rendered Razor counter via the optional JS bridge.
- **Unchecked:** `DataChanged` continues to synchronize server state, Razor observers and Blazor components. Only the Blazor-to-Razor **live counter DOM bridge** is disabled. The Razor event-diagnostic labels still refresh from the server observer using their independent browser-side fetch.
- The checkbox setting is stored per exchange session and is restored on later Razor requests; toggling it requires no reload.
- The earlier `?disableCounter2JsBridge=true` query flag remains supported as a component-specific host override for compatibility.

This checkbox does **not** disable all JavaScript in the Razor page: Razor-hosted buttons and read-only diagnostic labels still require browser code. A normal non-interactive Razor PageModel cannot re-render already-delivered HTML merely because a server-side .NET event fired.

Browser E2E tests now verify live labels for Razor, Counter1 and Counter2, and checkbox off/on behavior with event delivery independent from the JS bridge.

## Version 0.6 / 0.6.1 / 0.6.2 / 0.6.3 features

Version 0.6 preserves all 0.5 broker behavior and adds optional ordering plus a transport service-provider interface.

Core broker features include:

- intentional singleton cross-scope broker;
- per-session state and subscription partitions;
- full Razor -> Blazor and Blazor -> Razor publication symmetry;
- fan-out to multiple Blazor components in the same session;
- fan-out across multiple Blazor Server/SignalR circuits that share the same exchange session;
- property-specific typed subscriptions;
- typed and raw session-wide subscriptions;
- unique per-instance `ActorId` so equal logical components in different circuits remain distinct actors;
- disposable subscription handles;
- typed `Publish<T>` / `PublishAsync<T>` APIs;
- atomic `Update<T>` / `UpdateAsync<T>` read-modify-write operations;
- typed `Get<T>` / `TryGet<T>` APIs;
- `MessageId`, `CorrelationId`, per-property `Version`, and timestamp metadata;
- correlation-based recursive-loop suppression;
- per-session locking;
- subscriber exception isolation;
- synchronous and asynchronous dispatch paths;
- inactive-session cleanup, metrics and `ILogger` diagnostics;
- backward-compatible legacy notification events;
- circuit registry that never replaces or scopes the singleton broker.

New in 0.6:

- optional `RazorBlazorOrderedDataExchange` facade;
- ordering by `(SessionId, PropertyName)` or by the entire `SessionId`;
- completion ordering that includes asynchronous subscriber completion;
- reentrant same-lane handling to avoid notification-chain deadlocks;
- on-demand reference-counted ordering lanes that retire automatically;
- ordered-exchange metrics;
- `IRazorBlazorDataExchangeTransport` infrastructure SPI;
- default broadcast `InMemoryRazorBlazorDataExchangeTransport`;
- JSON-based `RazorBlazorTransportEnvelope` suitable for future process boundaries;
- replaceable transport registration without changing the broker API;
- dedicated 0.6 ordering/transport tests in CI.

Detailed 0.6 architecture notes are in [`docs/v0.6-ordered-transport.md`](docs/v0.6-ordered-transport.md).

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

## Optional ordered publication

The normal broker remains unordered across independent concurrent publishers. When a workflow needs deterministic completion order, resolve `RazorBlazorOrderedDataExchange` and use its asynchronous methods.

Default ordering is per session/property:

```csharp
await orderedExchange.PublishAsync(
    sessionId,
    "Status",
    "Ready",
    actorId);
```

This serializes operations targeting the same `(SessionId, PropertyName)` while allowing different properties and sessions to progress concurrently.

For workflows that cascade across several related properties, serialize the entire session:

```csharp
await orderedExchange.UpdateAsync<int>(
    sessionId,
    "Counter",
    current => current + 1,
    actorId,
    orderingScope: RazorBlazorExchangeOrderingScope.Session);
```

A causally nested publication on a lane already held by the current notification chain executes inside that lane rather than attempting to acquire it again. This avoids self-deadlock during legitimate Razor/Blazor notification cascades.

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

## Transport abstraction

`IRazorBlazorDataExchangeTransport` is an infrastructure SPI for future multi-process adapters. The default implementation is `InMemoryRazorBlazorDataExchangeTransport`.

Its wire-oriented `RazorBlazorTransportEnvelope` contains:

- origin node id;
- session/property/actor identity;
- message and correlation ids;
- source version and timestamp;
- declared value type name;
- JSON payload.

The envelope deliberately avoids live CLR references and `System.Type` instances.

Replace the default transport through DI:

```csharp
services
    .AddRazorBlazorDataExchange()
    .UseRazorBlazorDataExchangeTransport<MyRedisTransport>();
```

where `MyRedisTransport` implements `IRazorBlazorDataExchangeTransport`.

**0.6 does not yet claim transparent distributed state replication.** A real Redis/Service Bus adapter still requires explicit policies for deduplication, remote/local version reconciliation, reconnect/replay, delivery guarantees, node-origin filtering, distributed ordering and transport authorization. The SPI is intentionally introduced before those policies so the proven 0.5 broker semantics are not silently changed.

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

- `RazorBlazorDataExchange` as singleton;
- `RazorBlazorOrderedDataExchange` as singleton;
- `IRazorBlazorDataExchangeTransport` with the in-memory singleton transport by default;
- `RazorBlazorCircuitHandler` as the Blazor `CircuitHandler`;
- `RazorBlazorDataExchangeProvider`;
- automatic inactive-session cleanup;
- required HTTP context access.

The circuit handler tracks circuits but **does not create one exchange instance per circuit**. All circuits and Razor/MVC requests resolve the same singleton broker.

## Solution structure

### `RazorBlazorDataExchange`

Core broker library. Important files/classes include:

- `RazorBlazorDataExchange`
- `ExchangeMessage` / `ExchangeMessage<T>`
- `RazorBlazorOrderedDataExchange`
- `IRazorBlazorDataExchangeTransport`
- `InMemoryRazorBlazorDataExchangeTransport`
- `RazorBlazorTransportEnvelope`
- `RazorBlazorDataExchangeOptions`
- `RazorBlazorCircuitHandler`
- `RazorBlazorDataExchangeProvider`
- `RazorBlazorDataExchangeCleanupService`
- DI service-collection extensions

### `BlazorComponents`

Sample Razor Class Library containing `CounterComponent.razor`.

The component demonstrates:

- logical component identity separated from concrete actor identity;
- typed subscription;
- subscription disposal;
- atomic updates;
- UI updates on the Blazor dispatcher;
- optional JS interop for immediate Blazor -> already-rendered Razor DOM updates.

### `RazorBlazorDataExchangeTester`

ASP.NET Core Razor Pages application demonstrating the complete hosting scenario.

The home page intentionally hosts two `CounterComponent` instances so Razor -> Blazor, Blazor -> Razor and Blazor -> Blazor fan-out can be observed directly.

The sample uses the classic Blazor Server hosting model because its purpose is embedding Blazor Server components in an existing Razor Pages application.

### `RazorBlazorDataExchange.StressTests`

Dependency-free executable test harness covering the core 0.5/0.6 broker invariants, including session isolation, actor filtering, bidirectional fan-out, 24,000 concurrent atomic updates, correlation-loop suppression, failure isolation, cleanup and metrics.

### `RazorBlazorDataExchange.V06Tests`

Executable test harness dedicated to 0.6 behavior:

- same-route ordered completion;
- independent property-lane concurrency;
- session-wide ordering across properties;
- reentrant same-route publication;
- concurrent ordered atomic updates;
- transport broadcast and failure isolation;
- JSON envelope round-trip;
- DI registration and transport replacement.

### `RazorBlazorDataExchange.E2ETests`

Playwright/Chromium end-to-end test harness that runs the real Razor Pages + Blazor Server application and validates the complete HTTP/SignalR/browser path.

The browser suite covers Razor -> Blazor, Blazor -> Razor DOM, Blazor -> peer Blazor, persistence across Razor reloads, cross-circuit fan-out in two tabs sharing one ASP.NET session, unique actor identity and independent-session isolation.

### `EFHelper`

Ancillary Entity Framework Core helper project. It is not required by the Razor/Blazor exchange broker itself.

## Concurrency and lifecycle

Core state synchronization is performed per session. Independent sessions therefore do not contend on one application-wide state lock.

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

Ordered-exchange lanes are created on demand and retired after the final active/waiting operation releases them.

## Metrics

`RazorBlazorDataExchange.GetMetrics()` reports broker sessions, values, subscriptions, publication/delivery counts, recursive suppression, delivery failures and removed sessions.

`RazorBlazorOrderedDataExchange.GetMetrics()` reports active ordering lanes, entered/completed/cancelled ordered operations and reentrant bypasses.

`IRazorBlazorDataExchangeTransport.GetMetrics()` reports transport subscribers, published/delivered envelopes and delivery failures.

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

Run the broker stress tests:

```bash
dotnet run --project RazorBlazorDataExchange.StressTests -c Release
```

Run the 0.6 ordering/transport tests:

```bash
dotnet run --project RazorBlazorDataExchange.V06Tests -c Release
```

For the browser end-to-end suite, build first and install Playwright Chromium:

```bash
pwsh RazorBlazorDataExchange.E2ETests/bin/Release/net8.0/playwright.ps1 install chromium
```

Start the tester on a known URL and run:

```bash
RBDX_BASE_URL=http://127.0.0.1:5087 \
  dotnet run --project RazorBlazorDataExchange.E2ETests -c Release --no-build
```

The GitHub Actions workflow performs solution build, broker stress tests, 0.6 ordering/transport tests, Chromium installation, tester startup and browser E2E validation automatically.

## Validated communication matrix

| Publisher | Receiver | Same circuit | Different circuit, same session | Different session |
|---|---|---:|---:|---:|
| Razor/MVC | Blazor | yes | yes | isolated |
| Blazor | Blazor | yes | yes | isolated |
| Blazor | Razor/MVC broker subscriber | yes | yes | isolated |
| Blazor | subsequent Razor/MVC request via shared state | yes | yes | isolated |
| Blazor | already-rendered Razor DOM | JS interop in originating page | not treated as a live Razor server UI | isolated |

The different-circuit cases are validated using two browser tabs that share the same ASP.NET session cookie while maintaining independent Blazor Server/SignalR circuits.

## Scope and scaling

The application broker is still **in-process**. The singleton lifetime is per ASP.NET Core process.

Version 0.6 provides a transport SPI and serialization-safe envelope so a later version can add cross-node publication without forcing application code to depend directly on Redis, Service Bus or another transport. Automatic distributed replication is intentionally not enabled until conflict, deduplication and delivery semantics are explicitly defined.

## Use cases

- incremental migration from Razor Pages/MVC to Blazor;
- embedding interactive Blazor Server islands into existing Razor applications;
- synchronizing state between Razor requests and live Blazor circuits;
- coordinating multiple Blazor components hosted by one Razor page;
- coordinating components in multiple circuits/tabs belonging to the same exchange session;
- deterministic completion ordering for selected session/property workflows;
- preparing the same application-facing broker model for a future distributed transport;
- preserving a traditional Razor/MVC application while progressively replacing individual UI areas with Blazor.

## License

Copyright (c) Gabriele Del Giovine 2025-2026.

Released under the GNU GPL v3 license.
