using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace ModelContextProtocol.Server;

/// <summary>
/// Provides a container for handlers used in the creation of an MCP server.
/// </summary>
/// <remarks>
/// <para>
/// This class provides a centralized collection of delegates that implement various capabilities of the Model Context Protocol.
/// Each handler in this class corresponds to a specific endpoint in the Model Context Protocol and
/// is responsible for processing a particular type of request. The handlers are used to customize
/// the behavior of the MCP server by providing implementations for the various protocol operations.
/// </para>
/// <para>
/// Handlers can be configured individually using the extension methods in <see cref="McpServerBuilderExtensions"/>
/// such as <see cref="McpServerBuilderExtensions.WithListToolsHandler"/> and
/// <see cref="McpServerBuilderExtensions.WithCallToolHandler"/>.
/// </para>
/// <para>
/// When a client sends a request to the server, the appropriate handler is invoked to process the
/// request and produce a response according to the protocol specification. Which handler is selected
/// is done based on an ordinal, case-sensitive string comparison.
/// </para>
/// </remarks>
public sealed class McpServerHandlers
{
    /// <summary>
    /// Gets or sets the handler for <see cref="RequestMethods.ToolsList"/> requests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handler should return a list of available tools when requested by a client.
    /// It supports pagination through the cursor mechanism, where the client can make
    /// repeated calls with the cursor returned by the previous call to retrieve more tools.
    /// </para>
    /// <para>
    /// This handler works alongside any tools defined in the <see cref="McpServerTool"/> collection.
    /// Tools from both sources will be combined when returning results to clients.
    /// </para>
    /// </remarks>
    public Func<RequestContext<ListToolsRequestParams>, CancellationToken, ValueTask<ListToolsResult>>? ListToolsHandler { get; set; }

    /// <summary>
    /// Gets or sets the handler for <see cref="RequestMethods.ToolsCall"/> requests.
    /// </summary>
    /// <remarks>
    /// This handler is invoked when a client makes a call to a tool that isn't found in the <see cref="McpServerTool"/> collection.
    /// The handler should implement logic to execute the requested tool and return appropriate results.
    /// </remarks>
    public Func<RequestContext<CallToolRequestParams>, CancellationToken, ValueTask<CallToolResult>>? CallToolHandler { get; set; }

    /// <summary>
    /// Gets or sets the handler for <see cref="RequestMethods.PromptsList"/> requests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This handler is invoked when a client requests a list of available prompts from the server
    /// via a <see cref="RequestMethods.PromptsList"/> request. Results from this handler are returned
    /// along with any prompts defined in the prompt collection.
    /// </para>
    /// <para>
    /// The handler supports pagination through the cursor mechanism where the client can make
    /// repeated calls with the cursor returned by the previous call to retrieve more prompts.
    /// </para>
    /// </remarks>
    public Func<RequestContext<ListPromptsRequestParams>, CancellationToken, ValueTask<ListPromptsResult>>? ListPromptsHandler { get; set; }

    /// <summary>
    /// Gets or sets the handler for <see cref="RequestMethods.PromptsGet"/> requests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This handler is invoked when a client requests details for a specific prompt by name and provides arguments
    /// for the prompt if needed. The handler receives the request context containing the prompt name and any arguments, 
    /// and should return a <see cref="GetPromptResult"/> with the prompt messages and other details.
    /// </para>
    /// <para>
    /// This handler will be invoked if the requested prompt name is not found in the prompt collection,
    /// allowing for dynamic prompt generation or retrieval from external sources.
    /// </para>
    /// </remarks>
    public Func<RequestContext<GetPromptRequestParams>, CancellationToken, ValueTask<GetPromptResult>>? GetPromptHandler { get; set; }

    /// <summary>
    /// Gets or sets the handler for <see cref="RequestMethods.ResourcesTemplatesList"/> requests.
    /// </summary>
    /// <remarks>
    /// This handler is called when clients request available resource templates that can be used
    /// to create resources within the Model Context Protocol server.
    /// Resource templates define the structure and URI patterns for resources accessible in the system,
    /// allowing clients to discover available resource types and their access patterns.
    /// </remarks>
    public Func<RequestContext<ListResourceTemplatesRequestParams>, CancellationToken, ValueTask<ListResourceTemplatesResult>>? ListResourceTemplatesHandler { get; set; }

    /// <summary>
    /// Gets or sets the handler for <see cref="RequestMethods.ResourcesList"/> requests.
    /// </summary>
    /// <remarks>
    /// This handler responds to client requests for available resources and returns information about resources accessible through the server.
    /// The implementation should return a <see cref="ListResourcesResult"/> with the matching resources.
    /// </remarks>
    public Func<RequestContext<ListResourcesRequestParams>, CancellationToken, ValueTask<ListResourcesResult>>? ListResourcesHandler { get; set; }

