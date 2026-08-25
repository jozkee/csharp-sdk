using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously

namespace ModelContextProtocol.Client;

/// <summary>
/// Provides a <see cref="IClientTransport"/> implemented via "stdio" (standard input/output).
/// </summary>
/// <remarks>
/// <para>
/// This transport launches an external process and communicates with it through standard input and output streams.
/// It's used to connect to MCP servers launched and hosted in child processes.
/// </para>
/// <para>
/// The transport manages the entire lifecycle of the process: starting it with specified command-line arguments
/// and environment variables, handling output, and properly terminating the process when the transport is closed.
/// </para>
/// </remarks>
public sealed partial class StdioClientTransport : IClientTransport
{
#if !NET
    // On .NET Framework, we need to synchronize access to Console.InputEncoding
    // to prevent race conditions when multiple transports are created concurrently.
    private static readonly object s_consoleEncodingLock = new();
#endif

    private readonly StdioClientTransportOptions _options;
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="StdioClientTransport"/> class.
    /// </summary>
    /// <param name="options">Configuration options for the transport, including the command to execute, arguments, working directory, and environment variables.</param>
    /// <param name="loggerFactory">A logger factory for creating loggers used for diagnostic output during transport operations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public StdioClientTransport(StdioClientTransportOptions options, ILoggerFactory? loggerFactory = null)
    {
        Throw.IfNull(options);

        _options = options;
        _loggerFactory = loggerFactory;
        Name = options.Name ?? $"stdio-{WhitespaceAndPeriods().Replace(Path.GetFileName(options.Command), "-")}";
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        string endpointName = Name;

        Process? process = null;
        bool processStarted = false;
        DataReceivedEventHandler? errorHandler = null;

        string command = _options.Command;
        IList<string>? arguments = _options.Arguments;

        ILogger logger = (ILogger?)_loggerFactory?.CreateLogger<StdioClientTransport>() ?? NullLogger.Instance;
        try
        {
            LogTransportConnecting(logger, endpointName);

            ProcessStartInfo startInfo = new()
            {
                FileName = command,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _options.WorkingDirectory ?? Environment.CurrentDirectory,
                StandardOutputEncoding = StreamClientSessionTransport.NoBomUtf8Encoding,
                StandardErrorEncoding = StreamClientSessionTransport.NoBomUtf8Encoding,
#if NET
                StandardInputEncoding = StreamClientSessionTransport.NoBomUtf8Encoding,
#endif
            };

            if (!_options.InheritEnvironmentVariables)
            {
                startInfo.Environment.Clear();
            }

            if (_options.EnvironmentVariables != null)
            {
                foreach (var entry in _options.EnvironmentVariables)
                {
                    startInfo.Environment[entry.Key] = entry.Value;
                }
            }

            // On Windows, resolve the command with a PATHEXT lookup: CreateProcess does not consult PATHEXT
            // when UseShellExecute is false, so a bare command like "npx" (really "npx.cmd") would otherwise
            // fail to launch. The resolver is Windows-only and returns null for commands that need no
            // expansion, in which case CreateProcess launches the command as given.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                WindowsCommandResolver.Resolve(command) is { } resolvedCommand)
            {
                // Launch the resolved target directly. Windows CreateProcess still routes .cmd/.bat files
                // through the command interpreter internally; that's expected.
                startInfo.FileName = resolvedCommand;
            }

            if (arguments is not null)
            {
                // Argument quoting is cmd-specific when the launched file is interpreted by cmd: Windows
                // re-invokes a .cmd/.bat script as cmd.exe /c "<command line>", and the script's own %*/%n
                // expansion re-parses the arguments a second time (the class of issue behind CVE-2024-24576).
                // For those targets the command line is built by hand so each argument is quoted in a way
                // that survives both cmd parses; any other executable receives its arguments verbatim via
                // normal argv quoting.
                if (RequiresCommandProcessorEscaping(startInfo.FileName))
                {
                    StringBuilder argsBuilder = new();
                    foreach (string arg in arguments)
                    {
                        if (argsBuilder.Length != 0)
                        {
                            argsBuilder.Append(' ');
                        }

                        AppendCommandProcessorArgument(argsBuilder, arg);
                    }

                    startInfo.Arguments = argsBuilder.ToString();
                }
                else
                {
#if NET
                    foreach (string arg in arguments)
                    {
                        startInfo.ArgumentList.Add(arg);
                    }
#else
                    StringBuilder argsBuilder = new();
                    foreach (string arg in arguments)
                    {
                        PasteArguments.AppendArgument(argsBuilder, arg);
                    }

                    startInfo.Arguments = argsBuilder.ToString();
#endif
                }
            }

            if (logger.IsEnabled(LogLevel.Trace))
            {
                LogCreateProcessForTransportDetailed(logger, endpointName, _options.Command,
                    startInfo.Arguments,
                    startInfo.WorkingDirectory);
            }
            else
            {
                LogCreateProcessForTransport(logger, endpointName, _options.Command);
            }

            process = new() { StartInfo = startInfo };

            // Set up stderr handling. Log all stderr output, and keep the last
            // few lines in a rolling log for use in exceptions.
            const int MaxStderrLength = 10; // keep the last 10 lines of stderr
            Queue<string> stderrRollingLog = new(MaxStderrLength);
            errorHandler = (sender, args) =>
            {
                string? data = args.Data;
                if (data is not null)
                {
                    lock (stderrRollingLog)
                    {
                        if (stderrRollingLog.Count >= MaxStderrLength)
                        {
                            stderrRollingLog.Dequeue();
                        }

                        stderrRollingLog.Enqueue(data);
                    }

                    try
                    {
                        _options.StandardErrorLines?.Invoke(data);
                    }
                    catch (Exception ex)
                    {
                        // Prevent exceptions in the user callback from propagating
                        // to the background thread that dispatches ErrorDataReceived,
                        // which would crash the process.
                        LogStderrCallbackFailed(logger, endpointName, ex);
                    }

                    LogReadStderr(logger, endpointName, data);
                }
            };
            process.ErrorDataReceived += errorHandler;

            // We need both stdin and stdout to use a no-BOM UTF-8 encoding. On .NET Core,
            // we can use ProcessStartInfo.StandardOutputEncoding/StandardInputEncoding, but
            // StandardInputEncoding doesn't exist on .NET Framework; instead, it always picks
            // up the encoding from Console.InputEncoding. As such, when not targeting .NET Core,
            // we temporarily change Console.InputEncoding to no-BOM UTF-8 around the Process.Start
            // call, to ensure it picks up the correct encoding.
#if NET
            processStarted = process.Start();
#else
            // IMPORTANT: This must be synchronized to prevent race conditions when multiple
            // transports are created concurrently.
            lock (s_consoleEncodingLock)
            {
                Encoding originalInputEncoding = Console.InputEncoding;
                bool encodingChanged = false;
                try
                {
                    try
                    {
                        Console.InputEncoding = StreamClientSessionTransport.NoBomUtf8Encoding;
                        encodingChanged = true;
                    }
                    catch
                    {
                        // Host has no usable console (e.g. WPF/WinForms on .NET Framework with no
                        // AllocConsole). The child inherits the current Console.InputEncoding;
                        // non-ASCII stdin may be misencoded, but the connect itself proceeds.
                    }

                    processStarted = process.Start();
                }
                finally
                {
                    if (encodingChanged)
                    {
                        Console.InputEncoding = originalInputEncoding;
                    }
                }
            }
#endif

            if (!processStarted)
            {
                LogTransportProcessStartFailed(logger, endpointName);
                throw new IOException("Failed to start MCP server process.");
            }

            LogTransportProcessStarted(logger, endpointName, process.Id);

            process.BeginErrorReadLine();

            return new StdioClientSessionTransport(_options, process, endpointName, stderrRollingLog, errorHandler, _loggerFactory);
        }
        catch (Exception ex)
        {
            LogTransportConnectFailed(logger, endpointName, ex);

            try
            {
                if (process is not null && errorHandler is not null)
                {
                    process.ErrorDataReceived -= errorHandler;
                }

                DisposeProcess(process, processStarted, _options.ShutdownTimeout);
            }
            catch (Exception ex2)
            {
                LogTransportShutdownFailed(logger, endpointName, ex2);
            }

            throw new IOException("Failed to connect transport.", ex);
        }
    }

