using System.Net;
using System.Net.Sockets;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Detects whether an IP address falls in a private, loopback, link-local,
/// or otherwise-reserved range. Used by the SSRF protection on the L402 HTTP
/// client — both the initial-URL validator in <c>AccessL402ResourceTool</c> and
/// the connect-time <see cref="SsrfConnectValidator"/> that closes the DNS-rebind
/// TOCTOU window.
/// <para/>
/// Coverage mirrors the sibling API repo's <c>PrivateIpAddressDetector</c>
/// (loopback, RFC1918, link-local, unique-local, unspecified) and additionally
/// rejects multicast and the 240.0.0.0/4 reserved space — neither of which is a
/// legitimate outbound fetch target for this tool.
/// </summary>
public static class PrivateIpAddressDetector
{
    /// <summary>
    /// Blocked ranges:
    /// <list type="bullet">
    ///   <item>IPv4 loopback (<c>127.0.0.0/8</c>)</item>
    ///   <item>IPv4 private (<c>10.0.0.0/8</c>, <c>172.16.0.0/12</c>, <c>192.168.0.0/16</c>)</item>
    ///   <item>IPv4 link-local (<c>169.254.0.0/16</c> — includes the cloud metadata IP <c>169.254.169.254</c>)</item>
    ///   <item>IPv4 "this network" (<c>0.0.0.0/8</c>)</item>
    ///   <item>IPv4 multicast (<c>224.0.0.0/4</c>) and reserved (<c>240.0.0.0/4</c>, incl. broadcast)</item>
    ///   <item>IPv6 loopback (<c>::1</c>) and unspecified (<c>::</c>)</item>
    ///   <item>IPv6 unique local (<c>fc00::/7</c>)</item>
    ///   <item>IPv6 link-local (<c>fe80::/10</c>)</item>
    ///   <item>IPv6 multicast (<c>ff00::/8</c>)</item>
    /// </list>
    /// IPv4-mapped IPv6 addresses (e.g., <c>::ffff:127.0.0.1</c>) are unwrapped to
    /// their IPv4 equivalent and re-checked, so a mapped loopback cannot slip past.
    /// </summary>
    public static bool IsPrivate(IPAddress ipAddress)
    {
        // Handle IPv4-mapped IPv6 addresses (e.g., ::ffff:127.0.0.1) so a mapped
        // private/loopback address is classified by its real IPv4 value.
        if (ipAddress.IsIPv4MappedToIPv6)
        {
            ipAddress = ipAddress.MapToIPv4();
        }

        // IPv6 checks
        if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // ::1 (loopback)
            if (IPAddress.IsLoopback(ipAddress))
                return true;

            var bytes = ipAddress.GetAddressBytes();

            // fc00::/7 — unique local addresses (fc00:: through fdff::)
            if ((bytes[0] & 0xFE) == 0xFC)
                return true;

            // fe80::/10 — link-local addresses
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
                return true;

            // ff00::/8 — multicast
            if (bytes[0] == 0xFF)
                return true;

            // :: (unspecified address)
            if (ipAddress.Equals(IPAddress.IPv6Any) || ipAddress.Equals(IPAddress.IPv6None))
                return true;

            return false;
        }

        // IPv4 checks
        var ipBytes = ipAddress.GetAddressBytes();

        // 0.0.0.0/8 — "this network" (includes the unspecified address 0.0.0.0)
        if (ipBytes[0] == 0)
            return true;

        // 127.0.0.0/8 — loopback
        if (ipBytes[0] == 127)
            return true;

        // 10.0.0.0/8 — private
        if (ipBytes[0] == 10)
            return true;

        // 172.16.0.0/12 — private (172.16.0.0 through 172.31.255.255)
        if (ipBytes[0] == 172 && ipBytes[1] >= 16 && ipBytes[1] <= 31)
            return true;

        // 192.168.0.0/16 — private
        if (ipBytes[0] == 192 && ipBytes[1] == 168)
            return true;

        // 169.254.0.0/16 — link-local (includes cloud metadata 169.254.169.254)
        if (ipBytes[0] == 169 && ipBytes[1] == 254)
            return true;

        // 224.0.0.0/4 — multicast (224–239)
        if (ipBytes[0] >= 224 && ipBytes[0] <= 239)
            return true;

        // 240.0.0.0/4 — reserved (240–255, includes 255.255.255.255 broadcast)
        if (ipBytes[0] >= 240)
            return true;

        return false;
    }
}
