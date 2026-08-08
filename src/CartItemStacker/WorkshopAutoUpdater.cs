using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Il2Cpp;
using Il2CppSteamworks;
using MelonLoader;
using MelonLoader.Utils;

namespace CartItemStacker;

internal static class WorkshopAutoUpdater
{
    internal const ulong WorkshopItemId = 3775738163UL;
    internal const string UpdaterFileName = "CartItemStacker.Updater.dll";

    private const string ModAssemblyName = "CartItemStacker";
    private const string ModFileName = "CartItemStacker.dll";
    private const string LegacyUpdaterFileName = "CartItemStacker.Updater.exe";
    private const string UpdaterDependenciesFileName =
        "CartItemStacker.Updater.deps.json";
    private const string UpdaterRuntimeConfigFileName =
        "CartItemStacker.Updater.runtimeconfig.json";
    private const string UpdaterDirectoryName = "CartItemStackerUpdater";
    private const string SuccessMarkerFileName = "update.success";
    private const string StateFileName = "CartItemStackerWorkshop.state";
    private const string LogFileName = "CartItemStackerUpdater.log";
    private const uint WorkshopStateSubscribed = 1U;
    private const uint WorkshopStateInstalled = 1U << 2;
    private const uint WorkshopStateNeedsUpdate = 1U << 3;
    private const uint WorkshopStateDownloading = 1U << 4;
    private const uint WorkshopStateDownloadPending = 1U << 5;
    private const uint WorkshopStateNotReady =
        WorkshopStateNeedsUpdate |
        WorkshopStateDownloading |
        WorkshopStateDownloadPending;
    private static int _checkStarted;
    private static bool _updateScheduled;

