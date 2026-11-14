using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using NBitcoin.Secp256k1;

namespace LightningEnable.Mcp.Services;

/// <summary>The reviewer key could not be loaded or created. Never contains key material.</summary>
public sealed class AttestationKeyException(string message) : Exception(message);

/// <summary>Public half of the reviewer key, plus where it came from.</summary>
public sealed record ReviewerIdentity(string PubkeyHex, string Npub, string Source, string? KeyFile, bool CreatedNow)
{
    public object ToResult() => new { pubkey = PubkeyHex, npub = Npub, source = Source, keyFile = KeyFile, createdNow = CreatedNow };
}

/// <summary>
/// The reviewer key that signs this installation's attestations: a NEW, dedicated secp256k1
/// key per agent installation. It is not the NWC wallet key, not any payment key, and not
/// derived from one, so publishing reviews never exposes or links a key that can move money.
///
/// Where it comes from, in order:
/// <list type="number">
/// <item><c>LIGHTNING_ENABLE_ATTESTATION_KEY</c> (64 hex characters or <c>nsec1...</c>), for
///   hosts that manage secrets themselves. Set but invalid is an error, never a silent
///   fallback to another key.</item>
/// <item><c>~/.lightning-enable/attestation-key</c> (64 hex characters), generated on first use
///   and created owner-only AT CREATION: mode 0600 on POSIX; on Windows a protected DACL (no
///   inherited entries) granting only the current user. It is written under a temporary name
///   and moved into place without replacing, so two processes starting at once agree on one key.</item>
/// </list>
/// The secret is never logged and never returned; only the public key leaves this class.
/// Rotation: a new key is a new reviewer identity. Mirrors the Python <c>attestation_key</c>.
/// </summary>
public sealed class AttestationKeyStore
{
    public const string EnvVar = "LIGHTNING_ENABLE_ATTESTATION_KEY";
    public const string KeyFileName = "attestation-key";

    public const string RotationNote =
        "Rotation: a new key is a new reviewer identity. Earlier reviews stay attributed to "
        + "the old pubkey; nothing links the two unless you publish that link yourself.";

    private readonly string _dir;
    private readonly Func<string, string?> _env;
    private readonly object _lock = new();
    private (ECPrivKey Key, ReviewerIdentity Identity)? _cached;

    public AttestationKeyStore(IBudgetConfigurationService? configService = null)
        : this(ResolveDir(configService), Environment.GetEnvironmentVariable)
    {
    }

    internal AttestationKeyStore(string configDir, Func<string, string?> env)
    {
        _dir = configDir;
        _env = env;
    }

    public string KeyPath => Path.Combine(_dir, KeyFileName);

    public ReviewerIdentity GetIdentity() => Load().Identity;

    /// <summary>The signing key. Callers must never log or return it.</summary>
    internal ECPrivKey GetSigningKey() => Load().Key;

