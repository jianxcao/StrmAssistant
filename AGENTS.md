# StrmAssistant - Agentic Coding Guidelines

## Build & Development Commands

### Build Commands
```bash
# Restore dependencies
dotnet restore StrmAssistant/StrmAssistant.Jellyfin.csproj

# Build release version
dotnet build StrmAssistant/StrmAssistant.Jellyfin.csproj -c Release --no-restore

# Build debug version
dotnet build StrmAssistant/StrmAssistant.Jellyfin.csproj -c Debug

# Publish plugin
dotnet publish StrmAssistant/StrmAssistant.Jellyfin.csproj -c Release -o publish-jellyfin
```

### Deployment
```bash
# Deploy to local Jellyfin (standard)
./deploy-plugin.sh

# Deploy with debug symbols (PDB)
./deploy-plugin.sh --with-pdb

# Restart Jellyfin after deployment
sudo systemctl restart jellyfin
```

### Testing
This project currently has no formal unit tests. Testing is done manually by deploying to Jellyfin and verifying functionality through:
- Plugin UI configuration page
- Scheduled task execution (Dashboard → Scheduled Tasks)
- Jellyfin logs: `tail -f /var/log/jellyfin/jellyfin.log`

## Code Style Guidelines

### Language & Framework
- Target Framework: .NET 8.0
- Language Version: `latest`
- Target Platform: Jellyfin 10.10.0+
- Build Configuration: Release (for production), Debug (for development)

### Naming Conventions

**Classes**: PascalCase
```csharp
public class MediaInfoService { }
public class JellyfinPlugin { }
public class PluginConfiguration { }
```

**Methods**: PascalCase, async methods end with `Async`
```csharp
public Task<bool> ExtractAndPersistMediaInfoAsync() { }
public void Initialize() { }
```

**Properties**: PascalCase
```csharp
public bool EnableMediaInfoExtraction { get; set; }
public string MediaInfoJsonRootFolder { get; set; }
```

**Private Fields**: `_camelCase` prefix
```csharp
private readonly ILogger<JellyfinPlugin> _logger;
private readonly MediaEncoderAdapter _mediaEncoder;
```

**Interfaces**: IPascalCase
```csharp
public interface IScheduledTask { }
public interface IPluginServiceRegistrator { }
```

**Parameters**: camelCase
```csharp
public void ProcessItem(BaseItem item, CancellationToken cancellationToken) { }
```

**Constants**: PascalCase or UPPER_CASE
```csharp
private static readonly Regex ChineseRegex = new Regex(@"[\u4E00-\u9FFF]");
public static readonly string[] MovieDbFallbackLanguages = { "zh-CN", "zh-TW" };
```

### Import/Using Order

Organize using statements in this order:
1. System namespace imports
2. External/Jellyfin libraries
3. Internal namespace imports (alphabetical)

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;

using StrmAssistant.Jellyfin.Adapters;
using StrmAssistant.Jellyfin.Services;
```

### Formatting Standards

- **Indentation**: 4 spaces (no tabs)
- **Braces**: Allman style (opening brace on new line)
- **Line Length**: No strict limit, but keep under 120 when practical
- **Blank Lines**: One blank line between methods, two between types

```csharp
public void Example()
{
    if (condition)
    {
        DoSomething();
    }
    else
    {
        DoSomethingElse();
    }
}
```

### Async/Await Patterns

Always use `async/await` for asynchronous operations, never `.Result` or `.Wait()`:
```csharp
// Correct
public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
{
    var items = await _libraryManager.GetItemsAsync(query, cancellationToken);
    await _mediaInfoService.BatchExtractMediaInfoAsync(items, progress, cancellationToken);
}

// Incorrect - will cause deadlocks
public void Execute()
{
    var items = _libraryManager.GetItemsAsync(query, cancellationToken).Result;
}
```

### Error Handling

- Always wrap operations in try-catch blocks
- Log errors with full exception details
- Return empty collections or null values rather than throwing
- Use pattern matching for null checks

```csharp
try
{
    var mediaInfo = await _mediaEncoder.ExtractMediaInfoAsync(item, cancellationToken);
    if (mediaInfo == null)
    {
        _logger.LogWarning("Failed to extract media info for {ItemName}", item.Name);
        return false;
    }
    return true;
}
catch (Exception ex)
{
    _logger.LogError(ex, "Error extracting media info for {ItemName}", item.Name);
    return false;
}
```

### Dependency Injection

Use constructor injection for all dependencies:
```csharp
public class MediaInfoService
{
    private readonly MediaEncoderAdapter _mediaEncoder;
    private readonly ILogger<MediaInfoService> _logger;