    /// <summary>
    /// Gets or sets the handler for <see cref="RequestMethods.ResourcesRead"/> requests.
    /// </summary>
    /// <remarks>
    /// This handler is responsible for retrieving the content of a specific resource identified by its URI in the Model Context Protocol.
    /// When a client sends a resources/read request, this handler is invoked with the resource URI.
    /// The handler should implement logic to locate and retrieve the requested resource, then return
    /// its contents in a ReadResourceResult object.
    /// </remarks>
    public Func<RequestContext<ReadResourceRequestParams>, CancellationToken, ValueTask<ReadResourceResult>>? ReadResourceHandler { get; set; }

    /// <summary>
    /// Gets or sets the handler for <see cref="RequestMethods.ResourcesSubscribe"/> requests.
    /// </summary>
    /// <remarks>
    /// When a client sends a <see cref="RequestMethods.ResourcesSubscribe"/> request, this handler is invoked with the resource URI
    /// to be subscribed to. The implementation should register the client's interest in receiving updates
    /// for the specified resource.
    /// Subscriptions allow clients to receive real-time notifications when resources change, without
    /// requiring polling.
    /// </remarks>
    public Func<RequestContext<SubscribeRequestParams>, CancellationToken, ValueTask<EmptyResult>>? SubscribeToResourcesHandler { get; set; }

    /// <summary>
    /// Gets or sets the handler for <see cref="RequestMethods.ResourcesUnsubscribe"/> requests.
    /// </summary>
    /// <remarks>
    /// When a client sends a <see cref="RequestMethods.ResourcesUnsubscribe"/> request, this handler is invoked with the resource URI
    /// to be unsubscribed from. The implementation should remove the client's registration for receiving updates
    /// about the specified resource.
    /// </remarks>
    public Func<RequestContext<UnsubscribeRequestParams>, CancellationToken, ValueTask<EmptyResult>>? UnsubscribeFromResourcesHandler { get; set; }

    /// <summary>
    /// Gets or sets the handler for completion requests.
    /// </summary>
    /// <remarks>
    /// This handler provides auto-completion suggestions for prompt arguments or resource references in the Model Context Protocol.
    /// The handler receives a reference type (e.g., "ref/prompt" or "ref/resource") and the current argument value,
    /// and should return appropriate completion suggestions.
    /// </remarks>
    public Func<RequestContext<CompleteRequestParams>, CancellationToken, ValueTask<CompleteResult>>? CompleteHandler { get; set; }

    /// <summary>
    /// Gets or sets the handler for set logging level requests from clients.
    /// </summary>
    public Func<RequestContext<SetLevelRequestParams>, CancellationToken, ValueTask<EmptyResult>>? SetLoggingLevelHandler { get; set; }

    /// <summary>
    /// Applies the configured handlers to the given McpServerOptions.
    /// </summary>
    /// <param name="options">The McpServerOptions to apply handlers to.</param>
    internal void ApplyTo(McpServerOptions options)
    {
        // Apply handlers to McpServerOptions instead of capability types
        if (ListToolsHandler is not null || CallToolHandler is not null)
        {
            options.ListToolsHandler = ListToolsHandler ?? options.ListToolsHandler;
            options.CallToolHandler = CallToolHandler ?? options.CallToolHandler;
            
            options.Capabilities ??= new();
            options.Capabilities.Tools ??= new();
        }

        if (ListPromptsHandler is not null || GetPromptHandler is not null)
        {
            options.ListPromptsHandler = ListPromptsHandler ?? options.ListPromptsHandler;
            options.GetPromptHandler = GetPromptHandler ?? options.GetPromptHandler;
            
            options.Capabilities ??= new();
            options.Capabilities.Prompts ??= new();
        }

        if (ListResourcesHandler is not null ||
            ReadResourceHandler is not null ||
            ListResourceTemplatesHandler is not null)
        {
            options.ListResourceTemplatesHandler = ListResourceTemplatesHandler ?? options.ListResourceTemplatesHandler;
            options.ListResourcesHandler = ListResourcesHandler ?? options.ListResourcesHandler;
            options.ReadResourceHandler = ReadResourceHandler ?? options.ReadResourceHandler;

            if (SubscribeToResourcesHandler is not null || UnsubscribeFromResourcesHandler is not null)
            {
                options.SubscribeToResourcesHandler = SubscribeToResourcesHandler ?? options.SubscribeToResourcesHandler;
                options.UnsubscribeFromResourcesHandler = UnsubscribeFromResourcesHandler ?? options.UnsubscribeFromResourcesHandler;
                
                options.Capabilities ??= new();
                options.Capabilities.Resources ??= new();
                options.Capabilities.Resources.Subscribe = true;
            }

            options.Capabilities ??= new();
            options.Capabilities.Resources ??= new();
        }

        if (SetLoggingLevelHandler is not null)
        {
            options.SetLoggingLevelHandler = SetLoggingLevelHandler;
            
            options.Capabilities ??= new();
            options.Capabilities.Logging ??= new();
        }

        if (CompleteHandler is not null)
        {
            options.CompleteHandler = CompleteHandler;
            
            options.Capabilities ??= new();
            options.Capabilities.Completions ??= new();
        }
    }
}
