using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Text.Json.Nodes;

namespace ModelContextProtocol.Server;

/// <summary>
/// Provides configuration options for the MCP server.
/// </summary>
public sealed class McpServerOptions
{
    /// <summary>
    /// Gets or sets information about this server implementation, including its name and version.
    /// </summary>
    /// <remarks>
    /// This information is sent to the client during initialization to identify the server.
    /// It's displayed in client logs and can be used for debugging and compatibility checks.
    /// </remarks>
    public Implementation? ServerInfo { get; set; }

    /// <summary>
    /// Gets or sets server capabilities to advertise to the client.
    /// </summary>
    /// <remarks>
    /// These determine which features will be available when a client connects.
    /// Capabilities can include "tools", "prompts", "resources", "logging", and other
    /// protocol-specific functionality.
    /// </remarks>
    public ServerCapabilities? Capabilities { get; set; }

    /// <summary>
    /// Gets or sets the protocol version supported by this server, using a date-based versioning scheme.
    /// </summary>
    /// <remarks>
    /// The protocol version defines which features and message formats this server supports.
    /// This uses a date-based versioning scheme in the format "YYYY-MM-DD".
    /// If <see langword="null"/>, the server will advertize to the client the version requested
    /// by the client if that version is known to be supported, and otherwise will advertize the latest
    /// version supported by the server.
    /// </remarks>
    public string? ProtocolVersion { get; set; }

    /// <summary>
    /// Gets or sets a timeout used for the client-server initialization handshake sequence.
    /// </summary>
    /// <remarks>
    /// This timeout determines how long the server will wait for client responses during
    /// the initialization protocol handshake. If the client doesn't respond within this timeframe,
    /// the initialization process will be aborted.
    /// </remarks>
    public TimeSpan InitializationTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets optional server instructions to send to clients.
    /// </summary>
    /// <remarks>
    /// These instructions are sent to clients during the initialization handshake and provide
    /// guidance on how to effectively use the server's capabilities. They can include details
    /// about available tools, expected input formats, limitations, or other helpful information.
    /// Client applications typically use these instructions as system messages for LLM interactions
    /// to provide context about available functionality.
    /// </remarks>
    public string? ServerInstructions { get; set; }

    /// <summary>
    /// Gets or sets whether to create a new service provider scope for each handled request.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="true"/>. When <see langword="true"/>, each invocation of a request
    /// handler will be invoked within a new service scope.
    /// </remarks>
    public bool ScopeRequests { get; set; } = true;

    /// <summary>
    /// Gets or sets preexisting knowledge about the client including its name and version to help support
    /// stateless Streamable HTTP servers that encode this knowledge in the mcp-session-id header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When not specified, this information is sourced from the client's initialize request.
    /// </para>
    /// </remarks>
    public Implementation? KnownClientInfo { get; set; }

    /// <summary>Gets or sets notification handlers to register with the server.</summary>
    /// <remarks>
    /// <para>
    /// When constructed, the server will enumerate these handlers once, which may contain multiple handlers per notification method key.
    /// The server will not re-enumerate the sequence after initialization.
    /// </para>
    /// <para>
    /// Notification handlers allow the server to respond to client-sent notifications for specific methods.
    /// Each key in the collection is a notification method name, and each value is a callback that will be invoked
    /// when a notification with that method is received.
    /// </para>
    /// <para>
    /// Handlers provided via <see cref="NotificationHandlers"/> will be registered with the server for the lifetime of the server.
    /// For transient handlers, <see cref="IMcpEndpoint.RegisterNotificationHandler"/> may be used to register a handler that can
    /// then be unregistered by disposing of the <see cref="IAsyncDisposable"/> returned from the method.
    /// </para>
    /// </remarks>
    public IEnumerable<KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>>? NotificationHandlers { get; set; }

    // Tools capability server-side properties
    /// <summary>
    /// Gets or sets the handler for <see cref="RequestMethods.ToolsList"/> requests.
    /// </summary>
    /// <remarks>
    /// The handler should return a list of available tools when requested by a client.
    /// It supports pagination through the cursor mechanism, where the client can make
    /// repeated calls with the cursor returned by the previous call to retrieve more tools.
    /// When used in conjunction with <see cref="ToolCollection"/>, both the tools from this handler
    /// and the tools from the collection will be combined to form the complete list of available tools.
    /// </remarks>
    public Func<RequestContext<ListToolsRequestParams>, CancellationToken, ValueTask<ListToolsResult>>? ListToolsHandler { get; set; }

