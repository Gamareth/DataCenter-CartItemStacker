namespace CartItemStacker;

internal static class WorkshopUpdateRules
{
    internal static bool ShouldStageUpdate(
        Version currentVersion,
        Version candidateVersion,
        uint candidateTimestamp,
        uint appliedTimestamp,
        string currentHash,
        string candidateHash)
    {
        if (currentVersion is null || candidateVersion is null)
            return false;
        if (!IsSha256(currentHash) || !IsSha256(candidateHash))
            return false;
        if (string.Equals(
                currentHash,
                candidateHash,
                StringComparison.OrdinalIgnoreCase))
            return false;

        var versionComparison = candidateVersion.CompareTo(currentVersion);
        if (versionComparison < 0)
            return false;
        if (versionComparison > 0)
            return true;

        // A compatibility rebuild may deliberately retain the public mod
        // version. Steam's item timestamp distinguishes that newer payload.
        return candidateTimestamp > appliedTimestamp;
    }

    internal static bool IsSha256(string value)
    {
        if (value is null || value.Length != 64)
            return false;

        foreach (var character in value)
        {
            var hexadecimal =
                character is >= '0' and <= '9' ||
                character is >= 'a' and <= 'f' ||
                character is >= 'A' and <= 'F';
            if (!hexadecimal)
                return false;
        }

        return true;
    }
}
