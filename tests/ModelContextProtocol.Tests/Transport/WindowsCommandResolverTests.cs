using ModelContextProtocol.Client;
using ModelContextProtocol.Tests.Utils;
using System.Reflection;

namespace ModelContextProtocol.Tests.Transport;

/// <summary>
/// Tests for the which-style command resolution (PATH/PATHEXT lookup) that <see cref="StdioClientTransport"/>
/// performs before launching a server process. The resolver runs on both Unix and Windows; Unix simply has
/// no PATHEXT and no system directory search.
/// </summary>
public class WindowsCommandResolverTests(ITestOutputHelper testOutputHelper) : LoggedTest(testOutputHelper)
{
    /// <summary>
    /// A typical Windows PATHEXT value, separated the way the current platform separates path lists. The
    /// resolver is data-driven, so it can be exercised on any platform; the entries are lowercase so that
    /// probing also matches on case-sensitive file systems.
    /// </summary>
    private static readonly string WindowsPathExt = string.Join(Path.PathSeparator, [".com", ".exe", ".bat", ".cmd"]);

    /// <summary>Sentinel for "use whatever PATHEXT this platform would supply", so tests can also pass an explicit <see langword="null"/>.</summary>
    private const string PlatformPathExt = "\u0000platform";

    public static bool IsUnix => !PlatformDetection.IsWindows;

    [Fact]
    public void Resolve_BareName_FoundInPathDirectory()
    {
        using TempDirectory root = new();
        string pathDirectory = root.CreateSubdirectory("path");
        string command = $"probe-{Guid.NewGuid():N}";
        string expected = CreateExecutableFile(Path.Combine(pathDirectory, command + ExecutableExtension));

        AssertSamePath(expected, Resolve(command, path: pathDirectory));
    }

    [Fact]
    public void Resolve_CommandNotFound_ReturnsNull()
    {
        using TempDirectory root = new();
        Assert.Null(Resolve($"probe-{Guid.NewGuid():N}", path: root.Path, currentDirectory: root.Path));
    }

    [Fact]
    public void Resolve_RootedCommand_ResolvedWithoutSearching()
    {
        using TempDirectory root = new();
        string command = Path.Combine(root.Path, $"probe-{Guid.NewGuid():N}");
        string expected = CreateExecutableFile(command + ExecutableExtension);

        AssertSamePath(expected, Resolve(command));
        Assert.Null(Resolve(Path.Combine(root.Path, $"missing-{Guid.NewGuid():N}")));
    }

    [Fact]
    public void Resolve_SearchesProcessDirectoryThenCurrentDirectoryThenPath()
    {
        using TempDirectory root = new();
        string processDirectory = root.CreateSubdirectory("process");
        string currentDirectory = root.CreateSubdirectory("current");
        string pathDirectory = root.CreateSubdirectory("path");

        string command = $"probe-{Guid.NewGuid():N}";
        string processCandidate = CreateExecutableFile(Path.Combine(processDirectory, command + ExecutableExtension));
        string currentCandidate = CreateExecutableFile(Path.Combine(currentDirectory, command + ExecutableExtension));
        string pathCandidate = CreateExecutableFile(Path.Combine(pathDirectory, command + ExecutableExtension));

        string processPath = Path.Combine(processDirectory, "host" + ExecutableExtension);

        AssertSamePath(processCandidate, Resolve(command, processPath, currentDirectory, pathDirectory));
        File.Delete(processCandidate);
        AssertSamePath(currentCandidate, Resolve(command, processPath, currentDirectory, pathDirectory));
        File.Delete(currentCandidate);
        AssertSamePath(pathCandidate, Resolve(command, processPath, currentDirectory, pathDirectory));
    }

