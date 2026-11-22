using System.Security.AccessControl;
using System.Security.Principal;

namespace TetherBridge.Desktop.FullTunnel;

// Conservative ACL gate for binaries that may be launched elevated.
public static class TunnelPathTrust
{
    private static readonly HashSet<string> TrustedWriters = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18", // LocalSystem
        "S-1-5-32-544", // Administrators
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" // TrustedInstaller
    };
    private const FileSystemRights Dangerous = FileSystemRights.Write | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    public static bool IsDangerousAccessMask(uint mask) => (mask & (unchecked((uint)Dangerous) | 0x50000000u)) != 0;

    public static bool IsTrustedAcl(SecurityIdentifier owner, IEnumerable<FileSystemAccessRule> rules, bool includeInheritOnly = false)
    {
        if (!TrustedWriters.Contains(owner.Value)) return false;
        foreach (var rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow || (!includeInheritOnly && (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)) continue;
            if (IsDangerousAccessMask(unchecked((uint)rule.FileSystemRights)) && !TrustedWriters.Contains(rule.IdentityReference.Value)) return false;
        }
        return true;
    }
    public static void Verify(string directory, params string[] files)
    {
        var info = new DirectoryInfo(directory);
        for (var i = 0; i < 3; i++)
        {
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Untrusted tunnel directory path.");
            var security = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            Check(security, includeInheritOnly: i == 0);
            info = info.Parent ?? throw new IOException("Invalid tunnel directory hierarchy.");
        }
        var expectedDll = files.Where(path => Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var sibling in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
            if (!expectedDll.Contains(Path.GetFullPath(sibling))) throw new IOException("Unexpected DLL in tunnel installation directory.");
        foreach (var path in files)
        {
            var file = new FileInfo(path);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Untrusted tunnel dependency path.");
            Check(file.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), includeInheritOnly: false);
        }
    }
    private static void Check(FileSystemSecurity security, bool includeInheritOnly)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier ?? throw new IOException("Tunnel owner is unavailable.");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        if (!IsTrustedAcl(owner, rules, includeInheritOnly)) throw new IOException("Tunnel files are writable or owned by an untrusted principal.");
    }
}