    /// <summary>
    /// Gets or sets the handler for <see cref="RequestMethods.ToolsCall"/> requests.
    /// </summary>
    /// <remarks>
    /// This handler is invoked when a client makes a call to a tool that isn't found in the <see cref="ToolCollection"/>.
    /// The handler should implement logic to execute the requested tool and return appropriate results.
    /// It receives a <see cref="RequestContext{CallToolRequestParams}"/> containing information about the tool
    /// being called and its arguments, and should return a <see cref="CallToolResult"/> with the execution results.
    /// </remarks>
    public Func<RequestContext<CallToolRequestParams>, CancellationToken, ValueTask<CallToolResult>>? CallToolHandler { get; set; }

    /// <summary>
    /// Gets or sets a collection of tools served by the server.
    /// </summary>
    /// <remarks>
    /// Tools specified via <see cref="ToolCollection"/> augment the <see cref="ListToolsHandler"/> and
    /// <see cref="CallToolHandler"/>, if provided. ListTools requests will output information about every tool
    /// in <see cref="ToolCollection"/> and then also any tools output by <see cref="ListToolsHandler"/>, if it's
    /// non-<see langword="null"/>. CallTool requests will first check <see cref="ToolCollection"/> for the tool
    /// being requested, and if the tool is not found in the <see cref="ToolCollection"/>, any specified <see cref="CallToolHandler"/>
    /// will be invoked as a fallback.
    /// </remarks>
    public McpServerPrimitiveCollection<McpServerTool>? ToolCollection { get; set; }

    // Prompts capability server-side properties
    /// <summary>
    /// Gets or sets the handler for <see cref="RequestMethods.PromptsList"/> requests.
    /// </summary>
    /// <remarks>
    /// This handler is invoked when a client requests a list of available prompts from the server
    /// via a <see cref="RequestMethods.PromptsList"/> request. Results from this handler are returned
    /// along with any prompts defined in <see cref="PromptCollection"/>.
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
    /// This handler will be invoked if the requested prompt name is not found in the <see cref="PromptCollection"/>,
    /// allowing for dynamic prompt generation or retrieval from external sources.
    /// </para>
    /// </remarks>
    public Func<RequestContext<GetPromptRequestParams>, CancellationToken, ValueTask<GetPromptResult>>? GetPromptHandler { get; set; }

    /// <summary>
    /// Gets or sets a collection of prompts that will be served by the server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="PromptCollection"/> contains the predefined prompts that clients can request from the server.
    /// This collection works in conjunction with <see cref="ListPromptsHandler"/> and <see cref="GetPromptHandler"/>
    /// when those are provided:
    /// </para>
    /// <para>
    /// - For <see cref="RequestMethods.PromptsList"/> requests: The server returns all prompts from this collection
    ///   plus any additional prompts provided by the <see cref="ListPromptsHandler"/> if it's set.
    /// </para>
    /// <para>
    /// - For <see cref="RequestMethods.PromptsGet"/> requests: The server first checks this collection for the requested prompt.
    ///   If not found, it will invoke the <see cref="GetPromptHandler"/> as a fallback if one is set.
    /// </para>
    /// </remarks>
    public McpServerPrimitiveCollection<McpServerPrompt>? PromptCollection { get; set; }

    // Resources capability server-side properties
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
    /// Gets or sets a collection of resources served by the server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resources specified via <see cref="ResourceCollection"/> augment the <see cref="ListResourcesHandler"/>, <see cref="ListResourceTemplatesHandler"/>
    /// and <see cref="ReadResourceHandler"/> handlers, if provided. Resources with template expressions in their URI templates are considered resource templates
    /// and are listed via ListResourceTemplate, whereas resources without template parameters are considered static resources and are listed with ListResources.
    /// </para>
    /// <para>
    /// ReadResource requests will first check the <see cref="ResourceCollection"/> for the exact resource being requested. If no match is found, they'll proceed to
    /// try to match the resource against each resource template in <see cref="ResourceCollection"/>. If no match is still found, the request will fall back to
    /// any handler registered for <see cref="ReadResourceHandler"/>.
    /// </para>
    /// </remarks>
    public McpServerResourceCollection? ResourceCollection { get; set; }

    // Completions capability server-side properties
    /// <summary>
    /// Gets or sets the handler for completion requests.
    /// </summary>
    /// <remarks>
    /// This handler provides auto-completion suggestions for prompt arguments or resource references in the Model Context Protocol.
    /// The handler receives a reference type (e.g., "ref/prompt" or "ref/resource") and the current argument value,
    /// and should return appropriate completion suggestions.
    /// </remarks>
    public Func<RequestContext<CompleteRequestParams>, CancellationToken, ValueTask<CompleteResult>>? CompleteHandler { get; set; }

    // Logging capability server-side properties
    /// <summary>
    /// Gets or sets the handler for set logging level requests from clients.
    /// </summary>
    public Func<RequestContext<SetLevelRequestParams>, CancellationToken, ValueTask<EmptyResult>>? SetLoggingLevelHandler { get; set; }
}
