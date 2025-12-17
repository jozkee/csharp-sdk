using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore.Tests.Utils;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Moq;
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

namespace ModelContextProtocol.AspNetCore.Tests;

public class UseMcpClientTests : KestrelInMemoryTest
{    public UseMcpClientTests(ITestOutputHelper testOutputHelper)
        : base(testOutputHelper)
    {
    }

    private async Task<WebApplication> StartServerAsync(Action<WebApplication>? configureApp = null)
    {
        IMcpServerBuilder builder = Builder.Services.AddMcpServer(options =>
        {
            options.Capabilities = new ServerCapabilities
            {
                Tools = new(),
                Resources = new(),
                Prompts = new(),
            };
            options.ServerInstructions = "This is a test server with only stub functionality";

            options.Handlers = new()
            {
                ListToolsHandler = async (request, cancellationToken) =>
                {
                    return new ListToolsResult
                    {
                        Tools =
                        [
                            new Tool
                            {
                                Name = "echo",
                                Description = "Echoes the input back to the client.",
                                InputSchema = JsonElement.Parse("""
                                    {
                                        "type": "object",
                                        "properties": {
                                            "message": {
                                                "type": "string",
                                                "description": "The input to echo back."
                                            }
                                        },
                                        "required": ["message"]
                                    }
                                    """),
                            },
                            new Tool
                            {
                                Name = "echoSessionId",
                                Description = "Echoes the session id back to the client.",
                                InputSchema = JsonElement.Parse("""
                                    {
                                        "type": "object"
                                    }
                                    """),
                            },
                            new Tool
                            {
                                Name = "sampleLLM",
                                Description = "Samples from an LLM using MCP's sampling feature.",
                                InputSchema = JsonElement.Parse("""
                                    {
                                        "type": "object",
                                        "properties": {
                                            "prompt": {
                                                "type": "string",
                                                "description": "The prompt to send to the LLM"
                                            },
                                            "maxTokens": {
                                                "type": "number",
                                                "description": "Maximum number of tokens to generate"
                                            }
                                        },
                                        "required": ["prompt", "maxTokens"]
                                    }
                                    """),
                            }
                        ]
                    };
                },
                CallToolHandler = async (request, cancellationToken) =>
                {
                    if (request.Params is null)
                    {
                        throw new McpProtocolException("Missing required parameter 'name'", McpErrorCode.InvalidParams);
                    }
                    if (request.Params.Name == "echo")
                    {
                        if (request.Params.Arguments is null || !request.Params.Arguments.TryGetValue("message", out var message))
                        {
                            throw new McpProtocolException("Missing required argument 'message'", McpErrorCode.InvalidParams);
                        }
                        return new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = $"Echo: {message}" }]
                        };
                    }
                    else if (request.Params.Name == "echoSessionId")
                    {
                        return new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = request.Server.SessionId ?? string.Empty }]
                        };
                    }
                    else if (request.Params.Name == "sampleLLM")
                    {
                        if (request.Params.Arguments is null ||
                            !request.Params.Arguments.TryGetValue("prompt", out var prompt) ||
                            !request.Params.Arguments.TryGetValue("maxTokens", out var maxTokens))
                        {
                            throw new McpProtocolException("Missing required arguments 'prompt' and 'maxTokens'", McpErrorCode.InvalidParams);
                        }
                        // Simple mock response for sampleLLM
                        return new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = "LLM sampling result: Test response" }]
                        };
                    }
                    else
                    {
                        throw new McpProtocolException($"Unknown tool: '{request.Params.Name}'", McpErrorCode.InvalidParams);
                    }
                }
            };
        })
        .WithHttpTransport();

        var app = Builder.Build();
        configureApp?.Invoke(app);
        app.MapMcp();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private sealed class CallbackState
    {
        public ChatOptions? CapturedOptions { get; set; }
    }

    private IChatClient CreateTestChatClient(out CallbackState callbackState)
    {
        var state = new CallbackState();

        var mockInnerClient = new Mock<IChatClient>();
        mockInnerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>(
                (msgs, opts, ct) => state.CapturedOptions = opts)
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Dummy response")]));

        mockInnerClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>(
                (msgs, opts, ct) => state.CapturedOptions = opts)
            .Returns(GetStreamingResponseAsync());

        callbackState = state;
        return mockInnerClient.Object.AsBuilder()
            .UseMcpClient(HttpClient, LoggerFactory)
            .Build();

        static async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Dummy response");
        }
    }

    private async Task GetResponseAsync(IChatClient client, ChatOptions options, bool streaming)
    {
        if (streaming)
        {
            await foreach (var _ in client.GetStreamingResponseAsync("Test message", options, TestContext.Current.CancellationToken))
            { }
        }
        else
        {
            _ = await client.GetResponseAsync("Test message", options, TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UseMcpClient_ShouldProduceTools(bool streaming, bool useUrl)
    {
        // Arrange
        await using var _ = await StartServerAsync();
        using IChatClient sut = CreateTestChatClient(out var callbackState);
        var mcpTool = useUrl ? 
            new HostedMcpServerTool("serverName", HttpClient.BaseAddress!) : 
            new HostedMcpServerTool("serverName", HttpClient.BaseAddress!.ToString());
        var options = new ChatOptions { Tools = [mcpTool] };

        // Act
        await GetResponseAsync(sut, options, streaming);

        // Assert
        Assert.NotNull(callbackState.CapturedOptions);
        Assert.NotNull(callbackState.CapturedOptions.Tools);
        var toolNames = callbackState.CapturedOptions.Tools.Select(t => t.Name).ToList();
        Assert.Equal(3, toolNames.Count);
        Assert.Contains("echo", toolNames);
        Assert.Contains("echoSessionId", toolNames);
        Assert.Contains("sampleLLM", toolNames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UseMcpClient_DoesNotConflictWithRegularTools(bool streaming)
    {
        // Arrange
        await using var _ = await StartServerAsync();
        using IChatClient sut = CreateTestChatClient(out var callbackState);
        var regularTool = AIFunctionFactory.Create(() => "regular tool result", "regularTool");
        var mcpTool = new HostedMcpServerTool("serverName", HttpClient.BaseAddress!.ToString());
        var options = new ChatOptions
        {
            Tools = [regularTool, mcpTool]
        };

        // Act
        await GetResponseAsync(sut, options, streaming);

        // Assert
        Assert.NotNull(callbackState.CapturedOptions);
        Assert.NotNull(callbackState.CapturedOptions.Tools);
        var toolNames = callbackState.CapturedOptions.Tools.Select(t => t.Name).ToList();
        Assert.Equal(4, toolNames.Count);
        Assert.Contains("regularTool", toolNames);
        Assert.Contains("echo", toolNames);
        Assert.Contains("echoSessionId", toolNames);
        Assert.Contains("sampleLLM", toolNames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UseMcpClient_AuthorizationTokenHeaderFlowsCorrectly(bool streaming)
    {
        // Arrange
        const string testToken = "test-bearer-token-12345";
        bool authReceivedForInitialize = false;
        bool authReceivedForNotificationsInitialized = false;
        bool authReceivedForToolsList = false;
        
        await using var _ = await StartServerAsync(
            configureApp: app =>
            {
                app.Use(async (context, next) =>
                {
                    if (context.Request.Method == "POST" &&
                        context.Request.Headers.TryGetValue("Authorization", out var authHeader))
                    {
                        Assert.Equal($"Bearer {testToken}", authHeader.ToString());

                        context.Request.EnableBuffering();
                        JsonRpcRequest? rpcRequest = await JsonSerializer.DeserializeAsync<JsonRpcRequest>(
                            context.Request.Body, 
                            McpJsonUtilities.DefaultOptions, 
                            context.RequestAborted);
                        context.Request.Body.Position = 0;
                        Assert.NotNull(rpcRequest);
                        
                        switch (rpcRequest.Method)
                        {
                            case "initialize":
                                authReceivedForInitialize = true;
                                break;
                            case "notifications/initialized":
                                authReceivedForNotificationsInitialized = true;
                                break;
                            case "tools/list":
                                authReceivedForToolsList = true;
                                break;
                        }
                    }
                    await next();
                });
            });
        
        using IChatClient sut = CreateTestChatClient(out var callbackState);
        var mcpTool = new HostedMcpServerTool("serverName", HttpClient.BaseAddress!)
        {
            AuthorizationToken = testToken
        };
        var options = new ChatOptions
        {
            Tools = [mcpTool]
        };

        // Act
        await GetResponseAsync(sut, options, streaming);

        // Assert
        Assert.True(authReceivedForInitialize, "Authorization header was not captured in initial request");
        Assert.True(authReceivedForNotificationsInitialized, "Authorization header was not captured in notifications/initialized request");
        Assert.True(authReceivedForToolsList, "Authorization header was not captured in tools/list request");
        Assert.NotNull(callbackState.CapturedOptions);
        Assert.NotNull(callbackState.CapturedOptions.Tools);
        var toolNames = callbackState.CapturedOptions.Tools.Select(t => t.Name).ToList();
        Assert.Equal(3, toolNames.Count);
        Assert.Contains("echo", toolNames);
        Assert.Contains("echoSessionId", toolNames);
        Assert.Contains("sampleLLM", toolNames);
    }

    public static IEnumerable<object?[]> UseMcpClient_ApprovalsWorkCorrectly_TestData()
    {
        string[] allToolNames = ["echo", "echoSessionId", "sampleLLM"];
        foreach (var streaming in new[] { false, true })
        {
            yield return new object?[] { streaming, new HostedMcpServerToolNeverRequireApprovalMode(), (string[])[], allToolNames };
            yield return new object?[] { streaming, new HostedMcpServerToolAlwaysRequireApprovalMode(), allToolNames, (string[])[] };
            yield return new object?[] { streaming, null, allToolNames, (string[])[] };
            // Specific mode with empty lists - all tools should default to requiring approval.
            yield return new object?[] { streaming, new HostedMcpServerToolRequireSpecificApprovalMode([], []), allToolNames, (string[])[] };
            // Specific mode with one tool always requiring approval - the other two should default to requiring approval.
            yield return new object?[] { streaming, new HostedMcpServerToolRequireSpecificApprovalMode(["echo"], []), allToolNames, (string[])[] };
            // Specific mode with one tool never requiring approval - the other two should default to requiring approval.
            yield return new object?[] { streaming, new HostedMcpServerToolRequireSpecificApprovalMode([], ["echo"]), (string[])["echoSessionId", "sampleLLM"], (string[])["echo"] };
        }
    }

    [Theory]
    [MemberData(nameof(UseMcpClient_ApprovalsWorkCorrectly_TestData))]
    public async Task UseMcpClient_ApprovalsWorkCorrectly(
        bool streaming, 
        HostedMcpServerToolApprovalMode? approvalMode,
        string[] expectedApprovalRequiredAIFunctions,
        string[] expectedNormalAIFunctions)
    {
        // Arrange
        await using var _ = await StartServerAsync();
        using IChatClient sut = CreateTestChatClient(out var callbackState);
        var mcpTool = new HostedMcpServerTool("serverName", HttpClient.BaseAddress!)
        {
            ApprovalMode = approvalMode
        };
        var options = new ChatOptions { Tools = [mcpTool] };

        // Act
        await GetResponseAsync(sut, options, streaming);

        // Assert
        Assert.NotNull(callbackState.CapturedOptions);
        Assert.NotNull(callbackState.CapturedOptions.Tools);
        Assert.Equal(3, callbackState.CapturedOptions.Tools.Count);

        var toolsRequiringApproval = callbackState.CapturedOptions.Tools
            .Where(t => t is ApprovalRequiredAIFunction).Select(t => t.Name);

        var toolsNotRequiringApproval = callbackState.CapturedOptions.Tools
            .Where(t => t is not ApprovalRequiredAIFunction).Select(t => t.Name);

        Assert.Equivalent(expectedApprovalRequiredAIFunctions, toolsRequiringApproval);
        Assert.Equivalent(expectedNormalAIFunctions, toolsNotRequiringApproval);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UseMcpClient_ThrowsInvalidOperationException_WhenServerAddressIsInvalid(bool streaming)
    {
        // Arrange
        await using var _ = await StartServerAsync();
        using IChatClient sut = CreateTestChatClient(out var callbackState);
        var mcpTool = new HostedMcpServerTool("serverNameConnector", "test-connector-123");
        var options = new ChatOptions { Tools = [mcpTool] };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => GetResponseAsync(sut, options, streaming));
        Assert.Contains("test-connector-123", exception.Message);
    }

    [Theory]
    [InlineData(false, null, (string[])["echo", "echoSessionId", "sampleLLM"])]
    [InlineData(true, null, (string[])["echo", "echoSessionId", "sampleLLM"])]
    [InlineData(false, (string[])["echo"], (string[])["echo"])]
    [InlineData(true, (string[])["echo"], (string[])["echo"])]
    [InlineData(false, (string[])[], (string[])[])]
    [InlineData(true, (string[])[], (string[])[])]
    public async Task UseMcpClient_AllowedTools_FiltersCorrectly(bool streaming, string[]? allowedTools, string[] expectedTools)
    {
        // Arrange
        await using var _ = await StartServerAsync();
        using IChatClient sut = CreateTestChatClient(out var callbackState);
        var mcpTool = new HostedMcpServerTool("serverName", HttpClient.BaseAddress!)
        {
            AllowedTools = allowedTools
        };
        var options = new ChatOptions { Tools = [mcpTool] };

        // Act
        await GetResponseAsync(sut, options, streaming);

        // Assert
        Assert.NotNull(callbackState.CapturedOptions);
        Assert.NotNull(callbackState.CapturedOptions.Tools);
        var toolNames = callbackState.CapturedOptions.Tools.Select(t => t.Name).ToList();
        Assert.Equal(expectedTools.Length, toolNames.Count);
        Assert.Equivalent(expectedTools, toolNames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UseMcpClient_CachesClientForSameServerAddress(bool streaming)
    {
        // Arrange
        await using var _ = await StartServerAsync();
        using IChatClient sut = CreateTestChatClient(out var callbackState);
        var mcpTool = new HostedMcpServerTool("serverName", HttpClient.BaseAddress!);
        var options = new ChatOptions { Tools = [mcpTool] };

        // Act - First call
        await GetResponseAsync(sut, options, streaming);

        // Assert - First call should succeed and produce tools
        Assert.NotNull(callbackState.CapturedOptions);
        Assert.NotNull(callbackState.CapturedOptions.Tools);
        var firstCallToolCount = callbackState.CapturedOptions.Tools.Count;
        Assert.Equal(3, firstCallToolCount);

        // Act - Second call with same server address (should use cached client)
        await GetResponseAsync(sut, options, streaming);

        // Assert - Second call should also succeed with same tools
        Assert.NotNull(callbackState.CapturedOptions);
        Assert.NotNull(callbackState.CapturedOptions.Tools);
        var secondCallToolCount = callbackState.CapturedOptions.Tools.Count;
        Assert.Equal(3, secondCallToolCount);
        Assert.Equal(firstCallToolCount, secondCallToolCount);

        // Verify the tools are the same
        var toolNames = callbackState.CapturedOptions.Tools.Select(t => t.Name).ToList();
        Assert.Contains("echo", toolNames);
        Assert.Contains("echoSessionId", toolNames);
        Assert.Contains("sampleLLM", toolNames);
    }
}
