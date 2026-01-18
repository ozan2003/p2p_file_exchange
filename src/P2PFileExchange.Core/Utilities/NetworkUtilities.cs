using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace P2PFileExchange.Core.Utilities;

/// <summary>
/// Provides helpers for network information.
/// </summary>
public static class NetworkUtilities
{
    /// <summary>
    /// Returns the local IPv4 addresses for active network interfaces.
    /// </summary>
    public static IReadOnlyList<IPAddress> GetLocalIPv4Addresses()
    {
        return
        [
            .. NetworkInterface
                .GetAllNetworkInterfaces()
                .Where(adapter =>
                    adapter.OperationalStatus == OperationalStatus.Up
                )
                .Where(adapter =>
                    adapter.NetworkInterfaceType
                    != NetworkInterfaceType.Loopback
                )
                .SelectMany(adapter =>
                    adapter.GetIPProperties().UnicastAddresses
                )
                .Select(address => address.Address)
                .Where(address =>
                    address.AddressFamily == AddressFamily.InterNetwork
                ),
        ];
    }

    /// <summary>
    /// Returns the first available IPv4 address or loopback if none are available.
    /// </summary>
    public static IPAddress GetPrimaryIPv4Address()
    {
        IReadOnlyList<IPAddress> addresses = GetLocalIPv4Addresses();
        return addresses.Count > 0 ? addresses[0] : IPAddress.Loopback;
    }
}