    internal static void CheckAfterInitialization()
    {
        if (!ModSettings.AutoUpdateFromWorkshop)
            return;
        if (!SteamManager.Initialized)
        {
            ModSettings.Debug(
                "Workshop auto-update is waiting for Steam initialization.");
            return;
        }
        if (Interlocked.Exchange(ref _checkStarted, 1) != 0)
            return;

        try
        {
            CheckAndSchedule();
        }
        catch (Exception exception)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                "Workshop update check failed without affecting the running " +
                $"mod. {exception.GetType().Name}: {exception.Message}");
        }
    }

    internal static void OnApplicationQuit()
    {
        if (_updateScheduled)
        {
            Melon<CartItemStacker.Mod>.Logger.Msg(
                "Data Center is closing; the verified Workshop update will " +
                "now be installed for the next launch.");
        }
    }

    internal static void CleanupAfterSuccessfulUpdate()
    {
        var updaterDirectory = Path.Combine(
            MelonEnvironment.UserDataDirectory,
            UpdaterDirectoryName);
        var successMarkerPath = Path.Combine(
            updaterDirectory,
            SuccessMarkerFileName);
        if (!File.Exists(successMarkerPath))
            return;

        try
        {
            DeleteIfPresent(Path.Combine(updaterDirectory, UpdaterFileName));
            DeleteIfPresent(Path.Combine(
                updaterDirectory,
                UpdaterDependenciesFileName));
            DeleteIfPresent(Path.Combine(
                updaterDirectory,
                UpdaterRuntimeConfigFileName));
            DeleteIfPresent(Path.Combine(
                updaterDirectory,
                LegacyUpdaterFileName));
            File.Delete(successMarkerPath);
            if (Directory.Exists(updaterDirectory) &&
                !Directory.EnumerateFileSystemEntries(updaterDirectory).Any())
                Directory.Delete(updaterDirectory);

            Melon<CartItemStacker.Mod>.Logger.Msg(
                "The automatic Workshop update completed successfully; " +
                "temporary updater files were removed.");
        }
        catch (Exception exception)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                "The Workshop update succeeded, but its temporary updater " +
                $"files could not yet be removed: {exception.Message}");
        }
    }

    private static void CheckAndSchedule()
    {
        var workshopId = new PublishedFileId_t(WorkshopItemId);
        var workshopState = SteamUGC.GetItemState(workshopId);
        if ((workshopState & WorkshopStateSubscribed) == 0)
        {
            Melon<CartItemStacker.Mod>.Logger.Msg(
                "Workshop auto-update check complete: Cart Item Stacker " +
                "Workshop item 3775738163 is not subscribed.");
            return;
        }
        if ((workshopState & WorkshopStateInstalled) == 0 ||
            (workshopState & WorkshopStateNotReady) != 0)
        {
            Interlocked.Exchange(ref _checkStarted, 0);
            ModSettings.Debug(
                "Workshop auto-update is waiting for Steam to finish " +
                "installing the subscribed item.");
            return;
        }

        if (!SteamUGC.GetItemInstallInfo(
                workshopId,
                out _,
                out var workshopFolder,
                4096,
                out var workshopTimestamp) ||
            string.IsNullOrWhiteSpace(workshopFolder))
        {
            Melon<CartItemStacker.Mod>.Logger.Msg(
                "Workshop auto-update check complete: no subscribed " +
                "Cart Item Stacker Workshop installation was found.");
            return;
        }

        var candidatePath = Path.Combine(workshopFolder, ModFileName);
        if (!File.Exists(candidatePath))
        {
            ModSettings.Debug(
                $"Workshop item does not contain '{ModFileName}'.");
            return;
        }

        var destinationPath = Path.Combine(
            MelonEnvironment.ModsDirectory,
            ModFileName);
        if (!File.Exists(destinationPath))
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                $"Workshop update destination is missing: '{destinationPath}'.");
            return;
        }

        var currentAssembly = AssemblyName.GetAssemblyName(destinationPath);
        var candidateAssembly = AssemblyName.GetAssemblyName(candidatePath);
        if (!string.Equals(
                candidateAssembly.Name,
                ModAssemblyName,
                StringComparison.Ordinal) ||
            !string.Equals(
                currentAssembly.Name,
                ModAssemblyName,
                StringComparison.Ordinal) ||
            currentAssembly.Version is null ||
            candidateAssembly.Version is null)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                "Workshop update candidate failed its assembly identity check.");
            return;
        }

        var currentHash = ComputeSha256(destinationPath);
        var candidateHash = ComputeSha256(candidatePath);
        var statePath = Path.Combine(
            MelonEnvironment.UserDataDirectory,
            StateFileName);
        var appliedTimestamp = ReadAppliedTimestamp(statePath);

        if (string.Equals(
                currentHash,
                candidateHash,
                StringComparison.OrdinalIgnoreCase))
        {
            WriteAppliedTimestamp(statePath, workshopTimestamp);
            LogUpToDate(currentAssembly.Version, candidateAssembly.Version);
            return;
        }

        if (!WorkshopUpdateRules.ShouldStageUpdate(
                currentAssembly.Version,
                candidateAssembly.Version,
                workshopTimestamp,
                appliedTimestamp,
                currentHash,
                candidateHash))
        {
            LogUpToDate(currentAssembly.Version, candidateAssembly.Version);
            return;
        }

        var updaterPath = ExtractEmbeddedUpdater();
        if (updaterPath is null)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                "A newer Workshop DLL was found, but the embedded updater " +
                "could not be prepared. Install this update manually.");
            return;
        }

        var logPath = Path.Combine(
            MelonEnvironment.UserDataDirectory,
            LogFileName);
        var successMarkerPath = Path.Combine(
            Path.GetDirectoryName(updaterPath) ?? string.Empty,
            SuccessMarkerFileName);
        using var process = StartUpdater(
            updaterPath,
            candidatePath,
            destinationPath,
            candidateHash,
            candidateAssembly.Version,
            workshopTimestamp,
            statePath,
            logPath,
            successMarkerPath);
        if (process is null)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                "Could not start the Workshop updater process. Install the " +
                "new Workshop DLL manually.");
            return;
        }

        _updateScheduled = true;
        Melon<CartItemStacker.Mod>.Logger.Msg(
            $"Cart Item Stacker update {candidateAssembly.Version} is " +
            "available and verified. Automatic installation will begin " +
            "after Data Center closes and will be active on the next launch.");
    }

    private static void LogUpToDate(
        System.Version installedVersion,
        System.Version workshopVersion)
    {
        var versionDetails = installedVersion == workshopVersion
            ? $"version {installedVersion}"
            : $"installed {installedVersion}; Workshop {workshopVersion}";
        Melon<CartItemStacker.Mod>.Logger.Msg(
            $"Cart Item Stacker is up to date ({versionDetails}).");
    }

    private static Process StartUpdater(
        string updaterPath,
        string sourcePath,
        string destinationPath,
        string expectedHash,
        System.Version expectedVersion,
        uint workshopTimestamp,
        string statePath,
        string logPath,
        string successMarkerPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FindDotNetHost(),
            WorkingDirectory = Path.GetDirectoryName(updaterPath) ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add(updaterPath);
        AddArgument(startInfo, "--pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--source", sourcePath);
        AddArgument(startInfo, "--destination", destinationPath);
        AddArgument(startInfo, "--sha256", expectedHash);
        AddArgument(startInfo, "--version", expectedVersion.ToString());
        AddArgument(startInfo, "--timestamp", workshopTimestamp.ToString(CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--state", statePath);
        AddArgument(startInfo, "--log", logPath);
        AddArgument(startInfo, "--success-marker", successMarkerPath);
        return Process.Start(startInfo);
    }

    private static void AddArgument(
        ProcessStartInfo startInfo,
        string name,
        string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static string ExtractEmbeddedUpdater()
    {
        try
        {
            var updaterDirectory = Path.Combine(
                MelonEnvironment.UserDataDirectory,
                UpdaterDirectoryName);
            Directory.CreateDirectory(updaterDirectory);
            var updaterPath = Path.Combine(
                updaterDirectory,
                UpdaterFileName);
            ExtractEmbeddedFile(UpdaterFileName, updaterPath);
            ExtractEmbeddedFile(
                UpdaterDependenciesFileName,
                Path.Combine(updaterDirectory, UpdaterDependenciesFileName));
            ExtractEmbeddedFile(
                UpdaterRuntimeConfigFileName,
                Path.Combine(updaterDirectory, UpdaterRuntimeConfigFileName));

            return updaterPath;
        }
        catch (Exception exception)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                $"Could not extract the embedded updater: {exception.Message}");
            return null;
        }
    }

    private static void ExtractEmbeddedFile(
        string resourceName,
        string destinationPath)
    {
        using var resource = typeof(WorkshopAutoUpdater).Assembly
            .GetManifestResourceStream(resourceName) ??
            throw new InvalidDataException(
                $"Embedded updater resource '{resourceName}' is missing.");
        var temporaryPath = destinationPath + ".pending";

        using (var output = File.Create(temporaryPath))
            resource.CopyTo(output);

        var embeddedHash = ComputeSha256(temporaryPath);
        if (File.Exists(destinationPath) &&
            string.Equals(
                ComputeSha256(destinationPath),
                embeddedHash,
                StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temporaryPath);
            return;
        }

        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private static string FindDotNetHost()
    {
        var executableName = OperatingSystem.IsWindows()
            ? "dotnet.exe"
            : "dotnet";
        var explicitHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(explicitHost) && File.Exists(explicitHost))
            return explicitHost;

        foreach (var variableName in new[]
                 {
                     "DOTNET_ROOT",
                     "DOTNET_ROOT_X64",
                     "DOTNET_ROOT_ARM64",
                 })
        {
            var root = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(root))
                continue;
            var candidate = Path.Combine(root, executableName);
            if (File.Exists(candidate))
                return candidate;
        }

        try
        {
            var runtimeDirectory = new DirectoryInfo(
                RuntimeEnvironment.GetRuntimeDirectory());
            var dotNetRoot = runtimeDirectory.Parent?.Parent?.Parent;
            if (dotNetRoot is not null)
            {
                var candidate = Path.Combine(dotNetRoot.FullName, executableName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch
        {
            // Fall back to PATH. Process.Start reports a useful warning if the
            // host is unavailable.
        }

        return executableName;
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
        DeletePendingIfPresent(path);
    }

    private static void DeletePendingIfPresent(string path)
    {
        var pendingPath = path + ".pending";
        if (File.Exists(pendingPath))
            File.Delete(pendingPath);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var algorithm = SHA256.Create();
        return Convert.ToHexString(algorithm.ComputeHash(stream));
    }

    private static uint ReadAppliedTimestamp(string path)
    {
        try
        {
            return File.Exists(path) &&
                   uint.TryParse(
                       File.ReadAllText(path).Trim(),
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out var timestamp)
                ? timestamp
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void WriteAppliedTimestamp(string path, uint timestamp)
    {
        try
        {
            var newestTimestamp = Math.Max(ReadAppliedTimestamp(path), timestamp);
            File.WriteAllText(
                path,
                newestTimestamp.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception exception)
        {
            ModSettings.Debug(
                $"Could not record Workshop baseline: {exception.Message}");
        }
    }
}