    public MediaInfoService(
        MediaEncoderAdapter mediaEncoder,
        ILogger<MediaInfoService> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }
}
```

### Logging

- Use structured logging with named parameters
- Use appropriate log levels: LogDebug, LogInformation, LogWarning, LogError
- Include context in all log messages

```csharp
_logger.LogInformation("Extracting media info for: {ItemName}", item.Name);
_logger.LogWarning("Failed to extract media info for {ItemName}", item.Name);
_logger.LogError(ex, "Error during media info extraction");
```

### XML Documentation

Public APIs should have XML documentation comments:
```csharp
/// <summary>
/// 媒体信息提取服务 - 使用 FFProbe 提取和持久化媒体流信息
/// </summary>
public class MediaInfoService
{
    /// <summary>
    /// 提取并持久化媒体信息
    /// </summary>
    /// <param name="item">媒体项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否成功</returns>
    public async Task<bool> ExtractAndPersistMediaInfoAsync(BaseItem item, CancellationToken cancellationToken = default)
    {
    }
}
```

### File Organization

- **Adapters/**: Abstraction layer for Jellyfin/Emby APIs
- **Services/**: Business logic and service classes
- **Providers/**: External data providers (TMDB, search, etc.)
- **ScheduledTasks/**: IScheduledTask implementations
- **Options/**: Configuration and UI classes
- **Common/**: Shared utilities and helpers
- **Mod/**: Harmony patches and runtime modifications

### Conditional Compilation

The project uses `JELLYFIN` define constant to exclude Emby-specific code:
```xml
<DefineConstants>JELLYFIN</DefineConstants>
```

Use conditional compilation for platform-specific code:
```csharp
#if JELLYFIN
    // Jellyfin-specific code
#else
    // Emby-specific code
#endif
```

### Jellyfin API Best Practices

- Always use `CancellationToken` for async operations
- Use `IProgress<double>` for long-running operations
- Leverage Jellyfin's built-in services (ILibraryManager, IMediaEncoder, etc.)
- Create adapter classes for Jellyfin APIs to maintain compatibility
- Use `BaseItemKind` enums instead of string comparisons

### Plugin Configuration

All configuration properties should have default values and XML documentation:
```csharp
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// 启用媒体信息提取
    /// </summary>
    public bool EnableMediaInfoExtraction { get; set; } = true;

    /// <summary>
    /// 媒体信息提取并发数
    /// </summary>
    public int MediaInfoConcurrency { get; set; } = 2;
}
```

### Static Helper Methods

For small utilities without state, use static classes:
```csharp
public static class CommonUtility
{
    public static bool IsValidHttpUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var uriResult)
            && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
    }
}
```

### String Formatting

Use string interpolation or named parameters for log messages:
```csharp
// Good - structured logging
_logger.LogInformation("Processing {Count} items in {FolderName}", items.Count, folder.Name);

// Good - interpolation for display
var message = $"Found {items.Count} items";

// Avoid - string concatenation for performance-critical paths
var message = "Found " + items.Count + " items";
```

### Nullable Reference Types

The project uses nullable reference types. Always handle nulls appropriately:
```csharp
// Check for null
if (string.IsNullOrEmpty(item.Name)) return false;

// Null coalescing
var name = item.Name ?? "Unknown";

// Null conditional
var parentId = item.Parent?.Id;
```

## Common Patterns

### Adapter Pattern
```csharp
public class MediaEncoderAdapter
{
    private readonly IMediaEncoder _mediaEncoder;
    public MediaEncoderAdapter(IMediaEncoder mediaEncoder) => _mediaEncoder = mediaEncoder;

    public async Task<MediaInfo> ExtractMediaInfoAsync(BaseItem item, CancellationToken ct)
    {
        // Abstract Jellyfin/Emby differences
    }
}
```

### Service Registration
```csharp
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton<MediaInfoService>();
        services.AddSingleton<MediaEncoderAdapter>();
    }
}
```

### Scheduled Task Implementation
```csharp
public class ExtractMediaInfoTask : IScheduledTask
{
    public string Name => "提取媒体信息";
    public string Key => "ExtractMediaInfo";
    public string Description => "Use FFProbe to extract media stream info";
    public string Category => "StrmAssistant";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Implementation
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[] { new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerLibraryScan } };
    }
}
```
