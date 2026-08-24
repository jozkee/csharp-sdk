using System.Runtime.InteropServices;

namespace ModelContextProtocol.Tests.Interop.WindowsCommandResolution;

[Collection(nameof(DisableParallelization))]
public sealed class WhichReferenceImplementationTests
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static bool IsUnix => !IsWindows;

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void NodeWhich_RelativeDirectoryCommand_ProbesPathExtWithoutSearchingPath()
    {
        using TemporaryWhichEnvironment environment = new();
        string command = Path.Combine("relative-tools", "launcher");
        string expected = command + ".CMD";
        environment.CreateCurrentDirectoryFile(expected);
        environment.CreatePathFile(command + ".EXE");

        string? resolved = WhichReferenceImplementations.NodeWhich(
            command,
            environment.CurrentDirectory,
            environment.PathDirectory,
            ".EXE;.CMD");

        Assert.Equal(expected, resolved, ignoreCase: true);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void NodeWhich_RelativeDirectoryCommand_DoesNotUsePathFallback()
    {
        using TemporaryWhichEnvironment environment = new();
        string command = Path.Combine("relative-tools", "launcher");
        environment.CreatePathFile(command + ".CMD");

        string? resolved = WhichReferenceImplementations.NodeWhich(
            command,
            environment.CurrentDirectory,
            environment.PathDirectory,
            ".CMD");

        Assert.Null(resolved);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void NodeWhich_CommandWithExtension_ProbesExactThenDoubleExtension()
    {
        using TemporaryWhichEnvironment environment = new();
        string command = "launcher.EXE";
        string doubleExtension = command + ".CMD";
        environment.CreateCurrentDirectoryFile(doubleExtension);

        string? resolved = WhichReferenceImplementations.NodeWhich(
            command,
            environment.CurrentDirectory,
            path: string.Empty,
            pathExt: ".EXE;.CMD");

        Assert.Equal(Path.Combine(environment.CurrentDirectory, doubleExtension), resolved, ignoreCase: true);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void NodeWhich_EmptyOptionsUseEnvironmentFallbacks()
    {
        using TemporaryWhichEnvironment environment = new();
        using EnvironmentVariableScope path = new("PATH", environment.PathDirectory);
        using EnvironmentVariableScope pathExt = new("PATHEXT", ".CMD");
        environment.CreatePathFile("launcher.CMD");

        string? resolved = WhichReferenceImplementations.NodeWhich(
            "launcher",
            environment.CurrentDirectory,
            path: string.Empty,
            pathExt: string.Empty);

        Assert.Equal(Path.Combine(environment.PathDirectory, "launcher.CMD"), resolved, ignoreCase: true);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void PythonWhich_RelativeDirectoryCommand_ProbesPathExtWithoutSearchingPath()
    {
        using TemporaryWhichEnvironment environment = new();
        string command = Path.Combine("relative-tools", "launcher");
        string expected = command + ".CMD";
        environment.CreateCurrentDirectoryFile(expected);
        environment.CreatePathFile(command + ".EXE");

        string? resolved = WhichReferenceImplementations.PythonWhich(
            command,
            environment.CurrentDirectory,
            environment.PathDirectory,
            ".EXE;.CMD");

        Assert.Equal(expected, resolved, ignoreCase: true);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void PythonWhich_RelativeDirectoryCommand_DoesNotUsePathFallback()
    {
        using TemporaryWhichEnvironment environment = new();
        string command = Path.Combine("relative-tools", "launcher");
        environment.CreatePathFile(command + ".CMD");

        string? resolved = WhichReferenceImplementations.PythonWhich(
            command,
            environment.CurrentDirectory,
            environment.PathDirectory,
            ".CMD");

        Assert.Null(resolved);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void PythonWhich_CommandWithPathExtExtension_ProbesExactThenDoubleExtension()
    {
        using TemporaryWhichEnvironment environment = new();
        string command = "launcher.EXE";
        environment.CreatePathFile(command);
        environment.CreatePathFile(command + ".CMD");

        string? resolved = WhichReferenceImplementations.PythonWhich(
            command,
            environment.CurrentDirectory,
            environment.PathDirectory,
            ".EXE;.CMD");

        Assert.Equal(Path.Combine(environment.PathDirectory, command), resolved, ignoreCase: true);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void PythonWhich_EmptyPathExtUsesEnvironmentFallback()
    {
        using TemporaryWhichEnvironment environment = new();
        using EnvironmentVariableScope pathExt = new("PATHEXT", ".JS");
        string command = Path.Combine("relative-tools", "launcher");
        string expected = command + ".JS";
        environment.CreateCurrentDirectoryFile(expected);

        string? resolved = WhichReferenceImplementations.PythonWhich(
            command,
            environment.CurrentDirectory,
            pathExt: string.Empty);

        Assert.Equal(expected, resolved, ignoreCase: true);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void PythonWhich_MissingPathExtUsesCpythonDefault()
    {
        using TemporaryWhichEnvironment environment = new();
        using EnvironmentVariableScope pathExt = new("PATHEXT", value: null);
        string command = Path.Combine("relative-tools", "launcher");
        string expected = command + ".JS";
        environment.CreateCurrentDirectoryFile(expected);

        string? resolved = WhichReferenceImplementations.PythonWhich(
            command,
            environment.CurrentDirectory);

        Assert.Equal(expected, resolved, ignoreCase: true);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows-only test.")]
    public void PythonWhich_MissingPathUsesOsDefaultPath()
    {
        using TemporaryWhichEnvironment environment = new();
        using EnvironmentVariableScope path = new("PATH", value: null);
        environment.CreateCurrentDirectoryFile("launcher.EXE");

        string? resolved = WhichReferenceImplementations.PythonWhich(
            "launcher",
            environment.CurrentDirectory,
            pathExt: ".EXE");

        Assert.Equal(Path.Combine(".", "launcher.EXE"), resolved, ignoreCase: true);
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "Unix-only test.")]
    public void DotNetUnixProcessStartWhich_UsesProcessCurrentThenPathOrder()
    {
        using TemporaryWhichEnvironment environment = new();
        string processDirectory = environment.CreateDirectory("process");
        string currentDirectory = environment.CreateDirectory("current");
        string pathDirectory = environment.CreateDirectory("path");
        string processPath = Path.Combine(processDirectory, "dotnet");
        environment.CreateExecutable(processDirectory, "launcher");
        environment.CreateExecutable(currentDirectory, "launcher");
        environment.CreateExecutable(pathDirectory, "launcher");

        string? resolved = WhichReferenceImplementations.DotNetUnixProcessStartWhich(
            "launcher",
            processPath,
            currentDirectory,
            pathDirectory);

        Assert.Equal(Path.Combine(processDirectory, "launcher"), resolved);

        File.Delete(resolved!);
        resolved = WhichReferenceImplementations.DotNetUnixProcessStartWhich(
            "launcher",
            processPath,
            currentDirectory,
            pathDirectory);

        Assert.Equal(Path.Combine(currentDirectory, "launcher"), resolved);

        File.Delete(resolved!);
        resolved = WhichReferenceImplementations.DotNetUnixProcessStartWhich(
            "launcher",
            processPath,
            currentDirectory,
            pathDirectory);

        Assert.Equal(Path.Combine(pathDirectory, "launcher"), resolved);
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "Unix-only test.")]
    public void DotNetUnixProcessStartWhich_SkipsDirectoriesAndNonExecutableFiles()
    {
        using TemporaryWhichEnvironment environment = new();
        string processDirectory = environment.CreateDirectory("process");
        string currentDirectory = environment.CreateDirectory("current");
        string firstPathDirectory = environment.CreateDirectory("path1");
        string secondPathDirectory = environment.CreateDirectory("path2");
        Directory.CreateDirectory(Path.Combine(processDirectory, "launcher"));
        environment.CreateFile(currentDirectory, "launcher");
        environment.CreateExecutable(secondPathDirectory, "launcher");
        string path = string.Join(Path.PathSeparator.ToString(), string.Empty, firstPathDirectory, string.Empty, secondPathDirectory);

        string? resolved = WhichReferenceImplementations.DotNetUnixProcessStartWhich(
            "launcher",
            Path.Combine(processDirectory, "dotnet"),
            currentDirectory,
            path);

        Assert.Equal(Path.Combine(secondPathDirectory, "launcher"), resolved);

        File.Delete(resolved!);
        resolved = WhichReferenceImplementations.DotNetUnixProcessStartWhich(
            "launcher",
            Path.Combine(processDirectory, "dotnet"),
            currentDirectory,
            path: null);

        Assert.Null(resolved);
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "Unix-only test.")]
    public void DotNetUnixProcessStartWhich_RootedPathIsReturnedWithoutValidation()
    {
        string command = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, $"missing-{Guid.NewGuid():N}");

        string? resolved = WhichReferenceImplementations.DotNetUnixProcessStartWhich(
            command,
            processPath: null,
            Directory.GetCurrentDirectory(),
            path: null);

        Assert.Equal(command, resolved);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _originalValue);
    }

    private sealed class TemporaryWhichEnvironment : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"mcp-which-{Guid.NewGuid():N}");

        public TemporaryWhichEnvironment()
        {
            CurrentDirectory = Path.Combine(_root, "current");
            PathDirectory = Path.Combine(_root, "path");
            Directory.CreateDirectory(CurrentDirectory);
            Directory.CreateDirectory(PathDirectory);
        }

        public string CurrentDirectory { get; }

        public string PathDirectory { get; }

        public void CreateCurrentDirectoryFile(string relativePath) =>
            CreateFileCore(CurrentDirectory, relativePath);

        public void CreatePathFile(string relativePath) =>
            CreateFileCore(PathDirectory, relativePath);

        public string CreateDirectory(string relativePath)
        {
            string path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public void CreateFile(string root, string relativePath) =>
            CreateFileCore(root, relativePath);

        public void CreateExecutable(string root, string relativePath)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException();
            }

            string path = CreateFileCore(root, relativePath);
            if (Chmod(path, mode: 0x140) != 0)
            {
                throw new IOException($"chmod failed with errno {Marshal.GetLastWin32Error()}.");
            }
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);

        private static string CreateFileCore(string root, string relativePath)
        {
            string path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
            return path;
        }

        [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
        private static extern int Chmod(string path, uint mode);
    }
}