    internal static void DisposeProcess(
        Process? process, bool processRunning, TimeSpan shutdownTimeout, Action? beforeDispose = null)
    {
        if (process is not null)
        {
            try
            {
                processRunning = processRunning && !HasExited(process);
                if (processRunning)
                {
                    // Wait for the process to exit.
                    // Kill the while process tree because the process may spawn child processes
                    // and Node.js does not kill its children when it exits properly.
                    process.KillTree(shutdownTimeout);
                }

                // Invoke the callback while the process handle is still valid,
                // e.g. to read ExitCode before Dispose() invalidates it.
                beforeDispose?.Invoke();
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>Gets a value that indicates whether <paramref name="process"/> has exited.</summary>
    internal static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Gets whether the file at <paramref name="path"/> is interpreted by cmd, in which case the arguments
    /// must be escaped for cmd's grammar. This is the case for cmd.exe itself and for a .cmd/.bat script,
    /// which Windows launches by re-invoking it through cmd.exe.
    /// </summary>
    private static bool RequiresCommandProcessorEscaping(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        string fileName = Path.GetFileName(path);
        return
            fileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "cmd.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "cmd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Appends <paramref name="argument"/> to <paramref name="builder"/>, quoting it for cmd when needed.
    /// </summary>
    /// <remarks>
    /// A .cmd/.bat script (and cmd.exe itself) is parsed by cmd, and the script's own <c>%*</c>/<c>%n</c>
    /// expansion re-parses the arguments a second time. Caret-escaping only survives a single parse, so an
    /// argument that contains whitespace or a cmd metacharacter (or is empty) is instead wrapped in double
    /// quotes, which cmd and the child's argv parser both treat literally across both parses. Embedded quotes
    /// are doubled and a trailing backslash run is doubled so it cannot escape the closing quote. This delivers
    /// the argument to the child intact while preventing cmd metacharacter injection (CVE-2024-24576).
    /// </remarks>
    private static void AppendCommandProcessorArgument(StringBuilder builder, string argument)
    {
        if (argument.Length != 0 && !RequiresCommandProcessorQuoting(argument))
        {
            builder.Append(argument);
            return;
        }

        string escaped = argument.Replace("\"", "\"\"");
        int trailingBackslashes = escaped.Length - escaped.TrimEnd('\\').Length;
        builder.Append('"').Append(escaped).Append('\\', trailingBackslashes).Append('"');
    }

    /// <summary>Gets whether <paramref name="argument"/> must be quoted so cmd does not interpret it.</summary>
    private static bool RequiresCommandProcessorQuoting(string argument)
    {
        foreach (char c in argument)
        {
            if (char.IsWhiteSpace(c) || c is '&' or '|' or '<' or '>' or '^' or '(' or ')' or '"')
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{EndpointName} connecting.")]
    private static partial void LogTransportConnecting(ILogger logger, string endpointName);

    [LoggerMessage(Level = LogLevel.Information, Message = "{EndpointName} starting server process. Command: '{Command}'.")]
    private static partial void LogCreateProcessForTransport(ILogger logger, string endpointName, string command);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{EndpointName} starting server process. Command: '{Command}', Arguments: {Arguments}, Working directory: {WorkingDirectory}.")]
    private static partial void LogCreateProcessForTransportDetailed(ILogger logger, string endpointName, string command, string? arguments, string workingDirectory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EndpointName} failed to start server process.")]
    private static partial void LogTransportProcessStartFailed(ILogger logger, string endpointName);

    [LoggerMessage(Level = LogLevel.Information, Message = "{EndpointName} received stderr log: '{Data}'.")]
    private static partial void LogReadStderr(ILogger logger, string endpointName, string data);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EndpointName} StandardErrorLines callback failed.")]
    private static partial void LogStderrCallbackFailed(ILogger logger, string endpointName, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "{EndpointName} started server process with PID {ProcessId}.")]
    private static partial void LogTransportProcessStarted(ILogger logger, string endpointName, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EndpointName} connect failed.")]
    private static partial void LogTransportConnectFailed(ILogger logger, string endpointName, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EndpointName} shutdown failed.")]
    private static partial void LogTransportShutdownFailed(ILogger logger, string endpointName, Exception exception);

#if NET
    [GeneratedRegex(@"[\s\.]+")]
    private static partial Regex WhitespaceAndPeriods();
#else
    private static Regex WhitespaceAndPeriods() => s_whitespaceAndPeriods;
    private static readonly Regex s_whitespaceAndPeriods = new(@"[\s\.]+", RegexOptions.Compiled);
#endif
}
