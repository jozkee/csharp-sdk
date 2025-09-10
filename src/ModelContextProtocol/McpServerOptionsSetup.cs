using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ModelContextProtocol;

/// <summary>
/// Configures the McpServerOptions using addition services from DI.
/// </summary>
/// <param name="serverHandlers">The server handlers configuration options.</param>
/// <param name="serverTools">Tools individually registered.</param>
/// <param name="serverPrompts">Prompts individually registered.</param>
/// <param name="serverResources">Resources individually registered.</param>
internal sealed class McpServerOptionsSetup(
    IOptions<McpServerHandlers> serverHandlers,
    IEnumerable<McpServerTool> serverTools,
    IEnumerable<McpServerPrompt> serverPrompts,
    IEnumerable<McpServerResource> serverResources) : IConfigureOptions<McpServerOptions>
{
    /// <summary>
    /// Configures the given McpServerOptions instance by setting server information
    /// and applying custom server handlers and tools.
    /// </summary>
    /// <param name="options">The options instance to be configured.</param>
    public void Configure(McpServerOptions options)
    {
        Throw.IfNull(options);

        // Collect all of the provided tools into a tools collection. If the options already has
        // a collection, add to it, otherwise create a new one. We want to maintain the identity
        // of an existing collection in case someone has provided their own derived type, wants
        // change notifications, etc.
        McpServerPrimitiveCollection<McpServerTool> toolCollection = options.ToolCollection ?? [];
        foreach (var tool in serverTools)
        {
            toolCollection.TryAdd(tool);
        }

        if (!toolCollection.IsEmpty)
        {
            options.ToolCollection = toolCollection;
        }

        // Collect all of the provided prompts into a prompts collection. If the options already has
        // a collection, add to it, otherwise create a new one. We want to maintain the identity
        // of an existing collection in case someone has provided their own derived type, wants
        // change notifications, etc.
        McpServerPrimitiveCollection<McpServerPrompt> promptCollection = options.PromptCollection ?? [];
        foreach (var prompt in serverPrompts)
        {
            promptCollection.TryAdd(prompt);
        }

        if (!promptCollection.IsEmpty)
        {
            options.PromptCollection = promptCollection;
        }

        // Collect all of the provided resources into a resources collection. If the options already has
        // a collection, add to it, otherwise create a new one. We want to maintain the identity
        // of an existing collection in case someone has provided their own derived type, wants
        // change notifications, etc.
        McpServerResourceCollection resourceCollection = options.ResourceCollection ?? [];
        foreach (var resource in serverResources)
        {
            resourceCollection.TryAdd(resource);
        }

        if (!resourceCollection.IsEmpty)
        {
            options.ResourceCollection = resourceCollection;
        }

        // Apply custom server handlers.
        OverwriteWithSetHandlers(serverHandlers.Value, options);
    }

    /// <summary>
    /// Overwrite any handlers in McpServerOptions with non-null handlers from this instance.
    /// </summary>
    private static void OverwriteWithSetHandlers(McpServerHandlers handlers, McpServerOptions options)
    {
        PromptsCapability? promptsCapability = options.Capabilities?.Prompts;
        if (handlers.ListPromptsHandler is not null || handlers.GetPromptHandler is not null)
        {
            promptsCapability ??= new();
            options.ListPromptsHandler = handlers.ListPromptsHandler ?? options.ListPromptsHandler;
            options.GetPromptHandler = handlers.GetPromptHandler ?? options.GetPromptHandler;
        }

        ResourcesCapability? resourcesCapability = options.Capabilities?.Resources;
        if (handlers.ListResourcesHandler is not null || handlers.ReadResourceHandler is not null)
        {
            resourcesCapability ??= new();
            options.ListResourceTemplatesHandler = handlers.ListResourceTemplatesHandler ?? options.ListResourceTemplatesHandler;
            options.ListResourcesHandler = handlers.ListResourcesHandler ?? options.ListResourcesHandler;
            options.ReadResourceHandler = handlers.ReadResourceHandler ?? options.ReadResourceHandler;

            if (handlers.SubscribeToResourcesHandler is not null || handlers.UnsubscribeFromResourcesHandler is not null)
            {
                options.SubscribeToResourcesHandler = handlers.SubscribeToResourcesHandler ?? options.SubscribeToResourcesHandler;
                options.UnsubscribeFromResourcesHandler = handlers.UnsubscribeFromResourcesHandler ?? options.UnsubscribeFromResourcesHandler;
                resourcesCapability.Subscribe = true;
            }
        }

        ToolsCapability? toolsCapability = options.Capabilities?.Tools;
        if (handlers.ListToolsHandler is not null || handlers.CallToolHandler is not null)
        {
            toolsCapability ??= new();
            options.ListToolsHandler = handlers.ListToolsHandler ?? options.ListToolsHandler;
            options.CallToolHandler = handlers.CallToolHandler ?? options.CallToolHandler;
        }

        LoggingCapability? loggingCapability = options.Capabilities?.Logging;
        if (handlers.SetLoggingLevelHandler is not null)
        {
            loggingCapability ??= new();
            options.SetLoggingLevelHandler = handlers.SetLoggingLevelHandler;
        }

        CompletionsCapability? completionsCapability = options.Capabilities?.Completions;
        if (handlers.CompleteHandler is not null)
        {
            options.CompleteHandler = handlers.CompleteHandler;
        }

        options.Capabilities ??= new();
        options.Capabilities.Prompts = promptsCapability;
        options.Capabilities.Resources = resourcesCapability;
        options.Capabilities.Tools = toolsCapability;
        options.Capabilities.Logging = loggingCapability;
        options.Capabilities.Completions = completionsCapability;
    }
}