    [Fact]
    public void Resolve_RelativePathEntry_ResolvedAgainstCurrentDirectory()
    {
        // A relative PATH entry (for example "tools") must be evaluated against the supplied working
        // directory, not the host's actual current directory.
        using TempDirectory root = new();
        string toolsDirectory = root.CreateSubdirectory("tools");
        string command = $"probe-{Guid.NewGuid():N}";
        string expected = CreateExecutableFile(Path.Combine(toolsDirectory, command + ExecutableExtension));

        AssertSamePath(expected, Resolve(command, currentDirectory: root.Path, path: "tools"));
    }

    [Fact]
    public void Resolve_NoDefaultCurrentDirectoryInExePath_SkipsCurrentDirectory()
    {
        using TempDirectory root = new();
        string currentDirectory = root.CreateSubdirectory("current");
        string pathDirectory = root.CreateSubdirectory("path");

        string command = $"probe-{Guid.NewGuid():N}";
        CreateExecutableFile(Path.Combine(currentDirectory, command + ExecutableExtension));
        string pathCandidate = CreateExecutableFile(Path.Combine(pathDirectory, command + ExecutableExtension));

        AssertSamePath(pathCandidate, Resolve(command, currentDirectory: currentDirectory, path: pathDirectory, noDefaultCurrentDirectoryInExePath: "1"));
    }

    [Fact]
    public void Resolve_PathEntries_HandlesQuotesAndDuplicates()
    {
        using TempDirectory root = new();
        string pathDirectory = root.CreateSubdirectory("path with spaces");
        string command = $"probe-{Guid.NewGuid():N}";
        string expected = CreateExecutableFile(Path.Combine(pathDirectory, command + ExecutableExtension));

        string path = $"{Path.PathSeparator}\"{pathDirectory}\"{Path.PathSeparator}{pathDirectory}{Path.PathSeparator}";

        AssertSamePath(expected, Resolve(command, currentDirectory: root.Path, path: path));
    }

    [Fact]
    public void Resolve_AppendsPathExtExtensions()
    {
        // A bare command such as "npx" must resolve to the "npx.cmd" shim via PATHEXT.
        using TempDirectory root = new();
        string pathDirectory = root.CreateSubdirectory("path");
        string command = $"probe-{Guid.NewGuid():N}";
        string expected = CreateExecutableFile(Path.Combine(pathDirectory, command + ".cmd"));

        AssertSamePath(expected, Resolve(command, path: pathDirectory, pathExt: WindowsPathExt));

        // Without PATHEXT, only the exact name is probed.
        Assert.Null(Resolve(command, path: pathDirectory, pathExt: null));
    }

    [Fact]
    public void Resolve_PathExtEntries_AreNormalizedAndDeduplicated()
    {
        using TempDirectory root = new();
        string pathDirectory = root.CreateSubdirectory("path");
        string command = $"probe-{Guid.NewGuid():N}";
        string expected = CreateExecutableFile(Path.Combine(pathDirectory, command + ".cmd"));

        // Entries may be missing the leading dot, be padded, or repeat with different casing.
        char separator = Path.PathSeparator;
        AssertSamePath(expected, Resolve(command, path: pathDirectory, pathExt: $" cmd {separator} .cmd {separator}{separator}.CMD"));
    }

    [Fact]
    public void Resolve_CommandWithExtension_PrefersExactNameOverPathExtCandidate()
    {
        using TempDirectory root = new();
        string pathDirectory = root.CreateSubdirectory("path");
        string command = $"probe-{Guid.NewGuid():N}.exe";
        string shim = CreateExecutableFile(Path.Combine(pathDirectory, command + ".cmd"));

        // With only the shim present, the PATHEXT candidate wins.
        AssertSamePath(shim, Resolve(command, path: pathDirectory, pathExt: WindowsPathExt));

        // Once the exact name exists, it takes precedence.
        string exact = CreateExecutableFile(Path.Combine(pathDirectory, command));
        AssertSamePath(exact, Resolve(command, path: pathDirectory, pathExt: WindowsPathExt));
    }

