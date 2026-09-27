using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped where the read-only bit is not enforced.
/// Tests that force a write failure by marking a file read-only would falsely fail under
/// root on Unix, which can write read-only files. Windows enforces the bit even for
/// administrators, and CI runs as an unprivileged user, so the tests run there.
/// </summary>
internal sealed class FactWhenFilePermissionsEnforcedAttribute : FactAttribute
{
    public FactWhenFilePermissionsEnforcedAttribute()
    {
        if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
            Skip = "Running as root: the read-only bit is not enforced, so the write cannot be made to fail.";
    }
}
