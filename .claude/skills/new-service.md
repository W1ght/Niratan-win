---
name: new-service
description: Scaffold a new Service + Interface + DI registration following Niratan conventions
---

# New Service Scaffold

Create a new service in Niratan following project conventions.

## Files to Create

| File | Purpose |
|---|---|
| `Niratan/Services/Xxx/IXxxService.cs` | Interface |
| `Niratan/Services/Xxx/XxxService.cs` | Implementation |
| Update `Niratan/App.xaml.cs` | DI registration |

If the service belongs to an existing namespace (Novels, Dictionary, Audio, etc.), add to that folder.

## Step 1: Interface

`Niratan/Services/Foo/IFooService.cs`:

```csharp
using System.Threading.Tasks;

namespace Niratan.Services.Foo;

public interface IFooService
{
    Task<FooResult> GetFooAsync(string id);
    Task SaveFooAsync(FooData data);
}
```

## Step 2: Implementation

`Niratan/Services/Foo/FooService.cs`:

```csharp
using System;
using System.Threading.Tasks;
using Serilog;

namespace Niratan.Services.Foo;

public sealed class FooService : IFooService
{
    // Inject dependencies via constructor
    private readonly IDataService _dataService;

    public FooService(IDataService dataService)
    {
        _dataService = dataService;
    }

    public async Task<FooResult> GetFooAsync(string id)
    {
        Log.Information("[Foo] Getting {Id}", id);
        // implementation
        throw new NotImplementedException();
    }

    public async Task SaveFooAsync(FooData data)
    {
        Log.Information("[Foo] Saving");
        // implementation
        throw new NotImplementedException();
    }
}
```

## Step 3: DI Registration

In `Niratan/App.xaml.cs`, add to the `ServiceCollection`:

```csharp
services.AddSingleton<IFooService, FooService>();
```

Use `AddSingleton` for stateless services (most cases). Use `AddTransient` only if the service holds per-use state.

## Rules

- Every service must have an interface
- Use `sealed class` for implementations
- Log with Serilog: `Log.Information`, `Log.Warning`, `Log.Error`
- No business logic in constructors — only store dependencies
- Async methods use `Task`/`Task<T>` return types
- Methods that touch the database must be async
- Use `[Foo]` prefix in log messages for filtering
- Services should NOT reference ViewModels or Views
