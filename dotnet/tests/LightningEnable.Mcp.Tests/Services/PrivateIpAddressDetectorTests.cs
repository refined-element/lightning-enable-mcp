using System.Net;
using LightningEnable.Mcp.Services;
using FluentAssertions;

namespace LightningEnable.Mcp.Tests.Services;

/// <summary>
/// SSRF (F-10d): the private/reserved-IP classifier shared by the initial-URL
/// validator and the connect-time <see cref="SsrfConnectValidator"/>. Driven with
/// IP literals so every case is offline-deterministic (no DNS).
/// </summary>
public class PrivateIpAddressDetectorTests
{
    [Theory]
    // IPv4 loopback / private / link-local / this-network
    [InlineData("127.0.0.1")]
    [InlineData("127.5.6.7")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.1.100")]
    [InlineData("169.254.0.1")]
    [InlineData("169.254.169.254")] // canonical cloud metadata endpoint
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    // IPv4 multicast + reserved + broadcast
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.255")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    // IPv6 loopback / unspecified / link-local / unique-local / multicast
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("ff02::1")]
    // IPv4-mapped IPv6 forms of private addresses must not slip through
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    public void IsPrivate_ReturnsTrue_ForPrivateOrReserved(string ip)
    {
        PrivateIpAddressDetector.IsPrivate(IPAddress.Parse(ip)).Should().BeTrue($"{ip} is private/reserved");
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]   // example.com
    [InlineData("172.15.255.255")]  // just below the 172.16/12 private block
    [InlineData("172.32.0.1")]      // just above the 172.16/12 private block
    [InlineData("169.253.0.1")]     // just below link-local
    [InlineData("11.0.0.1")]
    [InlineData("223.255.255.255")] // just below multicast
    [InlineData("2606:4700:4700::1111")] // public IPv6 (Cloudflare)
    [InlineData("2001:4860:4860::8888")] // public IPv6 (Google)
    public void IsPrivate_ReturnsFalse_ForPublic(string ip)
    {
        PrivateIpAddressDetector.IsPrivate(IPAddress.Parse(ip)).Should().BeFalse($"{ip} is a public address");
    }
}
