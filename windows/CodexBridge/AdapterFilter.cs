using System.Net.NetworkInformation;

namespace CodexBridge;

/// <summary>
/// Which network adapters are service adapters rather than a real link: counting them would add
/// a second copy of the real card's traffic (Hyper-V/WSL virtual switches, VPN tunnels) or traffic
/// that never leaves the PC. The bridge skips them and the settings window does not list them.
/// </summary>
static class AdapterFilter
{
    /// <summary>
    /// Words in the adapter's name or description, for adapters whose type looks like Ethernet.
    /// Narrow on purpose: inside a Hyper-V or VMware virtual machine the real card is
    /// "Microsoft Hyper-V Network Adapter" / "vmxnet3 Ethernet Adapter", which must still count.
    /// </summary>
    public static readonly string[] BuiltInWords =
    {
        "hyper-v virtual",          // host side: "Hyper-V Virtual Ethernet Adapter", "...Switch Extension Adapter"
        "vethernet",                // "vEthernet (Default Switch)", "vEthernet (WSL)"
        "wsl",
        "virtualbox host-only",
        "vmware virtual ethernet",  // host side of VMware's VMnet1/VMnet8
        "bluetooth",                // Bluetooth personal area network
        "wan miniport",
        "teredo",
        "isatap",
        "pseudo-interface",
        "npcap loopback",
        "wireguard",                // VPNs: their traffic also crosses the real card
        "tap-windows",
        "openvpn",
        "tailscale",
        "zerotier",
    };

    /// <summary>
    /// The list older versions wrote into config.json as "ignoreAdaptersContaining" and showed as
    /// "Advanced ignore words". Ignored when read back: some were too broad ("hyper-v" matched the
    /// real card of a Hyper-V virtual machine), the rest are covered by <see cref="BuiltInWords"/>.
    /// </summary>
    static readonly HashSet<string> OldDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        "hyper-v", "hyper-v virtual", "virtual switch", "virtual switch extension", "virtual filtering platform", "wsl",
        "teredo", "teredo tunneling", "wan miniport", "qos packet scheduler", "wfp native mac layer", "wfp 802.3 mac layer",
        "lightweight filter", "native wifi filter driver", "virtual wifi filter driver", "pseudo-interface", "vswitch",
        "vethernet", "bluetooth",
    };

    /// <summary>The user's own words from config.json, without the defaults older versions saved there.</summary>
    public static List<string> UserWords(IEnumerable<string>? configured) =>
        (configured ?? Enumerable.Empty<string>()).Select(w => w.Trim()).Where(w => w.Length > 0 && !OldDefaults.Contains(w)).ToList();

    /// <summary>Loopback and tunnels by type, the rest by <see cref="BuiltInWords"/> and the user's words.</summary>
    public static bool IsServiceAdapter(NetworkInterface nic, IEnumerable<string>? userWords = null)
    {
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return true;
        }
        var text = $"{nic.Name} {nic.Description}";
        return BuiltInWords.Concat(userWords ?? Enumerable.Empty<string>())
            .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
