using System.IO;
using MelonLoader;
using MelonLoader.Utils;

namespace CartItemStacker;

internal static class ModSettings
{
    internal const bool DefaultEnabled = true;
    internal const int DefaultEquipmentStackMaxUnits =
        CapacityLimits.MaximumEquipmentUnits;
    internal const int DefaultCableSpoolsPerStack = 4;
    internal const float DefaultAnimationSpeed = 1.0f;
    internal const float MinimumAnimationSpeed = 0.5f;
    internal const float MaximumAnimationSpeed = 2.0f;
    internal const bool DefaultRestackCargoIndicator = true;
    internal const bool DefaultDebugLogging = false;

    private const string CategoryIdentifier = "CartItemStacker";
    private const string PreferencesFileName = "CartItemStacker.cfg";

    private static MelonPreferences_Category _category;
    private static MelonPreferences_Entry<bool> _enabled;
    private static MelonPreferences_Entry<int> _equipmentStackMaxUnits;
    private static MelonPreferences_Entry<int> _cableSpoolsPerStack;
    private static MelonPreferences_Entry<float> _animationSpeed;
    private static MelonPreferences_Entry<bool> _restackCargoIndicator;
    private static MelonPreferences_Entry<bool> _debugLogging;

    internal static bool RequestedEnabled =>
        _enabled?.Value ?? DefaultEnabled;

    internal static int EquipmentStackMaxUnits =>
        _equipmentStackMaxUnits?.Value ?? DefaultEquipmentStackMaxUnits;

    internal static int CableSpoolsPerStack =>
        _cableSpoolsPerStack?.Value ?? DefaultCableSpoolsPerStack;

    internal static float AnimationSpeed =>
        _animationSpeed?.Value ?? DefaultAnimationSpeed;

    internal static bool RestackCargoIndicator =>
        _restackCargoIndicator?.Value ?? DefaultRestackCargoIndicator;

    internal static bool DebugLogging =>
        _debugLogging?.Value ?? DefaultDebugLogging;

    internal static void Initialize()
    {
        _category = MelonPreferences.CreateCategory(
            CategoryIdentifier,
            "Cart Item Stacker");
        _category.SetFilePath(
            Path.Combine(MelonEnvironment.UserDataDirectory, PreferencesFileName),
            autoload: false,
            printmsg: false);

        _enabled = _category.CreateEntry(
            "Enabled",
            DefaultEnabled,
            "Enabled",
            "Requested mod state. A state change is applied only when the trolley is empty.");
        _equipmentStackMaxUnits = _category.CreateEntry(
            "EquipmentStackMaxUnits",
            DefaultEquipmentStackMaxUnits,
            "Equipment stack maximum units",
            "Maximum accepted height per equipment stack, from 24U through 42U.");
        _cableSpoolsPerStack = _category.CreateEntry(
            "CableSpoolsPerStack",
            DefaultCableSpoolsPerStack,
            "Cable spools per stack",
            "Maximum accepted cable spools per stack, from 1 through 8.");
        _animationSpeed = _category.CreateEntry(
            "AnimationSpeed",
            DefaultAnimationSpeed,
            "Animation speed",
            "Movement animation speed multiplier, from 0.5 through 2.0.");
        _restackCargoIndicator = _category.CreateEntry(
            "RestackCargoIndicator",
            DefaultRestackCargoIndicator,
            "Restack cargo indicator",
            "Pulse cargo orange and charcoal only during save-load restacking, then show ready green for one second.");
        _debugLogging = _category.CreateEntry(
            "DebugLogging",
            DefaultDebugLogging,
            "Debug logging",
            "Write detailed placement diagnostics to the MelonLoader log.");

        _category.LoadFromFile(printmsg: false);

        var corrected = false;
        corrected |= Normalize(
            _equipmentStackMaxUnits,
            CapacityLimits.MinimumEquipmentUnits,
            CapacityLimits.MaximumEquipmentUnits,
            DefaultEquipmentStackMaxUnits);
        corrected |= Normalize(
            _cableSpoolsPerStack,
            CapacityLimits.MinimumCableSpools,
            CapacityLimits.MaximumCableSpools,
            DefaultCableSpoolsPerStack);
        corrected |= Normalize(
            _animationSpeed,
            MinimumAnimationSpeed,
            MaximumAnimationSpeed,
            DefaultAnimationSpeed);

        _category.SaveToFile(printmsg: false);
        if (corrected)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                "One or more invalid settings were restored to their default values.");
        }
    }

    internal static float GetAnimationDuration(float baseDuration) =>
        baseDuration / AnimationSpeed;

    internal static void Debug(string message)
    {
        if (DebugLogging)
            Melon<CartItemStacker.Mod>.Logger.Msg($"[Debug] {message}");
    }

    private static bool Normalize(
        MelonPreferences_Entry<int> entry,
        int minimum,
        int maximum,
        int defaultValue)
    {
        if (entry.Value >= minimum && entry.Value <= maximum)
            return false;

        Melon<CartItemStacker.Mod>.Logger.Warning(
            $"Setting '{entry.Identifier}' has invalid value {entry.Value}; " +
            $"using default value {defaultValue}.");
        entry.Value = defaultValue;
        return true;
    }

    private static bool Normalize(
        MelonPreferences_Entry<float> entry,
        float minimum,
        float maximum,
        float defaultValue)
    {
        if (!float.IsNaN(entry.Value) &&
            !float.IsInfinity(entry.Value) &&
            entry.Value >= minimum &&
            entry.Value <= maximum)
            return false;

        Melon<CartItemStacker.Mod>.Logger.Warning(
            $"Setting '{entry.Identifier}' has invalid value {entry.Value}; " +
            $"using default value {defaultValue}.");
        entry.Value = defaultValue;
        return true;
    }
}
