using System.Text.Json.Serialization;

namespace ModelContextProtocol.Protocol;

/// <summary>
/// Represents the capabilities that a server may support.
/// </summary>
/// <remarks>
/// <para>
/// Server capabilities define the features and functionality available when clients connect.
/// These capabilities are advertised to clients during the initialize handshake.
/// </para>
/// <para>
/// See the <see href="https://github.com/modelcontextprotocol/specification/blob/main/schema/">schema</see> for details.
/// </para>
/// </remarks>
public sealed class ServerCapabilities
{
    private ToolsCapability? _toolsCapability;
    private PromptsCapability? _promptsCapability;
    private ResourcesCapability? _resourcesCapability;
    private CompletionsCapability? _completionsCapability;
    private LoggingCapability? _loggingCapability;

    /// <summary>
    /// Gets or sets experimental, non-standard capabilities that the server supports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="Experimental"/> dictionary allows servers to advertise support for features that are not yet 
    /// standardized in the Model Context Protocol specification. This extension mechanism enables 
    /// future protocol enhancements while maintaining backward compatibility.
    /// </para>
    /// <para>
    /// Values in this dictionary are implementation-specific and should be coordinated between client 
    /// and server implementations. Clients should not assume the presence of any experimental capability 
    /// without checking for it first.
    /// </para>
    /// </remarks>
    [JsonPropertyName("experimental")]
    public IDictionary<string, object>? Experimental { get; set; }

    /// <summary>
    /// Gets or sets a server's logging capability, supporting sending log messages to the client.
    /// </summary>
    [JsonPropertyName("logging")]
    public LoggingWireCapability? LoggingWire => _loggingCapability?.Wire;

    /// <summary>
    /// Gets or sets a server's prompts capability for serving predefined prompt templates that clients can discover and use.
    /// </summary>
    [JsonPropertyName("prompts")]
    public PromptsWireCapability? PromptsWire => _promptsCapability?.Wire;

    /// <summary>
    /// Gets or sets a server's resources capability for serving predefined resources that clients can discover and use.
    /// </summary>
    [JsonPropertyName("resources")]
    public ResourcesWireCapability? ResourcesWire => _resourcesCapability?.Wire;

    /// <summary>
    /// Gets or sets a server's tools capability for listing tools that a client is able to invoke.
    /// </summary>
    [JsonPropertyName("tools")]
    public ToolsWireCapability? ToolsWire => _toolsCapability?.Wire;

    /// <summary>
    /// Gets or sets a server's completions capability for supporting argument auto-completion suggestions.
    /// </summary>
    [JsonPropertyName("completions")]
    public CompletionsWireCapability? CompletionsWire => _completionsCapability?.Wire;

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
    [JsonIgnore]
    public IEnumerable<KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>>? NotificationHandlers { get; set; }

    /// <summary>
    /// Gets or sets the full tools capability including server-side implementation details.
    /// </summary>
    /// <remarks>
    /// This property allows setting the complete tools capability configuration including handlers and collections.
    /// Only the wire protocol properties (like ListChanged) will be serialized and sent to clients.
    /// </remarks>
    [JsonIgnore]
    public ToolsCapability? ToolsCapability
    {
        get => _toolsCapability;
        set => _toolsCapability = value;
    }

    /// <summary>
    /// Gets or sets the full prompts capability including server-side implementation details.
    /// </summary>
    /// <remarks>
    /// This property allows setting the complete prompts capability configuration including handlers and collections.
    /// Only the wire protocol properties (like ListChanged) will be serialized and sent to clients.
    /// </remarks>
    [JsonIgnore]
    public PromptsCapability? PromptsCapability
    {
        get => _promptsCapability;
        set => _promptsCapability = value;
    }

    /// <summary>
    /// Gets or sets the full resources capability including server-side implementation details.
    /// </summary>
    /// <remarks>
    /// This property allows setting the complete resources capability configuration including handlers and collections.
    /// Only the wire protocol properties (like Subscribe and ListChanged) will be serialized and sent to clients.
    /// </remarks>
    [JsonIgnore]
    public ResourcesCapability? ResourcesCapability
    {
        get => _resourcesCapability;
        set => _resourcesCapability = value;
    }

    /// <summary>
    /// Gets or sets the full completions capability including server-side implementation details.
    /// </summary>
    /// <remarks>
    /// This property allows setting the complete completions capability configuration including handlers.
    /// Only the wire protocol properties will be serialized and sent to clients.
    /// </remarks>
    [JsonIgnore]
    public CompletionsCapability? CompletionsCapability
    {
        get => _completionsCapability;
        set => _completionsCapability = value;
    }

    /// <summary>
    /// Gets or sets the full logging capability including server-side implementation details.
    /// </summary>
    /// <remarks>
    /// This property allows setting the complete logging capability configuration including handlers.
    /// Only the wire protocol properties will be serialized and sent to clients.
    /// </remarks>
    [JsonIgnore]
    public LoggingCapability? LoggingCapability
    {
        get => _loggingCapability;
        set => _loggingCapability = value;
    }

    // Backward compatibility properties for existing user code
    /// <summary>
    /// Gets or sets the tools capability. This is provided for backward compatibility.
    /// Use ToolsCapability for new code.
    /// </summary>
    [JsonIgnore]
    [Obsolete("Use ToolsCapability instead")]
    public ToolsCapability? Tools
    {
        get => _toolsCapability;
        set => _toolsCapability = value;
    }

    /// <summary>
    /// Gets or sets the prompts capability. This is provided for backward compatibility.
    /// Use PromptsCapability for new code.
    /// </summary>
    [JsonIgnore]
    [Obsolete("Use PromptsCapability instead")]
    public PromptsCapability? Prompts
    {
        get => _promptsCapability;
        set => _promptsCapability = value;
    }

    /// <summary>
    /// Gets or sets the resources capability. This is provided for backward compatibility.
    /// Use ResourcesCapability for new code.
    /// </summary>
    [JsonIgnore]
    [Obsolete("Use ResourcesCapability instead")]
    public ResourcesCapability? Resources
    {
        get => _resourcesCapability;
        set => _resourcesCapability = value;
    }

    /// <summary>
    /// Gets or sets the completions capability. This is provided for backward compatibility.
    /// Use CompletionsCapability for new code.
    /// </summary>
    [JsonIgnore]
    [Obsolete("Use CompletionsCapability instead")]
    public CompletionsCapability? Completions
    {
        get => _completionsCapability;
        set => _completionsCapability = value;
    }

    /// <summary>
    /// Gets or sets the logging capability. This is provided for backward compatibility.
    /// Use LoggingCapability for new code.
    /// </summary>
    [JsonIgnore]
    [Obsolete("Use LoggingCapability instead")]
    public LoggingCapability? Logging
    {
        get => _loggingCapability;
        set => _loggingCapability = value;
    }
}
