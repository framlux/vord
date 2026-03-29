// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Net.Sockets;

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// A <see cref="SocketsHttpHandler"/> that blocks outbound connections to private, reserved, and
/// loopback IP addresses at the socket level. This prevents SSRF attacks — including DNS rebinding —
/// because the check occurs on the actual resolved IP at connect time, not on a separate DNS lookup.
/// </summary>
public sealed class SsrfSafeSocketsHttpHandler
{
    /// <summary>
    /// Creates a <see cref="SocketsHttpHandler"/> whose <see cref="SocketsHttpHandler.ConnectCallback"/>
    /// rejects connections to private or reserved IP addresses.
    /// </summary>
    /// <returns>A configured <see cref="SocketsHttpHandler"/>.</returns>
    public static SocketsHttpHandler Create()
    {
        SocketsHttpHandler handler = new()
        {
            ConnectCallback = ConnectAsync,
        };

        return handler;
    }

    /// <summary>
    /// Connect callback that resolves DNS and rejects connections to private or reserved IPs.
    /// </summary>
    internal static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host, cancellationToken);

        if (addresses.Length == 0)
        {
            throw new HttpRequestException(
                $"DNS resolution for '{context.DnsEndPoint.Host}' returned no addresses");
        }

        foreach (IPAddress address in addresses)
        {
            IPAddress addressToCheck = address.IsIPv4MappedToIPv6
                ? address.MapToIPv4()
                : address;

            if (IsPrivateOrReservedIp(addressToCheck))
            {
                throw new HttpRequestException(
                    $"Connection to '{context.DnsEndPoint.Host}' blocked: resolved to a private or reserved IP address");
            }
        }

        // Connect to the first address that succeeded validation
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();

            throw;
        }
    }

    /// <summary>
    /// Checks whether an IP address is private, reserved, or otherwise unsuitable for external connections.
    /// This is the single source of truth for IP safety checks across the application.
    /// </summary>
    /// <param name="ip">The IP address to check.</param>
    /// <returns>True if the IP is private or reserved; otherwise, false.</returns>
    internal static bool IsPrivateOrReservedIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        byte[] bytes = ip.GetAddressBytes();

        if (bytes.Length == 4)
        {
            // 10.0.0.0/8
            if (bytes[0] == 10)
            {
                return true;
            }

            // 172.16.0.0/12
            if ((bytes[0] == 172) && (bytes[1] >= 16) && (bytes[1] <= 31))
            {
                return true;
            }

            // 192.168.0.0/16
            if ((bytes[0] == 192) && (bytes[1] == 168))
            {
                return true;
            }

            // 169.254.0.0/16 (link-local, includes cloud metadata at 169.254.169.254)
            if ((bytes[0] == 169) && (bytes[1] == 254))
            {
                return true;
            }
        }

        if (bytes.Length == 16)
        {
            // IPv6 ULA fc00::/7 (first byte 0xFC or 0xFD)
            if ((bytes[0] == 0xFC) || (bytes[0] == 0xFD))
            {
                return true;
            }

            // IPv6 link-local fe80::/10 (first byte 0xFE, second byte 0x80-0xBF)
            if ((bytes[0] == 0xFE) && ((bytes[1] & 0xC0) == 0x80))
            {
                return true;
            }
        }

        // IPv4-mapped IPv6 addresses (::ffff:x.x.x.x) — re-check the embedded IPv4 portion
        if (ip.IsIPv4MappedToIPv6)
        {
            return IsPrivateOrReservedIp(ip.MapToIPv4());
        }

        return false;
    }
}
