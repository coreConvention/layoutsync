using System.Text.RegularExpressions;
using NanoidDotNet;

namespace coreConvention.Core.Validation;

/// <summary>
/// Validates and generates NanoIDs for document identification.
/// NanoIDs are URL-safe unique identifiers that are more compact than UUIDs.
///
/// This validator enforces the rule that document `id` fields must use NanoIDs,
/// not human-readable strings. Human-readable naming should use the `identifier` field.
/// </summary>
public static partial class NanoIdValidator
{
    /// <summary>
    /// NanoID character set: A-Z, a-z, 0-9, underscore, and hyphen.
    /// Standard NanoIDs are 21+ characters.
    /// </summary>
    private static readonly Regex NanoIdPattern = NanoIdRegex();

    /// <summary>
    /// Default alphabet used by Nanoid library.
    /// </summary>
    private const string DefaultAlphabet = "_-0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>
    /// Default NanoID size (21 characters provides ~126 bits of entropy).
    /// We use 23 for extra uniqueness.
    /// </summary>
    private const int DefaultSize = 23;

    /// <summary>
    /// Validates whether the given string is a valid NanoID.
    /// Valid NanoIDs are 21+ characters using alphanumeric chars plus _ and -.
    /// </summary>
    /// <param name="id">The ID to validate.</param>
    /// <returns>True if the ID is a valid NanoID format.</returns>
    public static bool IsValidNanoId(string? id)
    {
        return !string.IsNullOrEmpty(id) && NanoIdPattern.IsMatch(id);
    }

    /// <summary>
    /// Determines if the given ID appears to be human-readable rather than a NanoID.
    /// Human-readable IDs typically fail NanoID validation (contain invalid chars,
    /// wrong length, or semantic patterns like words separated by hyphens).
    /// </summary>
    /// <param name="id">The ID to check.</param>
    /// <returns>True if the ID appears to be human-readable (not a valid NanoID).</returns>
    public static bool IsHumanReadable(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        // If it's a valid NanoID, it's not human-readable
        if (IsValidNanoId(id))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Generates a new NanoID with the default size (23 characters).
    /// </summary>
    /// <returns>A new unique NanoID.</returns>
    public static string GenerateNanoId()
    {
        return Nanoid.Generate(alphabet: DefaultAlphabet, size: DefaultSize);
    }

    /// <summary>
    /// Generates a new NanoID with a custom size.
    /// </summary>
    /// <param name="size">The number of characters in the NanoID.</param>
    /// <returns>A new unique NanoID.</returns>
    public static string GenerateNanoId(int size)
    {
        return Nanoid.Generate(alphabet: DefaultAlphabet, size: size);
    }


    [GeneratedRegex(@"^[A-Za-z0-9_-]{21,}$", RegexOptions.Compiled)]
    private static partial Regex NanoIdRegex();
}