    [Fact]
    public void Resolve_DottedDirectory_DoesNotTreatDirectoryDotAsExtension()
    {
        // A dot in a *directory* name must not make an extensionless command probe its exact
        // (extensionless) name; the decision is based on the file name only.
        using TempDirectory root = new();
        string dottedDirectory = root.CreateSubdirectory("tools.v1");
        string command = Path.Combine(dottedDirectory, "server");
        CreateExecutableFile(command); // Extensionless decoy that must not win.
        string expected = CreateExecutableFile(command + ".exe");

        AssertSamePath(expected, Resolve(command, pathExt: WindowsPathExt));
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "Unix-only test.")]
    public void Resolve_Unix_SkipsFilesWithoutExecutePermission()
    {
        using TempDirectory root = new();
        string pathDirectory = root.CreateSubdirectory("path");
        string command = $"probe-{Guid.NewGuid():N}";
        string candidate = Path.Combine(pathDirectory, command);
        File.WriteAllText(candidate, string.Empty);

#if NET
        Assert.Null(Resolve(command, path: pathDirectory));
#endif

        CreateExecutableFile(candidate);
        AssertSamePath(candidate, Resolve(command, path: pathDirectory));
    }

    [Fact]
    public void Resolve_UsesCurrentProcessEnvironment()
    {
        // The public overload takes only the command and working directory: PATH, PATHEXT, and the
        // process path all come from the current process' environment.
        using TempDirectory root = new();
        string pathDirectory = root.CreateSubdirectory("path");
        string command = $"probe-{Guid.NewGuid():N}";
        string expected = CreateExecutableFile(Path.Combine(pathDirectory, command + ExecutableExtension));

        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Assert.Null(ResolveFromCurrentEnvironment(command, root.Path));

            Environment.SetEnvironmentVariable("PATH", pathDirectory + Path.PathSeparator + originalPath);
            AssertSamePath(expected, ResolveFromCurrentEnvironment(command, root.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    private static string? ResolveFromCurrentEnvironment(string command, string currentDirectory)
    {
        MethodInfo method = ResolverType.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static, binder: null, [typeof(string), typeof(string)], modifiers: null)
            ?? throw new InvalidOperationException("Could not find WindowsCommandResolver.Resolve via reflection.");

        return (string?)method.Invoke(null, [command, currentDirectory]);
    }

    private static string ExecutableExtension => PlatformDetection.IsWindows ? ".exe" : "";

    private static Type ResolverType { get; } = typeof(StdioClientTransport).Assembly
        .GetType("ModelContextProtocol.Client.WindowsCommandResolver", throwOnError: true)!;

    private static string? Resolve(
        string command,
        string? processPath = null,
        string? currentDirectory = null,
        string? path = null,
        string? pathExt = PlatformPathExt,
        string? noDefaultCurrentDirectoryInExePath = null)
    {
        if (pathExt == PlatformPathExt)
        {
            pathExt = PlatformDetection.IsWindows ? WindowsPathExt : null;
        }

        MethodInfo method = ResolverType.GetMethod(
            "Resolve",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string)],
            modifiers: null)
            ?? throw new InvalidOperationException("Could not find WindowsCommandResolver.Resolve via reflection.");

        return (string?)method.Invoke(null, [command, processPath, currentDirectory ?? Environment.CurrentDirectory, path, pathExt, noDefaultCurrentDirectoryInExePath]);
    }

    /// <summary>Creates the file if it doesn't exist and, on Unix, marks it executable.</summary>
    private static string CreateExecutableFile(string path)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, PlatformDetection.IsWindows ? "@echo off\r\n" : "#!/bin/sh\n");
        }

#if NET
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
#endif

        return path;
    }

    private static void AssertSamePath(string expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(Path.GetFullPath(expected), actual, ignoreCase: PlatformDetection.IsWindows);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mcp-resolve-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateSubdirectory(string name)
        {
            string directory = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(directory);
            return directory;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
