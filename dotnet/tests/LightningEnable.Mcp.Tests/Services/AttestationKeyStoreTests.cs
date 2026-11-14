using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using LightningEnable.Mcp.Services;
using Xunit;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// Reviewer key custody: generated on first use, owner-only at creation, never exposed.
/// Custody is asserted on the file's ACL (Windows) or mode (POSIX), not on readability — this
/// host may run elevated, where being able to read the file proves nothing.
/// </summary>
public sealed class AttestationKeyStoreTests : IDisposable
{
    // BIP-340 test scalar 3 — a published test value, not a credential.
    private const string TestKeyHex = "0000000000000000000000000000000000000000000000000000000000000003";
    private const string TestNsec = "nsec1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqps52s3re";
    private const string TestPubkey = "f9308a019258c31049344f85f89d5229b531c845836f99b08601f113bce036f9";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "le-attest-key-" + Guid.NewGuid().ToString("N"));
    private string Dir => Path.Combine(_root, ".lightning-enable");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private AttestationKeyStore Store(Dictionary<string, string>? env = null) =>
        new(Dir, name => env != null && env.TryGetValue(name, out var v) ? v : null);

    [Fact]
    public void FirstUse_CreatesKey_SecondLoadReusesIt()
    {
        var first = Store().GetIdentity();
        first.CreatedNow.Should().BeTrue();
        first.Source.Should().Be("file");
        NostrEvents.IsHex(first.PubkeyHex, 64).Should().BeTrue();
        first.Npub.Should().StartWith("npub1");

        var again = Store().GetIdentity();
        again.PubkeyHex.Should().Be(first.PubkeyHex);
        again.CreatedNow.Should().BeFalse();

        File.ReadAllText(Path.Combine(Dir, AttestationKeyStore.KeyFileName)).Should().MatchRegex("^[0-9a-f]{64}\n$");
        Directory.GetFiles(Dir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Identity_NeverCarriesTheSecret()
    {
        var store = Store();
        var identity = store.GetIdentity();
        var secretHex = File.ReadAllText(store.KeyPath).Trim();
        JsonSerializer.Serialize(identity.ToResult()).Should().NotContain(secretHex);
        identity.ToString().Should().NotContain(secretHex);
    }

    [Fact]
    public void KeyFile_IsOwnerOnly()
    {
        var store = Store();
        store.GetIdentity();
        if (OperatingSystem.IsWindows())
            AssertOwnerOnlyDacl(store.KeyPath);
        else
            File.GetUnixFileMode(store.KeyPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void OwnerOnlyFile_IsProtectedFromTheMomentOfCreation()
    {
        Directory.CreateDirectory(_root);
        var probe = Path.Combine(_root, "probe");
        using (AttestationKeyStore.CreateOwnerOnlyFile(probe))
        {
            // Still open, nothing written: the access control is already the final one.
            if (OperatingSystem.IsWindows())
                AssertOwnerOnlyDacl(probe);
            else
                File.GetUnixFileMode(probe).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void CreateOwnerOnlyFile_RefusesToOverwrite()
    {
        Directory.CreateDirectory(_root);
        var existing = Path.Combine(_root, "exists");
        File.WriteAllText(existing, "x");
        var act = () => AttestationKeyStore.CreateOwnerOnlyFile(existing).Dispose();
        act.Should().Throw<IOException>();
        File.ReadAllText(existing).Should().Be("x");
    }

    [Theory]
    [InlineData(TestKeyHex)]
    [InlineData(TestNsec)]
    public void EnvVarKey_IsUsed_AndNoFileWritten(string value)
    {
        var store = Store(new() { [AttestationKeyStore.EnvVar] = value });
        var identity = store.GetIdentity();
        identity.PubkeyHex.Should().Be(TestPubkey);
        identity.Source.Should().Be("env");
        identity.KeyFile.Should().BeNull();
        File.Exists(store.KeyPath).Should().BeFalse();
    }

    [Fact]
    public void InvalidEnvVar_IsAnError_WithoutEchoingIt()
    {
        var bogus = "not-a-key-" + new string('9', 40);
        var store = Store(new() { [AttestationKeyStore.EnvVar] = bogus });
        var act = () => store.GetIdentity();
        act.Should().Throw<AttestationKeyException>().Which.Message.Should().NotContain(bogus);
        File.Exists(store.KeyPath).Should().BeFalse();
    }

    [Fact]
    public void PlaceholderEnvVar_IsTreatedAsUnset()
    {
        Store(new() { [AttestationKeyStore.EnvVar] = "${LIGHTNING_ENABLE_ATTESTATION_KEY}" })
            .GetIdentity().Source.Should().Be("file");
    }

    [Fact]
    public void CorruptKeyFile_IsAnError_AndLeftUntouched()
    {
        Directory.CreateDirectory(Dir);
        var store = Store();
        WriteOwnerOnly(store.KeyPath, "garbage\n");
        var act = () => store.GetIdentity();
        act.Should().Throw<AttestationKeyException>().Which.Message.Should().Contain("not a valid key");
        File.ReadAllText(store.KeyPath).Should().Be("garbage\n");
    }

    private static void WriteOwnerOnly(string path, string text)
    {
        using var fs = AttestationKeyStore.CreateOwnerOnlyFile(path);
        fs.Write(System.Text.Encoding.ASCII.GetBytes(text));
    }

    [Fact]
    public void KeyFileReadableByOtherAccounts_IsRefused()
    {
        // Loading applies the creation rule. A plainly written file inherits the folder's access
        // list (on Windows: SYSTEM, Administrators...; on POSIX it is made 0644 here).
        Directory.CreateDirectory(Dir);
        var store = Store();
        File.WriteAllText(store.KeyPath, TestKeyHex + "\n");
        if (OperatingSystem.IsWindows())
            AttestationKeyStore.OtherTrustees(store.KeyPath).Should().NotBeEmpty("asserted on the ACL, not on readability");
        else
            File.SetUnixFileMode(store.KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var act = () => store.GetIdentity();
        var msg = act.Should().Throw<AttestationKeyException>().Which.Message;
        msg.Should().Contain("other accounts").And.Contain(OperatingSystem.IsWindows() ? "icacls" : "chmod 600");
        msg.Should().NotContain(TestKeyHex);
        File.ReadAllText(store.KeyPath).Should().Be(TestKeyHex + "\n");
    }

    [Fact]
    public void OwnerOnlyKeyFile_IsAccepted()
    {
        Directory.CreateDirectory(Dir);
        var store = Store();
        WriteOwnerOnly(store.KeyPath, TestKeyHex + "\n");
        AttestationKeyStore.OwnerOnlyProblem(store.KeyPath).Should().BeNull();
        store.GetIdentity().PubkeyHex.Should().Be(TestPubkey);
    }

    [Fact]
    public void LosingTheCreationRace_KeepsTheWinnersKey()
    {
        Directory.CreateDirectory(Dir);
        var store = Store();
        WriteOwnerOnly(store.KeyPath, TestKeyHex + "\n"); // the "other process" got there first
        store.Create(store.KeyPath).Should().BeFalse();
        store.GetIdentity().PubkeyHex.Should().Be(TestPubkey);
        Directory.GetFiles(Dir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Key_IsIndependentOfTheNwcWalletSecret()
    {
        var nwc = "nostr+walletconnect://" + string.Concat(Enumerable.Repeat("ab", 32))
            + "?relay=wss://relay.example.com&secret=" + TestKeyHex;
        Store(new() { ["NWC_CONNECTION_STRING"] = nwc }).GetIdentity().PubkeyHex.Should().NotBe(TestPubkey);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertOwnerOnlyDacl(string path)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        security.GetOwner(typeof(SecurityIdentifier)).Should().Be(user);
        security.AreAccessRulesProtected.Should().BeTrue("inheritance from the folder must be blocked");
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        rules.Should().ContainSingle("only the current user may have an entry — no Administrators, SYSTEM or Users");
        rules[0].IdentityReference.Should().Be(user);
        rules[0].AccessControlType.Should().Be(AccessControlType.Allow);
        rules[0].FileSystemRights.Should().HaveFlag(FileSystemRights.FullControl);
        rules[0].IsInherited.Should().BeFalse();
        // The same thing as SDDL, for the reader: D:P(A;;FA;;;<sid>), with the SID as printed.
        Regex.IsMatch(security.GetSecurityDescriptorSddlForm(AccessControlSections.Access), @"^D:PA?I?\(A;;FA;;;[^)]+\)$")
            .Should().BeTrue();
    }
}
