using System.Runtime.InteropServices;

namespace ModelContextProtocol.Client;

internal static class WindowsCommandResolver
{
    internal static string? Resolve(string command)
    {
        Debug.Assert(OperatingSystem.IsWindows());
        // PATHEXT expansion is the only thing this resolver adds over CreateProcess, which already searches
        // the current directory and PATH for the exact name. With no extensions to append there is nothing
        // left to do, so defer to CreateProcess by reporting no match.
        if (GetPathExtensions() is not { Length: > 0 } extensions)
        {
            return null;
        }

        // probeExactName is true when the name already carries an extension, so an explicitly named file is
        // tried verbatim before the PATHEXT candidates, e.g. npx.cmd -> probes "npx.cmd" first, then "npx.cmd.COM", ...
        string fileName = Path.GetFileName(command);
        bool probeExactName = fileName.Contains('.') ||
            extensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        if (Path.IsPathRooted(command))
        {
            return FindFirstCandidate(command, probeExactName, extensions);
        }

        if (command.AsSpan().IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            // A relative path with a directory part is resolved against the working directory only, never against PATH.
            return FindFirstCandidate(Path.Combine(Environment.CurrentDirectory, command), probeExactName, extensions);
        }

        // A bare command name is searched along ".;PATH" when the current directory participates, or "PATH"
        // otherwise, exactly as cmd.exe does per NeedCurrentDirectoryForExePath. Relative PATH entries are
        // resolved against the working directory.
        List<string> searchDirectories = [];
        if (NeedCurrentDirectoryForExePath(command))
        {
            searchDirectories.Add(Environment.CurrentDirectory);
        }
        searchDirectories.AddRange(GetPaths());

        HashSet<string> seenDirectories = new(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in searchDirectories)
        {
            if (seenDirectories.Add(directory) &&
                FindFirstCandidate(Path.Combine(directory, command), probeExactName, extensions) is { } result)
            {
                return result;
            }
        }

        return null;
    }

    private static string? FindFirstCandidate(string baseName, bool probeExactName, string[] extensions)
    {
        // File.Exists returns false (never throws) for a missing, malformed, or inaccessible path, and for
        // a directory, so any hit is an existing file. On Windows PATHEXT defines the executable extensions.
        if (probeExactName && File.Exists(baseName))
        {
            return baseName;
        }

        foreach (string extension in extensions)
        {
            string candidate = baseName + extension;
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string[] GetPaths() => Environment.GetEnvironmentVariable("PATH")?
        .Split([Path.PathSeparator], StringSplitOptions.RemoveEmptyEntries) ?? [];

    private static string[] GetPathExtensions() => Environment.GetEnvironmentVariable("PATHEXT")?
        .Split([Path.PathSeparator], StringSplitOptions.RemoveEmptyEntries) ?? [];

    // Whether the current directory should be searched for a bare command name. The Win32 API is called
    // rather than reading NoDefaultCurrentDirectoryInExePath directly, because its registry location can
    // change. https://learn.microsoft.com/windows/win32/api/processenv/nf-processenv-needcurrentdirectoryforexepatha
    [DllImport("kernel32.dll", EntryPoint = "NeedCurrentDirectoryForExePathW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NeedCurrentDirectoryForExePath(string exeName);
}