    private static string ResolveDir(IBudgetConfigurationService? cs)
    {
        try
        {
            var p = cs?.ConfigFilePath;
            if (!string.IsNullOrWhiteSpace(p)) return Path.GetDirectoryName(p)!;
        }
        catch
        {
            // fall through to the default
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lightning-enable");
    }

    private (ECPrivKey Key, ReviewerIdentity Identity) Load()
    {
        lock (_lock)
        {
            if (_cached is { } c) return c;

            var fromEnv = (_env(EnvVar) ?? string.Empty).Trim();
            if (fromEnv.Length > 0 && !fromEnv.StartsWith("${", StringComparison.Ordinal))
            {
                if (!NostrEvents.TryParseSecretKey(fromEnv, out var envKey) || envKey == null)
                    throw new AttestationKeyException(
                        $"{EnvVar} is set but is not a valid key (expected 64 hex characters or nsec1...). "
                        + "Fix or unset it; no other key is used in its place.");
                _cached = (envKey, Identity(envKey, "env", null, false));
                return _cached.Value;
            }

            var path = KeyPath;
            var created = false;
            if (!File.Exists(path)) created = Create(path);
            var key = Read(path);
            _cached = (key, Identity(key, "file", path, created));
            if (created)
                Console.Error.WriteLine($"[Lightning Enable] Created a new attestation reviewer key at {path}");
            return _cached.Value;
        }
    }

    private static ReviewerIdentity Identity(ECPrivKey key, string source, string? path, bool created)
    {
        var pub = NostrEvents.XOnlyPubkeyHex(key);
        return new ReviewerIdentity(pub, NostrEvents.NpubEncode(pub), source, path, created);
    }

    private static ECPrivKey Read(string path)
    {
        if (OwnerOnlyProblem(path) is { } problem)
            throw new AttestationKeyException(problem);
        try
        {
            var text = File.ReadAllText(path).Trim();
            if (NostrEvents.TryParseSecretKey(text, out var key) && key != null) return key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // reported below without the contents
        }
        throw new AttestationKeyException(
            $"The attestation key file {path} is unreadable or not a valid key. It is left "
            + "untouched; move it aside to create a new reviewer identity.");
    }

    /// <summary>
    /// Null when <paramref name="path"/> is readable by the current user only — the rule the file
    /// is created with — else a message saying what is wrong and how to fix it. POSIX: no group or
    /// other permission bits. Windows: every ALLOW entry (explicit or inherited) names the current
    /// user's SID — no SYSTEM, Administrators, Users or Everyone.
    /// </summary>
    internal static string? OwnerOnlyProblem(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                var mode = File.GetUnixFileMode(path);
                return (mode & others) == 0 ? null
                    : $"The attestation key file {path} is readable by other accounts (mode {Convert.ToString((int)mode, 8)}). "
                      + $"Restrict it with:  chmod 600 '{path}'  — or move it aside to create a new reviewer identity.";
            }
            var otherSids = OtherTrustees(path);
            return otherSids.Count == 0 ? null
                : $"The attestation key file {path} grants access to other accounts ({string.Join(", ", otherSids)}). "
                  + $"Restrict it to your user with:  icacls \"{path}\" /inheritance:r /grant:r \"%USERNAME%:F\"  "
                  + $"and remove any other entry that icacls \"{path}\" still lists — or move it aside to create a new reviewer identity.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Cannot check the permissions of the attestation key file {path}: {ex.Message}";
        }
    }

    [SupportedOSPlatform("windows")]
    internal static List<string> OtherTrustees(string path)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        return new FileInfo(path).GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow && !me.Equals(r.IdentityReference))
            .Select(r => r.IdentityReference.Value)
            .Distinct()
            .ToList();
    }

    /// <summary>Create the key file owner-only. False if another process won the race.</summary>
    internal bool Create(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var tmp = Path.Combine(dir, $"{KeyFileName}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}.tmp");
        try
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(dir);
            else
                Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var secret = GenerateSecretHex();
            using (var fs = CreateOwnerOnlyFile(tmp))
            using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                w.Write(secret + "\n");
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(tmp, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AttestationKeyException($"Cannot create the attestation key file {path}: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { /* best effort */ }
        }
    }

    private static string GenerateSecretHex()
    {
        while (true)
        {
            var hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            if (NostrEvents.TryParseSecretKey(hex, out _)) return hex;
        }
    }

    /// <summary>
    /// Create a NEW file readable and writable only by the current user. POSIX: mode 0600 set
    /// by the create call itself. Windows: the file is created with a security descriptor whose
    /// DACL is protected (inheritance blocked) and holds one entry, full control for the current
    /// user's SID, and whose owner is that user. It never exists with broader access.
    /// </summary>
    internal static FileStream CreateOwnerOnlyFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return CreateOwnerOnlyFileWindows(path);
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateOwnerOnlyFileWindows(string path)
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new AttestationKeyException("Cannot determine the current Windows user.");
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(
            FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadData, FileShare.None, 4096,
            FileOptions.None, security);
    }
}
