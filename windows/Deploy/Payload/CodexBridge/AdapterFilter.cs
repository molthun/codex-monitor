using System.Net.NetworkInformation;
using System.Text.Json.Nodes;

namespace CodexBridge;

/// <summary>
/// Which network adapters are service adapters rather than a real link: counting them would add
/// a second copy of the real card's traffic (Hyper-V/WSL virtual switches, VPN tunnels) or traffic
/// that never leaves the PC. The bridge skips them automatically; an explicit role can override that choice.
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
    /// "Advanced ignore words". Removed from recognized legacy presets: some were too broad ("hyper-v" matched the
    /// real card of a Hyper-V virtual machine), the rest are covered by <see cref="BuiltInWords"/>.
    /// </summary>
    static readonly HashSet<string> OldDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        "hyper-v", "hyper-v virtual", "virtual switch", "virtual switch extension", "virtual filtering platform", "wsl",
        "teredo", "teredo tunneling", "wan miniport", "qos packet scheduler", "wfp native mac layer", "wfp 802.3 mac layer",
        "lightweight filter", "native wifi filter driver", "virtual wifi filter driver", "pseudo-interface", "vswitch",
        "vethernet", "bluetooth",
    };

    public static readonly string[] Roles = { "Auto", "Ethernet", "Wi-Fi", "Wi-Fi hotspot", "Ignore" };

    /// <summary>Unknown roles and explicit Auto use automatic detection.</summary>
    public static string? NormalizeRole(string? role) =>
        Roles.Skip(1).FirstOrDefault(known => string.Equals(known, role?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Remove old defaults only when the known legacy preset is present. A short
    /// explicit list such as ["hyper-v"] must still be respected as a user choice.
    /// </summary>
    public static List<string> UserWords(IEnumerable<string?>? configured)
    {
        var words = (configured ?? Enumerable.Empty<string?>()).Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var legacyCore = new[] { "hyper-v", "virtual switch", "wsl", "teredo", "wan miniport", "bluetooth" };
        var legacy = legacyCore.All(old => words.Contains(old, StringComparer.OrdinalIgnoreCase));
        return legacy ? words.Where(w => !OldDefaults.Contains(w)).ToList() : words;
    }

    public static bool IsServiceAdapter(NetworkInterface nic, IEnumerable<string?>? userWords = null) =>
        IsServiceAdapter(nic.NetworkInterfaceType, nic.Name, nic.Description, userWords);

    public static bool IsServiceAdapter(NetworkInterfaceType type, string name, string description, IEnumerable<string?>? userWords = null)
    {
        if (type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return true;
        }
        var text = $"{name} {description}";
        return BuiltInWords.Concat((userWords ?? Enumerable.Empty<string?>()).OfType<string>())
            .Any(word => !string.IsNullOrWhiteSpace(word) && text.Contains(word.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Explicit roles override automatic service filtering.</summary>
    public static bool ShouldIgnore(NetworkInterfaceType type, string name, string description, string? role, IEnumerable<string?>? userWords = null) =>
        NormalizeRole(role) is { } chosen ? chosen == "Ignore" : IsServiceAdapter(type, name, description, userWords);

    /// <summary>Preserve roles of adapters absent from the settings list.</summary>
    public static JsonObject SaveRoles(JsonObject? existing, IEnumerable<(string Name, string Role)> edited)
    {
        var roles = existing?.DeepClone().AsObject() ?? new JsonObject();
        foreach (var (name, value) in edited)
        {
            // Windows names are case-insensitive; replace old spelling, avoiding duplicate JSON keys.
            foreach (var key in roles.Select(r => r.Key).Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToArray())
                roles.Remove(key);
            if (NormalizeRole(value) is { } role)
                roles[name] = role;
        }
        return roles;
    }
}
