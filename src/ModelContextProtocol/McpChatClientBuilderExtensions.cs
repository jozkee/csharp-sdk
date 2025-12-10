using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

namespace ModelContextProtocol;

/// <summary>
/// Provides extension methods for configuring and building instances of McpChatClient.  TODO: improve this.
/// </summary>
public static class McpChatClientBuilderExtensions
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="httpClient"></param>
    /// <param name="loggerFactory"></param>
    /// <returns></returns>
    public static ChatClientBuilder UseMcpClient(
        this ChatClientBuilder builder,
        HttpClient? httpClient = null,
        ILoggerFactory? loggerFactory = null)
    {
        return builder.Use((innerClient, services) =>
        {
            loggerFactory ??= (ILoggerFactory)services.GetService(typeof(ILoggerFactory))!;
            var chatClient = new McpChatClient(innerClient, httpClient, loggerFactory);
            return chatClient;
        });
    }

    /// <summary>
    /// Adds support for enabling MCP function invocation.
    /// </summary>
    private class McpChatClient : DelegatingChatClient
    {
        private readonly ILoggerFactory? _loggerFactory;

        /// <summary>The logger to use for logging information about function invocation.</summary>
        private readonly ILogger _logger;

        /// <summary>The HTTP client to use when connecting to the remote MCP server.</summary>
        private readonly HttpClient _httpClient;

        /// <summary>A dictionary of cached mcp clients, keyed by the MCP server URL.</summary>
        private ConcurrentDictionary<string, McpClient>? _mcpClients = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="McpChatClient"/> class.
        /// </summary>
        /// <param name="innerClient">The underlying <see cref="IChatClient"/>, or the next instance in a chain of clients.</param>
        /// <param name="httpClient">An optional <see cref="HttpClient"/> to use when connecting to MCP servers. If not provided, a new instance will be created.</param>
        /// <param name="loggerFactory">An <see cref="ILoggerFactory"/> to use for logging information about function invocation.</param>
        public McpChatClient(IChatClient innerClient, HttpClient? httpClient = null, ILoggerFactory? loggerFactory = null)
            : base(innerClient)
        {
            _loggerFactory = loggerFactory;
            _logger = (ILogger?)loggerFactory?.CreateLogger<McpChatClient>() ?? NullLogger.Instance;
            _httpClient = httpClient ?? new HttpClient();
        }

        /// <inheritdoc/>
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (options?.Tools is not { Count: > 0 })
            {
                // If there are no tools, just call the inner client.
                return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }

            var downstreamTools = await BuildDownstreamAIToolsAsync(options.Tools, cancellationToken).ConfigureAwait(false);
            options = options.Clone();
            options.Tools = downstreamTools;

            // Make the call to the inner client.
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (options?.Tools is not { Count: > 0 })
            {
                // If there are no tools, just call the inner client.
                await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
                {
                    yield return update;
                }
            }

            var downstreamTools = await BuildDownstreamAIToolsAsync(options!.Tools, cancellationToken).ConfigureAwait(false);
            options = options.Clone();
            options.Tools = downstreamTools;

            // Make the call to the inner client.
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }

        private async Task<List<AITool>?> BuildDownstreamAIToolsAsync(IList<AITool>? inputTools, CancellationToken cancellationToken)
        {
            List<AITool>? downstreamTools = null;
            foreach (var tool in inputTools ?? [])
            {
                if (tool is not HostedMcpServerTool mcpTool)
                {
                    // For other tools, we want to keep them in the list of tools.
                    downstreamTools ??= new List<AITool>();
                    downstreamTools.Add(tool);
                    continue;
                }

                if (!Uri.TryCreate(mcpTool.ServerAddress, UriKind.Absolute, out var parsedAddress) ||
                    (parsedAddress.Scheme != Uri.UriSchemeHttp && parsedAddress.Scheme != Uri.UriSchemeHttps))
                {
                    _logger.LogWarning("MCP server address '{ServerAddress}' is not a valid HTTP/HTTPS URL. Skipping.", mcpTool.ServerAddress);
                    continue;
                }

                // List all MCP functions from the specified MCP server.
                // This will need some caching in a real-world scenario to avoid repeated calls.
                var mcpClient = await CreateMcpClientAsync(parsedAddress, mcpTool.ServerName, mcpTool.AuthorizationToken).ConfigureAwait(false);
                var mcpFunctions = await mcpClient.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

                // Add the listed functions to our list of tools we'll pass to the inner client.
                foreach (var mcpFunction in mcpFunctions)
                {
                    if (mcpTool.AllowedTools is not null && !mcpTool.AllowedTools.Contains(mcpFunction.Name))
                    {
                        _logger.LogInformation("MCP function '{FunctionName}' is not allowed by the tool configuration.", mcpFunction.Name);
                        continue;
                    }

                    downstreamTools ??= new List<AITool>();
                    switch (mcpTool.ApprovalMode)
                    {
                        case HostedMcpServerToolAlwaysRequireApprovalMode alwaysRequireApproval:
                            downstreamTools.Add(new ApprovalRequiredAIFunction(mcpFunction));
                            break;
                        case HostedMcpServerToolNeverRequireApprovalMode neverRequireApproval:
                            downstreamTools.Add(mcpFunction);
                            break;
                        case HostedMcpServerToolRequireSpecificApprovalMode specificApprovalMode when specificApprovalMode.AlwaysRequireApprovalToolNames?.Contains(mcpFunction.Name) is true:
                            downstreamTools.Add(new ApprovalRequiredAIFunction(mcpFunction));
                            break;
                        case HostedMcpServerToolRequireSpecificApprovalMode specificApprovalMode when specificApprovalMode.NeverRequireApprovalToolNames?.Contains(mcpFunction.Name) is true:
                            downstreamTools.Add(mcpFunction);
                            break;
                        default:
                            // Default to always require approval if no specific mode is set.
                            downstreamTools.Add(new ApprovalRequiredAIFunction(mcpFunction));
                            break;
                    }
                }
            }

            return downstreamTools;
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Dispose of the HTTP client if it was created by this client.
                _httpClient?.Dispose();

                if (_mcpClients is not null)
                {
                    // Dispose of all cached MCP clients.
                    foreach (var client in _mcpClients.Values)
                    {
                        _ = client.DisposeAsync();
                    }

                    _mcpClients.Clear();
                }
            }

            base.Dispose(disposing);
        }

        private async Task<McpClient> CreateMcpClientAsync(Uri serverAddress, string serverName, string? authorizationToken)
        {
            if (_mcpClients is null)
            {
                _mcpClients = new ConcurrentDictionary<string, McpClient>(StringComparer.OrdinalIgnoreCase);
            }

            if (_mcpClients.TryGetValue(serverAddress.ToString(), out var cachedClient))
            {
                // Return the cached client if it exists.
                return cachedClient;
            }

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = serverAddress,
                Name = serverName,
                AdditionalHeaders = authorizationToken is not null
                    ? new Dictionary<string, string>() { { "Authorization", $"Bearer {authorizationToken}" } }
                    : null,
            }, _httpClient, _loggerFactory);

            return await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
    }
}
