using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;

namespace LayoutSync.Services;

/// <summary>
/// The encoder for the JSON files LayoutSync writes to disk (issue #38): the same as
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>, except that characters outside the
/// Basic Multilingual Plane, such as emoji, are also written as-is.
/// </summary>
/// <remarks>
/// <para>
/// The default encoder escapes every non-ASCII character and the HTML-sensitive ones
/// (<c>' &lt; &gt; &amp; +</c>), so every tool write re-escaped lines it never changed. The relaxed
/// encoder writes those characters literally. It is "unsafe" only for output embedded in an HTML
/// page or a <c>&lt;script&gt;</c> element; these files are only read back by JSON parsers.
/// </para>
/// <para>
/// On its own, the relaxed encoder still escapes supplementary-plane characters as a surrogate pair,
/// because the built-in encoders only allow U+0000 to U+FFFF. This class changes that one decision
/// and delegates everything else, so quotes, backslashes and control characters are escaped exactly
/// as the relaxed encoder escapes them, as are invisible separators such as U+00A0 and U+2028.
/// </para>
/// </remarks>
internal sealed class LiteralJsonEncoder : JavaScriptEncoder
{
    // Declared before Instance: static fields initialize in textual order, so the delegate
    // is set before the singleton is constructed.
    private static readonly JavaScriptEncoder Relaxed = UnsafeRelaxedJsonEscaping;

    public static readonly LiteralJsonEncoder Instance = new();

    private LiteralJsonEncoder()
    {
    }

    public override int MaxOutputCharactersPerInputCharacter => Relaxed.MaxOutputCharactersPerInputCharacter;

    public override bool WillEncode(int unicodeScalar)
        => !IsSupplementaryPlane(unicodeScalar) && Relaxed.WillEncode(unicodeScalar);

    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        ReadOnlySpan<char> remaining = new(text, textLength);
        while (!remaining.IsEmpty)
        {
            // Ill-formed UTF-16 (a lone surrogate) is reported too, so the base class substitutes
            // U+FFFD for it instead of emitting invalid UTF-8. The relaxed encoder allows U+FFFD,
            // so it is written literally (the built-in encoders escape it).
            if (Rune.DecodeFromUtf16(remaining, out Rune rune, out int charsConsumed) != OperationStatus.Done
                || WillEncode(rune.Value))
            {
                return textLength - remaining.Length;
            }

            remaining = remaining[charsConsumed..];
        }

        return -1;
    }

    public override unsafe bool TryEncodeUnicodeScalar(
        int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
        => Relaxed.TryEncodeUnicodeScalar(unicodeScalar, buffer, bufferLength, out numberOfCharactersWritten);

    private static bool IsSupplementaryPlane(int unicodeScalar) => unicodeScalar is >= 0x10000 and <= 0x10FFFF;
}
