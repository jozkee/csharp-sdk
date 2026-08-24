using System.Runtime.InteropServices;

namespace ModelContextProtocol.Tests.Interop.WindowsCommandResolution;

/// <summary>
/// Test-only C# ports of command lookup algorithms used by Node's <c>which</c> 2.0.2,
/// CPython 3.14's <c>shutil.which</c>, and .NET's Unix <c>Process.Start</c> implementation.
/// </summary>
internal static class WhichReferenceImplementations
{
    /// <summary>
    /// Ports the synchronous Windows path in
    /// https://github.com/npm/node-which/blob/v2.0.2/which.js.
    /// </summary>
    public static string? NodeWhich(
        string command,
        string currentDirectory,
        string? path = null,
        string? pathExt = null)
    {
        pathExt = !string.IsNullOrEmpty(pathExt) ?
            pathExt :
            Environment.GetEnvironmentVariable("PATHEXT");
        string effectivePathExt = !string.IsNullOrEmpty(pathExt) ? pathExt! : ".EXE;.CMD;.BAT;.COM";
        string[] extensions = effectivePathExt.Split(';');
        if (command.Contains('.') && extensions.FirstOrDefault() is not "")
        {
            extensions = [string.Empty, .. extensions];
        }

        string effectivePath = !string.IsNullOrEmpty(path) ?
            path! :
            Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        IEnumerable<string> directories =
            ContainsDirectorySeparator(command) ?
                [string.Empty] :
                [currentDirectory, .. effectivePath.Split(';')];

        foreach (string rawDirectory in directories)
        {
            string directory =
                rawDirectory.Length >= 2 && rawDirectory[0] == '"' && rawDirectory[^1] == '"' ?
                    rawDirectory.Substring(1, rawDirectory.Length - 2) :
                    rawDirectory;
            string basePath = Path.Combine(directory, command);

            foreach (string extension in extensions)
            {
                string candidate = basePath + extension;
                if (IsNodeExecutable(candidate, currentDirectory, effectivePathExt))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Ports the default executable-search mode of CPython 3.14's
    /// https://github.com/python/cpython/blob/v3.14.0/Lib/shutil.py.
    /// </summary>
    public static string? PythonWhich(
        string command,
        string currentDirectory,
        string? path = null,
        string? pathExt = null)
    {
        string? directory = Path.GetDirectoryName(command);
        string fileName = Path.GetFileName(command);
        IEnumerable<string> directories;

        if (!string.IsNullOrEmpty(directory))
        {
            directories = [directory];
        }
        else
        {
            path = path is not null ?
                path :
                Environment.GetEnvironmentVariable("PATH") ?? ".;C:\\bin";
            if (path.Length == 0)
            {
                return null;
            }

            string[] pathDirectories = path.Split(';');
            directories =
                NeedCurrentDirectoryForExePath(fileName) ?
                    [".", .. pathDirectories] :
                    pathDirectories;
        }

        pathExt = !string.IsNullOrEmpty(pathExt) ?
            pathExt :
            Environment.GetEnvironmentVariable("PATHEXT");
        pathExt = !string.IsNullOrEmpty(pathExt) ?
            pathExt :
            ".COM;.EXE;.BAT;.CMD;.VBS;.JS;.WS;.MSC";
        string[] extensions = pathExt!
            .Split([';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static extension => extension.TrimEnd('.'))
            .ToArray();
        List<string> candidateNames = extensions.Select(extension => fileName + extension).ToList();

        if (extensions.Any(extension => fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            candidateNames.Insert(0, fileName);
        }

        HashSet<string> seenDirectories = new(StringComparer.OrdinalIgnoreCase);
        foreach (string searchDirectory in directories)
        {
            if (!seenDirectories.Add(searchDirectory))
            {
                continue;
            }

            foreach (string candidateName in candidateNames)
            {
                string candidate = Path.Combine(searchDirectory, candidateName);
                if (IsFile(candidate, currentDirectory))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Ports the Unix executable resolution used by <c>Process.Start</c> before <c>execve</c>.
    /// </summary>
    public static string? DotNetUnixProcessStartWhich(
        string command,
        string? processPath,
        string currentDirectory,
        string? path)
    {
        if (Path.IsPathRooted(command))
        {
            return command;
        }

        if (processPath is not null)
        {
            try
            {
                string candidate = Path.Combine(Path.GetDirectoryName(processPath)!, command);
                if (IsUnixExecutable(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        string currentDirectoryCandidate = Path.Combine(currentDirectory, command);
        if (IsUnixExecutable(currentDirectoryCandidate))
        {
            return currentDirectoryCandidate;
        }

        if (path is not null)
        {
            foreach (string pathDirectory in path.Split([Path.PathSeparator], StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(pathDirectory, command);
                if (IsUnixExecutable(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool IsNodeExecutable(string candidate, string currentDirectory, string pathExt)
    {
        if (!IsFile(candidate, currentDirectory))
        {
            return false;
        }

        string[] executableExtensions = pathExt.Split(';');
        return executableExtensions.Contains(string.Empty) ||
            executableExtensions.Any(extension =>
                extension.Length != 0 && candidate.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFile(string candidate, string currentDirectory)
    {
        string path = Path.IsPathRooted(candidate) ? candidate : Path.Combine(currentDirectory, candidate);
        return File.Exists(path);
    }

    private static bool IsUnixExecutable(string path) =>
        UnixAccess(path, executePermission: 1) == 0 && !Directory.Exists(path);

    private static bool ContainsDirectorySeparator(string command) =>
        command.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
        command.IndexOf(Path.AltDirectorySeparatorChar) >= 0;

    private static bool NeedCurrentDirectoryForExePath(string command)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        return NeedCurrentDirectoryForExePathW(command);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NeedCurrentDirectoryForExePathW(string executableName);

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int UnixAccess(string path, int executePermission);
}
