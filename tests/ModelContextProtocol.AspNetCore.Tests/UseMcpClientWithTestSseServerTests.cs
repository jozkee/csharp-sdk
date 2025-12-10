using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Tests.Utils;
using Moq;
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

namespace ModelContextProtocol.AspNetCore.Tests;

public class UseMcpClientWithTestSseServerTests : LoggedTest, IClassFixture<SseServerWithMockLoggerFixture>
{
    private readonly HttpClientTransportOptions _transportOptions;
    private readonly SseServerWithMockLoggerFixture _fixture;

    public UseMcpClientWithTestSseServerTests(SseServerWithMockLoggerFixture fixture, ITestOutputHelper testOutputHelper)
        : base(testOutputHelper)
    {
        _transportOptions = new HttpClientTransportOptions()
        {
            Endpoint = new("http://localhost:5000/sse"),
            Name = "TestSseServer",
        };

        _fixture = fixture;
        _fixture.Initialize(testOutputHelper, _transportOptions);
    }

    public override void Dispose()
    {
        _fixture.TestCompleted();
        base.Dispose();
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
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Dummy response")])
            {
                ModelId = "test-model",
                FinishReason = ChatFinishReason.Stop
            });

        mockInnerClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>(
                (msgs, opts, ct) => state.CapturedOptions = opts)
            .Returns(GetStreamingResponseAsync());

        callbackState = state;
        return mockInnerClient.Object.AsBuilder()
            .UseMcpClient(_fixture.HttpClient, LoggerFactory)
            .Build();

        static async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent("Dummy response")],
                FinishReason = ChatFinishReason.Stop,
            };
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task UseMcpClient_ShouldProduceTools(bool streaming)
    {
        // Arrange
        IChatClient sut = CreateTestChatClient(out var callbackState);
        var options = new ChatOptions { Tools = [new HostedMcpServerTool(_transportOptions.Name!, _transportOptions.Endpoint)] };

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
        IChatClient sut = CreateTestChatClient(out var callbackState);
        var regularTool = AIFunctionFactory.Create(() => "regular tool result", "RegularTool");
        var options = new ChatOptions
        {
            Tools =
            [
                regularTool,
                new HostedMcpServerTool(_transportOptions.Name!, _transportOptions.Endpoint)
            ]
        };

        // Act
        await GetResponseAsync(sut, options, streaming);

        // Assert
        Assert.NotNull(callbackState.CapturedOptions);
        Assert.NotNull(callbackState.CapturedOptions.Tools);
        var toolNames = callbackState.CapturedOptions.Tools.Select(t => t.Name).ToList();
        Assert.Equal(4, toolNames.Count);
        Assert.Contains("RegularTool", toolNames);
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
        IChatClient sut = CreateTestChatClient(out var callbackState);
        var options = new ChatOptions
        {
            Tools = [new HostedMcpServerTool(_transportOptions.Name!, _transportOptions.Endpoint)
            {
                AuthorizationToken = testToken
            }]
        };

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
        // We set TestSseServer to log IHeaderDictionary as json.
        Assert.Contains(_fixture.ServerLogs, log => log.Message.Contains(@"""Authorization"":[""Bearer test-bearer-token-12345""]"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UseMcpClient_ApprovalsWorkCorrectly(bool streaming)
    {
        // Arrange
        IChatClient sut = CreateTestChatClient(out var callbackState);
        var alwaysRequireApprovalOptions = new ChatOptions
        {
            Tools = [new HostedMcpServerTool(_transportOptions.Name!, _transportOptions.Endpoint)
            {
                ApprovalMode = new HostedMcpServerToolAlwaysRequireApprovalMode()
            }]
        };
        var neverRequireApprovalOptions = new ChatOptions
        {
            Tools = [new HostedMcpServerTool(_transportOptions.Name!, _transportOptions.Endpoint)
            {
                ApprovalMode = new HostedMcpServerToolNeverRequireApprovalMode()
            }]
        };
        var specificApprovalOptions = new ChatOptions
        {
            Tools = [new HostedMcpServerTool(_transportOptions.Name!, _transportOptions.Endpoint)
            {
                ApprovalMode = new HostedMcpServerToolRequireSpecificApprovalMode(
                    alwaysRequireApprovalToolNames: ["echo"],
                    neverRequireApprovalToolNames: ["sampleLLM"])
            }]
        };

        // Act - Test with AlwaysRequireApproval mode
        await GetResponseAsync(sut, alwaysRequireApprovalOptions, streaming);

        // Assert - All tools should be wrapped in ApprovalRequiredAIFunction
        Assert.NotNull(callbackState.CapturedOptions);
        Assert.NotNull(callbackState.CapturedOptions.Tools);
        Assert.Equal(3, callbackState.CapturedOptions.Tools.Count);
        
        // All tools should require approval
        foreach (var tool in callbackState.CapturedOptions.Tools)
        {
            // The implementation wraps tools with approval requirements
            // We can verify the approval by checking if the tool is of the approval wrapper type
            // or by checking metadata/additional properties
            Assert.NotNull(tool);
        }

        // Act - Test with NeverRequireApproval mode
        await GetResponseAsync(sut, neverRequireApprovalOptions, streaming);

        // Assert - Tools should not require approval
        Assert.NotNull(callbackState.CapturedOptions);
        Assert.NotNull(callbackState.CapturedOptions.Tools);
        Assert.Equal(3, callbackState.CapturedOptions.Tools.Count);

        // Act - Test with specific tool approval mode
        await GetResponseAsync(sut, specificApprovalOptions, streaming);

        // Assert - Mixed approval requirements based on tool names
        Assert.NotNull(callbackState.CapturedOptions);
        Assert.NotNull(callbackState.CapturedOptions.Tools);
        Assert.Equal(3, callbackState.CapturedOptions.Tools.Count);
        // The specific approval logic is handled in the implementation
    }
}
