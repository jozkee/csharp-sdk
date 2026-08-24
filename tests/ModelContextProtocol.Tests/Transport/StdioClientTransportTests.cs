using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Tests.Utils;
using System.IO.Pipelines;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ModelContextProtocol.Tests.Transport;

[Collection(nameof(DisableParallelization))]
public class StdioClientTransportTests(ITestOutputHelper testOutputHelper) : LoggedTest(testOutputHelper)
{
    public static bool IsStdErrCallbackSupported => !PlatformDetection.IsMonoRuntime;

    public static bool IsWindows => PlatformDetection.IsWindows;

    [Fact]
    public async Task ConnectAsync_DoesNotLogEnvironmentVariablesAtTrace()
    {
        string secretName = $"MCP_TEST_SECRET_{Guid.NewGuid():N}";
        string secretValue = $"secret-{Guid.NewGuid():N}";

        using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.AddProvider(MockLoggerProvider);
            builder.SetMinimumLevel(LogLevel.Trace);
        });

        StdioClientTransport transport = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ?
            new(new()
            {
                Command = "cmd.exe",
                Arguments = ["/c", "exit /b 0"],
                EnvironmentVariables = new Dictionary<string, string?> { [secretName] = secretValue },
            }, loggerFactory) :
            new(new()
            {
                Command = "sh",
                Arguments = ["-c", "exit 0"],
                EnvironmentVariables = new Dictionary<string, string?> { [secretName] = secretValue },
            }, loggerFactory);

        await using var _ = await transport.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Contains(MockLoggerProvider.LogMessages, log =>
            log.LogLevel == LogLevel.Trace &&
            log.Message.Contains("starting server process", StringComparison.Ordinal));
        Assert.DoesNotContain(MockLoggerProvider.LogMessages, log =>
            log.Message.Contains(secretName, StringComparison.Ordinal) ||
            log.Message.Contains(secretValue, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_ValidProcessInvalidServer_Throws()
    {
        string id = Guid.NewGuid().ToString("N");

        StdioClientTransport transport = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ?
            new(new() { Command = "cmd", Arguments = ["/c", $"echo {id} >&2 & exit /b 1"] }, LoggerFactory) :
            new(new() { Command = "sh", Arguments = ["-c", $"echo {id} >&2; exit 1"] }, LoggerFactory);

        await Assert.ThrowsAnyAsync<IOException>(() => McpClient.CreateAsync(transport, loggerFactory: LoggerFactory, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(Skip= "Platform not supported by this test.", SkipUnless = nameof(IsStdErrCallbackSupported))]
    public async Task CreateAsync_ValidProcessInvalidServer_StdErrCallbackInvoked()
    {
        string id = Guid.NewGuid().ToString("N");

        int count = 0;
        StringBuilder sb = new();
        Action<string> stdErrCallback = line =>
        {
            Assert.NotNull(line);
            lock (sb)
            {
                sb.AppendLine(line);
                count++;
            }
        };

        StdioClientTransport transport = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ?
            new(new() { Command = "cmd", Arguments = ["/c", $"echo {id} >&2 & exit /b 1"], StandardErrorLines = stdErrCallback }, LoggerFactory) :
            new(new() { Command = "sh", Arguments = ["-c", $"echo {id} >&2; exit 1"], StandardErrorLines = stdErrCallback }, LoggerFactory);

        await Assert.ThrowsAnyAsync<IOException>(() => McpClient.CreateAsync(transport, loggerFactory: LoggerFactory, cancellationToken: TestContext.Current.CancellationToken));

        // The stderr reading thread may not have delivered the callback yet
        // after the IOException is thrown. Poll briefly for it to arrive.
        var deadline = DateTime.UtcNow + TestConstants.DefaultTimeout;
        while (Volatile.Read(ref count) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.InRange(count, 1, int.MaxValue);
        Assert.Contains(id, sb.ToString());
    }

    [Fact(Skip = "Platform not supported by this test.", SkipUnless = nameof(IsStdErrCallbackSupported))]
    public async Task CreateAsync_StdErrCallbackThrows_DoesNotCrashProcess()
    {
        StdioClientTransport transport = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ?
            new(new() { Command = "cmd", Arguments = ["/c", "echo fail >&2 & exit /b 1"], StandardErrorLines = _ => throw new InvalidOperationException("boom") }, LoggerFactory) :
            new(new() { Command = "sh", Arguments = ["-c", "echo fail >&2; exit 1"], StandardErrorLines = _ => throw new InvalidOperationException("boom") }, LoggerFactory);

        // Should throw IOException for the failed server, not crash the host process.
        await Assert.ThrowsAnyAsync<IOException>(() => McpClient.CreateAsync(transport, loggerFactory: LoggerFactory, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("argument with spaces")]
    [InlineData("&")]
    [InlineData("|")]
    [InlineData(">")]
    [InlineData("<")]
    [InlineData("^")]
    [InlineData(" & ")]
    [InlineData(" | ")]
    [InlineData(" > ")]
    [InlineData(" < ")]
    [InlineData(" ^ ")]
    [InlineData("& ")]
    [InlineData("| ")]
    [InlineData("> ")]
    [InlineData("< ")]
    [InlineData("^ ")]
    [InlineData(" &")]
    [InlineData(" |")]
    [InlineData(" >")]
    [InlineData(" <")]
    [InlineData(" ^")]
    [InlineData("^&<>|")]
    [InlineData("^&<>| ")]
    [InlineData(" ^&<>|")]
    [InlineData("\t^&<>")]
    [InlineData("^&\t<>")]
    [InlineData("ls /tmp | grep foo.txt > /dev/null")]
    [InlineData("let rec Y f x = f (Y f) x")]
    [InlineData("value with \"quotes\" and spaces")]
    [InlineData("C:\\Program Files\\Test App\\app.dll")]
    [InlineData("C:\\EndsWithBackslash\\")]
    [InlineData("--already-looks-like-flag")]
    [InlineData("-starts-with-dash")]
    [InlineData("name=value=another")]
    [InlineData("$(echo injected)")]
    [InlineData("value-with-\"quotes\"-and-\\backslashes\\")]
    [InlineData("http://localhost:1234/callback?foo=1&bar=2")]
    public async Task EscapesCliArgumentsCorrectly(string? cliArgumentValue)
    {
        if (PlatformDetection.IsMonoRuntime && cliArgumentValue?.EndsWith("\\") is true)
        {
            Assert.Skip("mono runtime does not handle arguments ending with backslash correctly.");
        }
        
        string cliArgument = $"--cli-arg={cliArgumentValue}";

        StdioClientTransportOptions options = new()
        {
            Name = "TestServer",
            Command = (PlatformDetection.IsMonoRuntime, PlatformDetection.IsWindows) switch
            {
                (true, _) => "mono",
                (_, true) => "TestServer.exe",
                _ => "dotnet",
            },
            Arguments = (PlatformDetection.IsMonoRuntime, PlatformDetection.IsWindows) switch
            {
                (true, _) => ["TestServer.exe", cliArgument],
                (_, true) => [cliArgument],
                _ => ["TestServer.dll", cliArgument],
            },
        };

        var transport = new StdioClientTransport(options, LoggerFactory);

        // Act: Create client (handshake) and list tools to ensure full round trip works with the argument present.
        await using var client = await McpClient.CreateAsync(transport, loggerFactory: LoggerFactory, cancellationToken: TestContext.Current.CancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(tools);
        Assert.NotEmpty(tools);

        var result = await client.CallToolAsync("echoCliArg", cancellationToken: TestContext.Current.CancellationToken);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal(cliArgumentValue ?? "", content.Text);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public async Task CommandPathWithSpaces_RoundTripsArguments()
    {
        // Regression test for #1601: a command whose absolute path contains a space must launch
        // and round-trip its arguments unchanged. The transport resolves the command via
        // PATH/PATHEXT and launches it directly (no cmd.exe wrapping), so a directly-launched
        // executable preserves every argument exactly, including cmd metacharacters.
        string[] argumentValues =
        [
            "&", "|", ">", "<", "^", "^&<>|",
            "argument with spaces",
            "value with \"quotes\" and spaces",
            "C:\\EndsWithBackslash\\",
            "$(echo injected)",
            "http://localhost:1234/callback?foo=1&bar=2",
        ];

        string sourceExe = Path.Combine(AppContext.BaseDirectory, "TestServer.exe");
        Assert.True(File.Exists(sourceExe), $"Expected TestServer.exe next to the test assembly at '{sourceExe}'.");

        // Copy the entire output (TestServer plus its runtime dependencies) into a directory whose
        // path contains a space. Copying once keeps the test fast while exercising every argument.
        string spacedDir = Path.Combine(Path.GetTempPath(), $"mcp test dir {Guid.NewGuid():N}");
        Directory.CreateDirectory(spacedDir);
        try
        {
            CopyDirectoryRecursively(AppContext.BaseDirectory, spacedDir);

            string command = Path.Combine(spacedDir, "TestServer.exe");
            Assert.Contains(' ', command);

            foreach (string argumentValue in argumentValues)
            {
                StdioClientTransportOptions options = new()
                {
                    Name = "TestServer",
                    Command = command,
                    Arguments = [$"--cli-arg={argumentValue}"],
                };

                var transport = new StdioClientTransport(options, LoggerFactory);

                await using var client = await McpClient.CreateAsync(transport, loggerFactory: LoggerFactory, cancellationToken: TestContext.Current.CancellationToken);

                var result = await client.CallToolAsync("echoCliArg", cancellationToken: TestContext.Current.CancellationToken);
                var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
                Assert.Equal(argumentValue, content.Text);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(spacedDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; the process handle may still be releasing.
            }
        }

        static void CopyDirectoryRecursively(string sourceDirectory, string destinationDirectory)
        {
            string sourceRoot = sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            foreach (string sourceSubdirectory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relativeDirectory = sourceSubdirectory.Substring(sourceRoot.Length);
                Directory.CreateDirectory(Path.Combine(destinationDirectory, relativeDirectory));
            }

            foreach (string sourceFile in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relativeFile = sourceFile.Substring(sourceRoot.Length);
                string destinationFile = Path.Combine(destinationDirectory, relativeFile);
                string? destinationParent = Path.GetDirectoryName(destinationFile);
                if (!string.IsNullOrEmpty(destinationParent))
                {
                    Directory.CreateDirectory(destinationParent);
                }

                File.Copy(sourceFile, destinationFile, overwrite: true);
            }
        }
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void ResolveWindowsCommand_CommandWithExtension_ResolvesToDoubleExtensionShim()
    {
        // A command that already carries an extension (e.g. "foo.exe") must still be resolved to a
        // "foo.exe.cmd" shim when no bare "foo.exe" exists, because a Windows shell (PowerShell) and
        // npm cross-spawn both append every PATHEXT extension to the command name unconditionally.
        string workingDirectory = Path.Combine(Path.GetTempPath(), $"mcp-resolve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            string baseName = $"probe-{Guid.NewGuid():N}.exe";
            string shimPath = Path.Combine(workingDirectory, baseName + ".cmd");
            File.WriteAllText(shimPath, "@echo off\r\n");

            string? resolved = InvokeResolveWindowsCommand(baseName, currentDirectory: workingDirectory);

            Assert.Equal(shimPath, resolved, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void ResolveWindowsCommand_BareNamePreferredOverDoubleExtensionShim()
    {
        // When both "foo.exe" and "foo.exe.cmd" exist, the exact command "foo.exe" wins: a command
        // that already has an extension is probed as the bare name first (PowerShell's ordering),
        // before any PATHEXT-appended candidate such as "foo.exe.cmd".
        string workingDirectory = Path.Combine(Path.GetTempPath(), $"mcp-resolve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            string baseName = $"probe-{Guid.NewGuid():N}.exe";
            string exactPath = Path.Combine(workingDirectory, baseName);
            File.WriteAllText(exactPath, string.Empty);
            File.WriteAllText(Path.Combine(workingDirectory, baseName + ".cmd"), "@echo off\r\n");

            string? resolved = InvokeResolveWindowsCommand(baseName, currentDirectory: workingDirectory);

            Assert.Equal(exactPath, resolved, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void ResolveWindowsCommand_RelativeDirectoryCommand_ResolvesFromWorkingDirectory()
    {
        string workingDirectory = Path.Combine(Path.GetTempPath(), $"mcp-resolve-{Guid.NewGuid():N}");
        string command = Path.Combine("relative tools", $"probe-{Guid.NewGuid():N}");
        Assert.False(Path.IsPathRooted(command));
        Assert.NotNull(Path.GetDirectoryName(command));

        string expectedPath = Path.Combine(workingDirectory, command + ".cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
        try
        {
            File.WriteAllText(expectedPath, "@echo off\r\n");

            string? resolved = InvokeResolveWindowsCommand(command, currentDirectory: workingDirectory);

            Assert.Equal(expectedPath, resolved, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void ResolveWindowsCommand_UsesProcessCurrentThenPathOrder()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mcp-resolve-{Guid.NewGuid():N}");
        string processDirectory = Path.Combine(root, "process");
        string currentDirectory = Path.Combine(root, "current");
        string pathDirectory = Path.Combine(root, "path");
        Directory.CreateDirectory(processDirectory);
        Directory.CreateDirectory(currentDirectory);
        Directory.CreateDirectory(pathDirectory);
        try
        {
            string command = $"probe-{Guid.NewGuid():N}";
            string processCandidate = Path.Combine(processDirectory, command + ".exe");
            string currentCandidate = Path.Combine(currentDirectory, command + ".exe");
            string pathCandidate = Path.Combine(pathDirectory, command + ".exe");
            File.WriteAllText(processCandidate, string.Empty);
            File.WriteAllText(currentCandidate, string.Empty);
            File.WriteAllText(pathCandidate, string.Empty);

            Assert.Equal(processCandidate, InvokeResolveWindowsCommand(command, Path.Combine(processDirectory, "host.exe"), currentDirectory, pathDirectory), ignoreCase: true);
            File.Delete(processCandidate);
            Assert.Equal(currentCandidate, InvokeResolveWindowsCommand(command, Path.Combine(processDirectory, "host.exe"), currentDirectory, pathDirectory), ignoreCase: true);
            File.Delete(currentCandidate);
            Assert.Equal(pathCandidate, InvokeResolveWindowsCommand(command, Path.Combine(processDirectory, "host.exe"), currentDirectory, pathDirectory), ignoreCase: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void ResolveWindowsCommand_PathHardeningHandlesQuotesDuplicatesAndMissingDots()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mcp-resolve-{Guid.NewGuid():N}");
        string currentDirectory = Path.Combine(root, "current");
        string pathDirectory = Path.Combine(root, "path with spaces");
        Directory.CreateDirectory(currentDirectory);
        Directory.CreateDirectory(pathDirectory);
        try
        {
            string command = $"probe-{Guid.NewGuid():N}";
            string expected = Path.Combine(pathDirectory, command + ".cmd");
            File.WriteAllText(expected, string.Empty);
            string path = $";\"{pathDirectory}\";{pathDirectory};";

            string? resolved = InvokeResolveWindowsCommand(command, processPath: null, currentDirectory, path, pathExt: "CMD;.cmd");

            Assert.Equal(expected, resolved, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void ResolveWindowsCommand_NoDefaultCurrentDirectorySearchUsesPath()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mcp-resolve-{Guid.NewGuid():N}");
        string currentDirectory = Path.Combine(root, "current");
        string pathDirectory = Path.Combine(root, "path");
        Directory.CreateDirectory(currentDirectory);
        Directory.CreateDirectory(pathDirectory);
        string? originalValue = Environment.GetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath");
        try
        {
            string command = $"probe-{Guid.NewGuid():N}";
            string currentCandidate = Path.Combine(currentDirectory, command + ".exe");
            string pathCandidate = Path.Combine(pathDirectory, command + ".exe");
            File.WriteAllText(currentCandidate, string.Empty);
            File.WriteAllText(pathCandidate, string.Empty);
            Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", "1");

            string? resolved = InvokeResolveWindowsCommand(command, processPath: null, currentDirectory, pathDirectory);

            Assert.Equal(pathCandidate, resolved, ignoreCase: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", originalValue);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void ResolveWindowsCommand_MissingPathExtProbesOnlyExactName()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mcp-resolve-{Guid.NewGuid():N}");
        string currentDirectory = Path.Combine(root, "current");
        string pathDirectory = Path.Combine(root, "path");
        Directory.CreateDirectory(currentDirectory);
        Directory.CreateDirectory(pathDirectory);
        try
        {
            string command = $"probe-{Guid.NewGuid():N}";
            string exactPath = Path.Combine(pathDirectory, command);
            File.WriteAllText(exactPath + ".exe", string.Empty);

            Assert.Null(InvokeResolveWindowsCommand(command, processPath: null, currentDirectory, pathDirectory, pathExt: null));

            File.WriteAllText(exactPath, string.Empty);
            Assert.Equal(exactPath, InvokeResolveWindowsCommand(command, processPath: null, currentDirectory, pathDirectory, pathExt: null), ignoreCase: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void WindowsCommandResolver_GetCurrentProcessPath_ReturnsExistingExecutable()
    {
        MethodInfo method = typeof(StdioClientTransport).Assembly
            .GetType("ModelContextProtocol.Client.WindowsCommandResolver", throwOnError: true)!
            .GetMethod("GetCurrentProcessPath", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not find WindowsCommandResolver.GetCurrentProcessPath via reflection.");

        string? processPath = (string?)method.Invoke(null, null);

        Assert.False(string.IsNullOrEmpty(processPath));
        Assert.True(File.Exists(processPath), $"Process path does not exist: {processPath}");
    }

    private static string? InvokeResolveWindowsCommand(
        string command,
        string? processPath = null,
        string? currentDirectory = null,
        string? path = null,
        string? pathExt = ".COM;.EXE;.BAT;.CMD")
    {
        MethodInfo method = typeof(StdioClientTransport).Assembly
            .GetType("ModelContextProtocol.Client.WindowsCommandResolver", throwOnError: true)!
            .GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not find WindowsCommandResolver.Resolve via reflection.");

        return (string?)method.Invoke(null, [command, processPath, currentDirectory ?? Environment.CurrentDirectory, path, pathExt]);
    }

    [Fact(Skip = "Platform not supported by this test.", SkipUnless = nameof(IsStdErrCallbackSupported))]
    public async Task InheritEnvironmentVariables_DefaultTrue_ChildSeesParentEnvVars()
    {
        // Check the same variable the False test checks for absence (HOME on Unix, USERNAME on Windows)
        // so the two tests form a direct symmetric pair: one asserts it IS set, the other asserts it is NOT.
        var tcs = new TaskCompletionSource<string>();
        StdioClientTransport transport = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ?
            new(new() { Command = "cmd", Arguments = ["/c", "if defined USERNAME (echo USERNAME_IS_SET >&2) else (echo USERNAME_NOT_SET >&2) & exit /b 1"], StandardErrorLines = line => tcs.TrySetResult(line) }, LoggerFactory) :
            new(new() { Command = "sh", Arguments = ["-c", "if [ -n \"$HOME\" ]; then echo HOME_IS_SET >&2; else echo HOME_NOT_SET >&2; fi; exit 1"], StandardErrorLines = line => tcs.TrySetResult(line) }, LoggerFactory);

        await Assert.ThrowsAnyAsync<IOException>(() => McpClient.CreateAsync(transport, loggerFactory: LoggerFactory, cancellationToken: TestContext.Current.CancellationToken));

        using var cts = new CancellationTokenSource(TestConstants.DefaultTimeout);
        string capturedLine = await tcs.Task.WaitAsync(cts.Token);

        Assert.Equal(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "USERNAME_IS_SET" : "HOME_IS_SET", capturedLine.Trim());
    }

    [Fact(Skip = "Platform not supported by this test.", SkipUnless = nameof(IsStdErrCallbackSupported))]
    public async Task InheritEnvironmentVariables_False_ChildDoesNotSeeParentEnvVars()
    {
        // Pass PATH so cmd/sh can be located. Verify that HOME (Unix) / USERNAME (Windows),
        // which are always set in the parent, are absent because they were not explicitly provided.
        var tcs = new TaskCompletionSource<string>();
        StdioClientTransport transport = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ?
            new(new()
            {
                Command = "cmd",
                Arguments = ["/c", "if defined USERNAME (echo USERNAME_IS_SET >&2) else (echo USERNAME_NOT_SET >&2) & exit /b 1"],
                InheritEnvironmentVariables = false,
                EnvironmentVariables = new Dictionary<string, string?> { ["PATH"] = Environment.GetEnvironmentVariable("PATH") },
                StandardErrorLines = line => tcs.TrySetResult(line)
            }, LoggerFactory) :
            new(new()
            {
                Command = "sh",
                Arguments = ["-c", "if [ -n \"$HOME\" ]; then echo HOME_IS_SET >&2; else echo HOME_NOT_SET >&2; fi; exit 1"],
                InheritEnvironmentVariables = false,
                EnvironmentVariables = new Dictionary<string, string?> { ["PATH"] = Environment.GetEnvironmentVariable("PATH") },
                StandardErrorLines = line => tcs.TrySetResult(line)
            }, LoggerFactory);

        await Assert.ThrowsAnyAsync<IOException>(() => McpClient.CreateAsync(transport, loggerFactory: LoggerFactory, cancellationToken: TestContext.Current.CancellationToken));

        using var cts = new CancellationTokenSource(TestConstants.DefaultTimeout);
        string capturedLine = await tcs.Task.WaitAsync(cts.Token);

        // HOME / USERNAME were in the parent but not passed — should be absent in the child.
        Assert.Equal(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "USERNAME_NOT_SET" : "HOME_NOT_SET", capturedLine.Trim());
    }

    [Fact(Skip = "Platform not supported by this test.", SkipUnless = nameof(IsStdErrCallbackSupported))]
    public async Task InheritEnvironmentVariables_False_WithExplicitVars_ChildSeesOnlyExplicitVars()
    {
        // Pass PATH + one explicit var. Verify HOME (Unix) / USERNAME (Windows) is absent,
        // and the explicitly provided variable is visible.
        const string explicitVarName = "MCP_STDIO_TEST_EXPLICIT_VAR";
        const string explicitVarValue = "explicit_test_value";

        var capturedLines = new List<string>();
        var lineCount = 0;
        var tcs = new TaskCompletionSource<bool>();
        void CaptureLines(string line)
        {
            lock (capturedLines)
            {
                capturedLines.Add(line.Trim());
                if (Interlocked.Increment(ref lineCount) >= 2)
                    tcs.TrySetResult(true);
            }
        }

        StdioClientTransport transport = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ?
            new(new()
            {
                Command = "cmd",
                Arguments = ["/c",
                    $"if defined USERNAME (echo USERNAME_IS_SET >&2) else (echo USERNAME_NOT_SET >&2) " +
                    $"& if defined {explicitVarName} (echo EXPLICIT_IS_SET >&2) else (echo EXPLICIT_NOT_SET >&2) " +
                    $"& exit /b 1"],
                InheritEnvironmentVariables = false,
                EnvironmentVariables = new Dictionary<string, string?> { ["PATH"] = Environment.GetEnvironmentVariable("PATH"), [explicitVarName] = explicitVarValue },
                StandardErrorLines = CaptureLines
            }, LoggerFactory) :
            new(new()
            {
                Command = "sh",
                Arguments = ["-c",
                    $"if [ -n \"$HOME\" ]; then echo HOME_IS_SET >&2; else echo HOME_NOT_SET >&2; fi; " +
                    $"if [ -n \"${explicitVarName}\" ]; then echo EXPLICIT_IS_SET >&2; else echo EXPLICIT_NOT_SET >&2; fi; exit 1"],
                InheritEnvironmentVariables = false,
                EnvironmentVariables = new Dictionary<string, string?> { ["PATH"] = Environment.GetEnvironmentVariable("PATH"), [explicitVarName] = explicitVarValue },
                StandardErrorLines = CaptureLines
            }, LoggerFactory);

        await Assert.ThrowsAnyAsync<IOException>(() => McpClient.CreateAsync(transport, loggerFactory: LoggerFactory, cancellationToken: TestContext.Current.CancellationToken));

        using var cts = new CancellationTokenSource(TestConstants.DefaultTimeout);
        await tcs.Task.WaitAsync(cts.Token);

        string allOutput = string.Join(Environment.NewLine, capturedLines);
        Assert.Contains(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "USERNAME_NOT_SET" : "HOME_NOT_SET", allOutput);
        Assert.Contains("EXPLICIT_IS_SET", allOutput);
    }

    [Fact]
    public void GetDefaultEnvironmentVariables_ReturnsFreshDictionaryEachCall()
    {
        var first = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        var second = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        Assert.NotSame(first, second);
    }

    [Fact]
    public void GetDefaultEnvironmentVariables_ReturnsCorrectComparer()
    {
        var result = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Equal(StringComparer.OrdinalIgnoreCase, result.Comparer);
        }
        else
        {
            Assert.Equal(StringComparer.Ordinal, result.Comparer);
        }
    }

    [Fact]
    public void GetDefaultEnvironmentVariables_ContainsOnlyAllowlistedKeys()
    {
        HashSet<string> allowedKeys = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new(StringComparer.OrdinalIgnoreCase)
            {
                "APPDATA", "HOMEDRIVE", "HOMEPATH", "LOCALAPPDATA", "PATH", "PATHEXT",
                "PROCESSOR_ARCHITECTURE", "PROGRAMFILES", "SYSTEMDRIVE", "SYSTEMROOT",
                "TEMP", "USERNAME", "USERPROFILE",
            }
            : new(StringComparer.Ordinal)
            {
                "HOME", "LOGNAME", "PATH", "SHELL", "TERM", "USER",
            };

        var result = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        foreach (var key in result.Keys)
        {
            Assert.Contains(key, allowedKeys);
        }
    }

    [Fact]
    public void GetDefaultEnvironmentVariables_ExcludesShellFunctionValues()
    {
        // Verify the postcondition: no returned values start with "()" (shell function markers).
        var result = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        foreach (var kvp in result)
        {
            Assert.False(kvp.Value?.StartsWith("()") ?? false,
                $"Value for '{kvp.Key}' starts with '()' and should have been filtered as a shell function.");
        }
    }

    [Fact]
    public void GetDefaultEnvironmentVariables_PathIsPresent_WhenSetInEnvironment()
    {
        // PATH is always set in a real process environment; verify it is included.
        var result = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        if (Environment.GetEnvironmentVariable("PATH") is not null)
        {
            Assert.True(result.ContainsKey("PATH"), "PATH should be present when it exists in the parent environment.");
        }
    }

    [Fact]
    public void GetDefaultEnvironmentVariables_DoesNotIncludeNonAllowlistedKeys()
    {
        // Keys that are definitely not on the allowlist must never appear.
        var result = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        Assert.False(result.ContainsKey("AWS_SECRET_ACCESS_KEY"));
        Assert.False(result.ContainsKey("GITHUB_TOKEN"));
        Assert.False(result.ContainsKey("OPENAI_API_KEY"));
    }

    [Fact]
    public async Task SendMessageAsync_Should_Use_LF_Not_CRLF()
    {
        using var serverInput = new MemoryStream();
        Pipe serverOutputPipe = new();

        var transport = new StreamClientTransport(serverInput, serverOutputPipe.Reader.AsStream(), LoggerFactory);
        await using var sessionTransport = await transport.ConnectAsync(TestContext.Current.CancellationToken);

        var message = new JsonRpcRequest { Method = "test", Id = new RequestId(44) };

        await sessionTransport.SendMessageAsync(message, TestContext.Current.CancellationToken);

        byte[] bytes = serverInput.ToArray();

        // The output should end with exactly \n (0x0A), not \r\n (0x0D 0x0A).
        Assert.True(bytes.Length > 1, "Output should contain message data");
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.NotEqual((byte)'\r', bytes[^2]);

        // Also verify the JSON content is valid
        var json = Encoding.UTF8.GetString(bytes).TrimEnd('\n');
        var expected = JsonSerializer.Serialize(message, McpJsonUtilities.DefaultOptions);
        Assert.Equal(expected, json);
    }

    [Fact]
    public async Task ReadMessagesAsync_Should_Accept_CRLF_Delimited_Messages()
    {
        Pipe serverInputPipe = new();
        Pipe serverOutputPipe = new();

        var transport = new StreamClientTransport(serverInputPipe.Writer.AsStream(), serverOutputPipe.Reader.AsStream(), LoggerFactory);
        await using var sessionTransport = await transport.ConnectAsync(TestContext.Current.CancellationToken);

        var message = new JsonRpcRequest { Method = "test", Id = new RequestId(44) };
        var json = JsonSerializer.Serialize(message, McpJsonUtilities.DefaultOptions);

        // Write a \r\n-delimited message to the server's output (which the client reads)
        await serverOutputPipe.Writer.WriteAsync(Encoding.UTF8.GetBytes($"{json}\r\n"), TestContext.Current.CancellationToken);

        var canRead = await sessionTransport.MessageReader.WaitToReadAsync(TestContext.Current.CancellationToken);

        Assert.True(canRead, "Should be able to read a \\r\\n-delimited message");
        Assert.True(sessionTransport.MessageReader.TryPeek(out var readMessage));
        Assert.NotNull(readMessage);
        Assert.IsType<JsonRpcRequest>(readMessage);
        Assert.Equal("44", ((JsonRpcRequest)readMessage).Id.ToString());
    }
}
