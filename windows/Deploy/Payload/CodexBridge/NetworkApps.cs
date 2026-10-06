using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CodexBridge;

/// <summary>One established TCP connection with its kernel byte counters.</summary>
readonly record struct TcpConnection(string Key, int Pid, IPAddress Remote, int LocalPort, int RemotePort, ulong BytesIn, ulong BytesOut);

/// <summary>
/// Per-connection TCP byte counters via the TCP extended statistics API (the data Resource
/// Monitor shows). Collection has to be switched on per connection, which needs admin rights;
/// the bridge runs elevated, so this normally works.
/// </summary>
static class TcpEStats
{
    const int AfInet = 2;
    const int AfInet6 = 23;
    const int TcpTableOwnerPidConnections = 4;
    const int TcpConnectionEstatsData = 1;
    const uint StateEstablished = 5;
    const uint ErrorInsufficientBuffer = 122;
    const uint ErrorAccessDenied = 5;
    // sizeof(TCP_ESTATS_DATA_ROD_v0); DataBytesOut is at offset 0, DataBytesIn at 16.
    const int RodSize = 96;
    const int V4RowSize = 24;
    const int V6RowSize = 56;

    static readonly HashSet<string> Enabled = new();
    static readonly IntPtr Rod = Marshal.AllocHGlobal(RodSize);
    static readonly IntPtr EnableRw = AllocEnableRw();

