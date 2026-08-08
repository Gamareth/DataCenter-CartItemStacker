using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;

return UpdaterProgram.Run(args);

internal static class UpdaterProgram
{
    private const string ExpectedAssemblyName = "CartItemStacker";
    private const string ExpectedDestinationName = "CartItemStacker.dll";
    private const int MaximumAttempts = 10;
    private static string _logPath;

    internal static int Run(string[] arguments)
    {
        try
        {
            var options = ParseArguments(arguments);
            _logPath = Require(options, "--log");
            var processId = ParseInt32(Require(options, "--pid"), "--pid");
            var sourcePath = Path.GetFullPath(Require(options, "--source"));
            var destinationPath = Path.GetFullPath(Require(options, "--destination"));
            var expectedHash = Require(options, "--sha256").ToUpperInvariant();
            var expectedVersion = Version.Parse(Require(options, "--version"));
            var timestamp = ParseUInt32(
                Require(options, "--timestamp"),
                "--timestamp");
            var statePath = Path.GetFullPath(Require(options, "--state"));
            var successMarkerPath = Path.GetFullPath(
                Require(options, "--success-marker"));

            ValidateArguments(
                sourcePath,
                destinationPath,
                expectedHash,
                expectedVersion);
            Log($"Waiting for Data Center process {processId} to exit.");
            WaitForProcessExit(processId);
            InstallWithRetries(
                sourcePath,
                destinationPath,
                expectedHash,
                expectedVersion);
            WriteState(statePath, timestamp);
            WriteSuccessMarker(
                successMarkerPath,
                expectedVersion,
                expectedHash,
                timestamp);
            Log(
                $"Installed Cart Item Stacker {expectedVersion} successfully.");
            return 0;
        }
        catch (Exception exception)
        {
            Log($"Update failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static void ValidateArguments(
        string sourcePath,
        string destinationPath,
        string expectedHash,
        Version expectedVersion)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Workshop DLL is missing.", sourcePath);
        if (!string.Equals(
                Path.GetFileName(destinationPath),
                ExpectedDestinationName,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected destination file name.");
        if (!IsSha256(expectedHash))
            throw new InvalidOperationException("Expected SHA-256 is invalid.");

        VerifyAssembly(sourcePath, expectedHash, expectedVersion);
    }

    private static void WaitForProcessExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit();
        }
        catch (ArgumentException)
        {
            // The game already exited between updater creation and attachment.
        }
    }

    private static void InstallWithRetries(
        string sourcePath,
        string destinationPath,
        string expectedHash,
        Version expectedVersion)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath) ??
            throw new InvalidOperationException("Destination directory is missing.");
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = destinationPath + ".pending";

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                VerifyAssembly(sourcePath, expectedHash, expectedVersion);
                File.Copy(sourcePath, temporaryPath, overwrite: true);
                VerifyAssembly(temporaryPath, expectedHash, expectedVersion);
                File.Move(temporaryPath, destinationPath, overwrite: true);
                VerifyAssembly(destinationPath, expectedHash, expectedVersion);
                return;
            }
            catch (Exception exception) when (attempt < MaximumAttempts)
            {
                TryDelete(temporaryPath);
                Log(
                    $"Install attempt {attempt}/{MaximumAttempts} failed: " +
                    $"{exception.Message}");
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }
    }

    private static void VerifyAssembly(
        string path,
        string expectedHash,
        Version expectedVersion)
    {
        var assemblyName = AssemblyName.GetAssemblyName(path);
        if (!string.Equals(
                assemblyName.Name,
                ExpectedAssemblyName,
                StringComparison.Ordinal) ||
            assemblyName.Version != expectedVersion)
            throw new InvalidDataException("DLL assembly identity is invalid.");

        var actualHash = ComputeSha256(path);
        if (!string.Equals(
                actualHash,
                expectedHash,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("DLL SHA-256 verification failed.");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var algorithm = SHA256.Create();
        return Convert.ToHexString(algorithm.ComputeHash(stream));
    }

    private static void WriteState(string path, uint timestamp)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(
            path,
            timestamp.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteSuccessMarker(
        string path,
        Version version,
        string hash,
        uint timestamp)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllLines(
            path,
            new[]
            {
                version.ToString(),
                hash,
                timestamp.ToString(CultureInfo.InvariantCulture),
            });
    }

    private static Dictionary<string, string> ParseArguments(string[] arguments)
    {
        if (arguments.Length % 2 != 0)
            throw new ArgumentException("Updater arguments must be name/value pairs.");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (!result.TryAdd(arguments[index], arguments[index + 1]))
                throw new ArgumentException($"Duplicate argument '{arguments[index]}'.");
        }

        return result;
    }

    private static string Require(
        IReadOnlyDictionary<string, string> options,
        string name) =>
        options.TryGetValue(name, out var value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing argument '{name}'.");

    private static int ParseInt32(string value, string name) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"Invalid argument '{name}'.");

    private static uint ParseUInt32(string value, string name) =>
        uint.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid argument '{name}'.");

    private static bool IsSha256(string value)
    {
        if (value.Length != 64)
            return false;
        return value.All(character =>
            character is >= '0' and <= '9' ||
            character is >= 'a' and <= 'f' ||
            character is >= 'A' and <= 'F');
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A later retry reports any persistent filesystem problem.
        }
    }

    private static void Log(string message)
    {
        var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
        try
        {
            if (!string.IsNullOrWhiteSpace(_logPath))
            {
                var directory = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.AppendAllText(_logPath, line);
            }
        }
        catch
        {
            // Logging must never prevent or change the update result.
        }
    }
}
