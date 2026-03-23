# RazorBlazorDataExchange

`RazorBlazorDataExchange` is a .NET 8 solution that provides a **bidirectional communication channel between ASP.NET Core Razor Pages and Blazor Server components**.

The project is designed to support **incremental Blazor adoption** inside existing Razor Pages applications by enabling **shared state**, **change notifications**, and **synchronized UI updates** between the hosting Razor page and embedded Blazor components.

## Features

- Shared state between Razor Pages and Blazor Server components
- Session-based isolation of exchanged data
- Event-driven state propagation in both directions
- Optional JavaScript interop for host page updates
- Protection against recursive or circular notifications
- Example solution showing real integration in an ASP.NET Core app

## Solution Structure

### `RazorBlazorDataExchange`
Core library containing the data exchange engine and supporting infrastructure.

Main classes:
- `RazorBlazorDataExchange`
- `RazorBlazorCircuitHandler`
- `RazorBlazorDataExchangeProvider`
- event argument and metadata classes

### `BlazorComponents`
Sample Blazor component library that consumes the shared service.

Main component:
- `CounterComponent.razor`

### `EFHelper`
Auxiliary project with Entity Framework Core dependencies for future or extended scenarios.

### `RazorBlazorDataExchangeTester`
ASP.NET Core demo application used to test and showcase the integration pattern.

## Architecture Overview

The core service, `RazorBlazorDataExchange`, acts as an **in-memory shared store** and an **event dispatcher**.

It allows components and pages to:

- store values associated with a `sessionId`
- retrieve previously stored values
- notify state changes
- process only relevant notifications

Values are internally stored using a composite key:

```text
sessionId:propertyName

This ensures separation across user sessions.

Core Concepts
Shared Store

The service stores values and metadata such as:

current value
value type
last modifier
timestamp
Event-Based Notifications

The library exposes enriched notification events:

DataChangeWithActorEventArgs
DataChangesWithActorEventArgs

These events carry contextual information such as:

session identifier
property name(s)
sender/setter
current value
processing state
Notification Filtering

The method:

ShouldProcessEvent(string sessionId, string setter, DataChangeWithActorEventArgs e)

prevents invalid or circular updates by ignoring events when:

the session does not match
the sender is the same as the receiver
the event is already marked as being processed
Blazor Integration

RazorBlazorCircuitHandler tracks RazorBlazorDataExchange instances per Blazor Server circuit.

RazorBlazorDataExchangeProvider bridges:

Razor Pages through HttpContext and session
Blazor Server through circuit tracking

This allows the correct shared instance to be resolved depending on the execution context.

Demo Component

CounterComponent.razor demonstrates how a Blazor component can:

receive a session identifier from a Razor Page
update a shared value
notify other participants through the exchange service
invoke JavaScript in the host page via IJSRuntime
Demo Application

The RazorBlazorDataExchangeTester project includes:

service registration
session configuration
Razor Pages host
Blazor Server setup
synchronized counter example
Example Flow

Razor -> Blazor

The user clicks a button in the Razor Page
The page updates the shared value
RazorBlazorDataExchange raises a notification
The Blazor component receives the event and updates its UI

Blazor -> Razor

The user clicks a button in the Blazor component
The component updates the shared value
RazorBlazorDataExchange raises a notification
The Razor Page can react through shared state and/or JavaScript interop
Technologies
.NET 8
ASP.NET Core Razor Pages
Blazor Server
Dependency Injection
HTTP Session
JavaScript Interop
Entity Framework Core
Getting Started
Prerequisites
.NET 8 SDK
Visual Studio 2022 or later, or the dotnet CLI
Run with Visual Studio
Open RazorBlazorDataExchange.sln
Set RazorBlazorDataExchangeTester as the startup project
Run the application
Run with .NET CLI
dotnet build
dotnet run --project RazorBlazorDataExchangeTester
Use Cases

This approach is useful when:

migrating an existing Razor Pages application toward Blazor
embedding interactive Blazor components into traditional Razor views
maintaining shared UI state across different rendering models
coordinating updates between server-rendered pages and interactive components
Notes
The project is a practical integration pattern and demo implementation
Some sections of the codebase appear prepared for future extensions
The EFHelper project is currently ancillary to the main Razor/Blazor exchange scenario
License

Copyright (c) Gabriele Del Giovine 2025

Released under the GNU/GPL v3 license.