    public static bool AccessDenied { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    struct MibTcpRow
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MibTcp6Row
    {
        public uint State;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId, LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddr;
        public uint RemoteScopeId, RemotePort;
    }

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    static extern uint SetPerTcpConnectionEStats(ref MibTcpRow row, int type, IntPtr rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll")]
    static extern uint GetPerTcpConnectionEStats(ref MibTcpRow row, int type, IntPtr rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize, IntPtr rod, uint rodVersion, uint rodSize);

    [DllImport("iphlpapi.dll")]
    static extern uint SetPerTcp6ConnectionEStats(ref MibTcp6Row row, int type, IntPtr rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll")]
    static extern uint GetPerTcp6ConnectionEStats(ref MibTcp6Row row, int type, IntPtr rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize, IntPtr rod, uint rodVersion, uint rodSize);

    static IntPtr AllocEnableRw()
    {
        // TCP_ESTATS_DATA_RW_v0 { BOOLEAN EnableCollection; }
        var rw = Marshal.AllocHGlobal(1);
        Marshal.WriteByte(rw, 1);
        return rw;
    }

    public static List<TcpConnection> Read()
    {
        var result = new List<TcpConnection>();
        var seen = new HashSet<string>();
        ReadV4(result, seen);
        ReadV6(result, seen);
        Enabled.IntersectWith(seen);
        return result;
    }

    static IntPtr GetTable(int af, out int count)
    {
        count = 0;
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TcpTableOwnerPidConnections, 0);
        for (var attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            var rc = GetExtendedTcpTable(buffer, ref size, false, af, TcpTableOwnerPidConnections, 0);
            if (rc == 0)
            {
                count = Marshal.ReadInt32(buffer);
                return buffer;
            }
            Marshal.FreeHGlobal(buffer);
            if (rc != ErrorInsufficientBuffer)
            {
                break;
            }
        }
        return IntPtr.Zero;
    }

    static int Port(uint networkOrder) => (int)(((networkOrder & 0xFF) << 8) | ((networkOrder >> 8) & 0xFF));

    static void ReadV4(List<TcpConnection> result, HashSet<string> seen)
    {
        var table = GetTable(AfInet, out var count);
        if (table == IntPtr.Zero)
        {
            return;
        }
        try
        {
            for (var i = 0; i < count; i++)
            {
                var row = table + 4 + i * V4RowSize;
                var tcpRow = new MibTcpRow
                {
                    State = (uint)Marshal.ReadInt32(row, 0),
                    LocalAddr = (uint)Marshal.ReadInt32(row, 4),
                    LocalPort = (uint)Marshal.ReadInt32(row, 8),
                    RemoteAddr = (uint)Marshal.ReadInt32(row, 12),
                    RemotePort = (uint)Marshal.ReadInt32(row, 16),
                };
                var pid = Marshal.ReadInt32(row, 20);
                if (tcpRow.State != StateEstablished)
                {
                    continue;
                }
                var remote = new IPAddress(BitConverter.GetBytes(tcpRow.RemoteAddr));
                if (IPAddress.IsLoopback(remote))
                {
                    continue;
                }
                var key = $"4|{tcpRow.LocalAddr}|{tcpRow.LocalPort}|{tcpRow.RemoteAddr}|{tcpRow.RemotePort}";
                seen.Add(key);
                if (!Enabled.Contains(key))
                {
                    var rc = SetPerTcpConnectionEStats(ref tcpRow, TcpConnectionEstatsData, EnableRw, 0, 1, 0);
                    AccessDenied = rc == ErrorAccessDenied;
                    if (rc != 0)
                    {
                        continue;
                    }
                    Enabled.Add(key);
                }
                if (GetPerTcpConnectionEStats(ref tcpRow, TcpConnectionEstatsData, IntPtr.Zero, 0, 0,
                        IntPtr.Zero, 0, 0, Rod, 0, RodSize) == 0)
                {
                    result.Add(new TcpConnection(key, pid, remote, Port(tcpRow.LocalPort), Port(tcpRow.RemotePort),
                        (ulong)Marshal.ReadInt64(Rod, 16), (ulong)Marshal.ReadInt64(Rod, 0)));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    static void ReadV6(List<TcpConnection> result, HashSet<string> seen)
    {
        var table = GetTable(AfInet6, out var count);
        if (table == IntPtr.Zero)
        {
            return;
        }
        try
        {
            for (var i = 0; i < count; i++)
            {
                // MIB_TCP6ROW_OWNER_PID: LocalAddr[16], LocalScopeId, LocalPort, RemoteAddr[16],
                // RemoteScopeId, RemotePort, State, OwningPid.
                var row = table + 4 + i * V6RowSize;
                var local = new byte[16];
                var remoteBytes = new byte[16];
                Marshal.Copy(row, local, 0, 16);
                Marshal.Copy(row + 24, remoteBytes, 0, 16);
                var tcpRow = new MibTcp6Row
                {
                    LocalAddr = local,
                    LocalScopeId = (uint)Marshal.ReadInt32(row, 16),
                    LocalPort = (uint)Marshal.ReadInt32(row, 20),
                    RemoteAddr = remoteBytes,
                    RemoteScopeId = (uint)Marshal.ReadInt32(row, 40),
                    RemotePort = (uint)Marshal.ReadInt32(row, 44),
                    State = (uint)Marshal.ReadInt32(row, 48),
                };
                var pid = Marshal.ReadInt32(row, 52);
                if (tcpRow.State != StateEstablished)
                {
                    continue;
                }
                var remote = new IPAddress(remoteBytes);
                if (remote.IsIPv4MappedToIPv6)
                {
                    remote = remote.MapToIPv4();
                }
                if (IPAddress.IsLoopback(remote))
                {
                    continue;
                }
                var key = $"6|{Convert.ToHexString(local)}|{tcpRow.LocalPort}|{Convert.ToHexString(remoteBytes)}|{tcpRow.RemotePort}";
                seen.Add(key);
                if (!Enabled.Contains(key))
                {
                    var rc = SetPerTcp6ConnectionEStats(ref tcpRow, TcpConnectionEstatsData, EnableRw, 0, 1, 0);
                    AccessDenied = rc == ErrorAccessDenied;
                    if (rc != 0)
                    {
                        continue;
                    }
                    Enabled.Add(key);
                }
                if (GetPerTcp6ConnectionEStats(ref tcpRow, TcpConnectionEstatsData, IntPtr.Zero, 0, 0,
                        IntPtr.Zero, 0, 0, Rod, 0, RodSize) == 0)
                {
                    result.Add(new TcpConnection(key, pid, remote, Port(tcpRow.LocalPort), Port(tcpRow.RemotePort),
                        (ulong)Marshal.ReadInt64(Rod, 16), (ulong)Marshal.ReadInt64(Rod, 0)));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }
}

/// <summary>LAN = private, link-local and multicast ranges plus the subnets of the local adapters.</summary>
sealed class LanClassifier
{
    List<(byte[] Network, int Prefix)> _subnets = new();
    DateTime _refreshedAt = DateTime.MinValue;

    public bool IsLan(IPAddress ip)
    {
        if (DateTime.UtcNow - _refreshedAt > TimeSpan.FromSeconds(30))
        {
            Refresh();
        }
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            if (b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) ||
                (b[0] == 169 && b[1] == 254) || (b[0] >= 224 && b[0] <= 239) || ip.Equals(IPAddress.Broadcast))
            {
                return true;
            }
        }
        else if ((b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) || b[0] == 0xFF)
        {
            return true;
        }
        return _subnets.Any(s => s.Network.Length == b.Length && InSubnet(b, s.Network, s.Prefix));
    }

    void Refresh()
    {
        _refreshedAt = DateTime.UtcNow;
        var subnets = new List<(byte[], int)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.PrefixLength > 0)
                    {
                        subnets.Add((address.Address.GetAddressBytes(), address.PrefixLength));
                    }
                }
            }
        }
        catch
        {
            // Keep the static ranges if the adapter list can't be read.
        }
        _subnets = subnets;
    }

    static bool InSubnet(byte[] address, byte[] network, int prefix)
    {
        for (var i = 0; i < address.Length && prefix > 0; i++, prefix -= 8)
        {
            var mask = prefix >= 8 ? 0xFF : (0xFF << (8 - prefix)) & 0xFF;
            if ((address[i] & mask) != (network[i] & mask))
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>
/// Turns a process into a readable application name and icon, like Task Manager does:
/// the executable's file description ("Google Chrome"), the service display name for
/// svchost, and "helper · host app" for runtimes and nameless helpers ("node · Visual Studio Code").
/// Icons are saved as PNG files next to temps.txt for the Rainmeter skin.
/// </summary>
sealed class AppResolver
{
    public const string BlankIcon = AppIcons.Blank;
    public const string DefaultIcon = AppIcons.Default;

    // Shells and system processes are never the "host app" of a helper.
    static readonly HashSet<string> NotHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "svchost.exe", "services.exe", "wininit.exe", "winlogon.exe", "userinit.exe", "sihost.exe",
        "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe", "openconsole.exe", "runtimebroker.exe",
        "taskhostw.exe", "dllhost.exe", "smss.exe", "csrss.exe", "system", "",
    };
    // Runtimes and embedded browsers carry no identity of their own.
    static readonly HashSet<string> Runtimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "node.exe", "python.exe", "pythonw.exe", "java.exe", "javaw.exe", "dotnet.exe", "msedgewebview2.exe",
        "electron.exe", "rundll32.exe", "wscript.exe", "cscript.exe", "curl.exe", "git-remote-https.exe",
    };

    readonly string _iconDir;
    readonly Dictionary<long, (string Label, string Icon)> _cache = new();
    readonly Dictionary<string, string> _icons = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<int, string> _services = new();
    Dictionary<int, int> _parents = new();
    DateTime _servicesAt = DateTime.MinValue;
    DateTime _parentsAt = DateTime.MinValue;
    DateTime _cacheAt = DateTime.UtcNow;

    public AppResolver(string iconDir)
    {
        _iconDir = iconDir;
        try
        {
            Directory.CreateDirectory(iconDir);
            using (var blank = new Bitmap(1, 1))
            {
                blank.MakeTransparent();
                blank.Save(Path.Combine(iconDir, "_blank.png"), ImageFormat.Png);
            }
            using var app = SystemIcons.Application.ToBitmap();
            app.Save(Path.Combine(iconDir, "_app.png"), ImageFormat.Png);
        }
        catch
        {
            // Icons are cosmetic.
        }
    }

    public (string Label, string Icon) Resolve(int pid, int localPort, int remotePort)
    {
        if (DateTime.UtcNow - _cacheAt > TimeSpan.FromMinutes(5))
        {
            _cache.Clear();
            _cacheAt = DateTime.UtcNow;
        }

        // The kernel ("System", PID 4) owns SMB file sharing connections.
        if (pid is 0 or 4)
        {
            var smb = localPort is 445 or 139 || remotePort is 445 or 139;
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            return (smb ? "Windows file sharing (SMB)" : "Windows (System)", IconFor(explorer));
        }

        if (!_cache.TryGetValue(pid, out var result))
        {
            result = ResolveProcess(pid);
            _cache[pid] = result;
        }
        return result;
    }

    (string, string) ResolveProcess(int pid)
    {
        var path = ImagePath(pid);
        if (path is null)
        {
            return ($"PID {pid}", DefaultIcon);
        }
        var file = Path.GetFileName(path);
        if (file.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            return (ServiceNames(pid) ?? "Service Host", IconFor(path));
        }

        var label = Describe(path, out var hasIdentity);
        if (hasIdentity && !Runtimes.Contains(file))
        {
            return (label, IconFor(path));
        }

        // Nameless helper or runtime: name it after the app that started it.
        var host = HostApp(pid, path);
        return host is null
            ? (label, IconFor(path))
            : ($"{label} · {host.Value.Label}", IconFor(host.Value.Path));
    }

    (string Label, string Path)? HostApp(int pid, string ownPath)
    {
        if (DateTime.UtcNow - _parentsAt > TimeSpan.FromSeconds(10))
        {
            _parents = Query("SELECT ProcessId, ParentProcessId FROM Win32_Process",
                o => (Convert.ToInt32(o["ProcessId"]), Convert.ToInt32(o["ParentProcessId"])));
            _parentsAt = DateTime.UtcNow;
        }
        var seen = new HashSet<int> { pid };
        var current = pid;
        for (var depth = 0; depth < 6 && _parents.TryGetValue(current, out var parent) && seen.Add(parent); depth++)
        {
            current = parent;
            var path = ImagePath(current);
            if (path is null || path.Equals(ownPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var file = Path.GetFileName(path);
            if (NotHosts.Contains(file) || Runtimes.Contains(file))
            {
                continue;
            }
            return (Describe(path, out _), path);
        }
        return null;
    }

    static string Describe(string path, out bool hasIdentity)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            foreach (var candidate in new[] { info.FileDescription, info.ProductName })
            {
                var text = candidate?.Trim();
                if (!string.IsNullOrEmpty(text) && text.Length <= 60)
                {
                    hasIdentity = true;
                    return text;
                }
            }
        }
        catch
        {
            // No version resource: fall back to the file name.
        }
        hasIdentity = false;
        return fileName;
    }

    string? ServiceNames(int pid)
    {
        if (DateTime.UtcNow - _servicesAt > TimeSpan.FromSeconds(60))
        {
            _services = Query("SELECT ProcessId, DisplayName FROM Win32_Service WHERE ProcessId <> 0",
                o => (Convert.ToInt32(o["ProcessId"]), o["DisplayName"]?.ToString() ?? ""));
            _servicesAt = DateTime.UtcNow;
        }
        return _services.TryGetValue(pid, out var names) && names.Length > 0 ? names : null;
    }

    // WMI query into pid -> value; several rows per pid (shared svchost) are joined with ", ".
    static Dictionary<int, T> Query<T>(string wql, Func<ManagementBaseObject, (int Pid, T Value)> select)
    {
        var result = new Dictionary<int, T>();
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    var (pid, value) = select(item);
                    if (value is string text && result.TryGetValue(pid, out var existing) && existing is string previous)
                    {
                        result[pid] = (T)(object)$"{previous}, {text}";
                    }
                    else
                    {
                        result[pid] = value;
                    }
                }
            }
        }
        catch
        {
            // WMI unavailable: names fall back to the executable description.
        }
        return result;
    }

    string IconFor(string path)
    {
        if (_icons.TryGetValue(path, out var cached))
        {
            return cached;
        }
        var icon = DefaultIcon;
        try
        {
            var name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..16] + ".png";
            var file = Path.Combine(_iconDir, name);
            if (!File.Exists(file))
            {
                using var extracted = Icon.ExtractAssociatedIcon(path);
                if (extracted is not null)
                {
                    using var bitmap = extracted.ToBitmap();
                    bitmap.Save(file, ImageFormat.Png);
                }
            }
            if (File.Exists(file))
            {
                icon = $@"AppIcons\{name}";
            }
        }
        catch
        {
            // Protected or missing executable: generic icon.
        }
        _icons[path] = icon;
        return icon;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    static string? ImagePath(int pid)
    {
        const uint ProcessQueryLimitedInformation = 0x1000;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var size = 1024;
            var name = new StringBuilder(size);
            return QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}

/// <summary>Per-application TCP throughput, Internet/LAN split, from connection byte counters.</summary>
sealed class AppTraffic
{
    readonly LanClassifier _lan = new();
    readonly AppResolver _apps;
    Dictionary<string, (ulong In, ulong Out)>? _previous;
    DateTime _previousAt;

    public AppTraffic(string iconDir) => _apps = new AppResolver(iconDir);

    public bool Available => !TcpEStats.AccessDenied;

    public Dictionary<string, AppRates> Sample()
    {
        var now = DateTime.UtcNow;
        var connections = TcpEStats.Read();
        var result = new Dictionary<string, AppRates>();
        if (_previous is not null && now > _previousAt)
        {
            var scale = 8 / (now - _previousAt).TotalSeconds / 1_000_000;
            foreach (var c in connections)
            {
                // Collection starts when the bridge first sees a connection, so new ones start near zero.
                _previous.TryGetValue(c.Key, out var prev);
                var down = (c.BytesIn > prev.In ? c.BytesIn - prev.In : 0) * scale;
                var up = (c.BytesOut > prev.Out ? c.BytesOut - prev.Out : 0) * scale;
                if (down + up <= 0)
                {
                    continue;
                }
                var (label, icon) = _apps.Resolve(c.Pid, c.LocalPort, c.RemotePort);
                if (!result.TryGetValue(label, out var rates))
                {
                    result[label] = rates = new AppRates { Icon = icon };
                }
                if (_lan.IsLan(c.Remote))
                {
                    rates.LanDown += down;
                    rates.LanUp += up;
                }
                else
                {
                    rates.WanDown += down;
                    rates.WanUp += up;
                }
            }
        }
        _previous = connections.ToDictionary(c => c.Key, c => (c.BytesIn, c.BytesOut));
        _previousAt = now;
        return result;
    }
}
