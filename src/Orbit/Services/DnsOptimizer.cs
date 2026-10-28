using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Orbit.Services;

public sealed record DnsProvider(string Name, string Primary, string Secondary)
{
    public string Servers => Primary + " / " + Secondary;
}

/// <summary>Measured answer time of a DNS server (null = didn't answer).</summary>
public sealed record DnsResult(DnsProvider Provider, double? AverageMs);

/// <summary>
/// Measures how fast public DNS servers answer from this PC and switches Windows to the fastest one.
/// A faster DNS makes websites, logins and game connections start quicker – it doesn't change the in-game ping.
/// </summary>
public static class DnsOptimizer
{
    public static readonly DnsProvider[] Providers =
    {
        new("Cloudflare", "1.1.1.1", "1.0.0.1"),
        new("Google", "8.8.8.8", "8.8.4.4"),
        new("Quad9", "9.9.9.9", "149.112.112.112"),
        new("OpenDNS", "208.67.222.222", "208.67.220.220"),
        new("AdGuard (unfiltered)", "94.140.14.140", "94.140.14.141"),
        new("Control D", "76.76.2.0", "76.76.10.0"),
        new("Comodo Secure", "8.26.56.26", "8.20.247.20"),
    };

    /// <summary>Names that are looked up during the test (things a Roblox player really uses).</summary>
    private static readonly string[] TestNames =
    {
        "www.roblox.com", "apis.roblox.com", "auth.roblox.com", "tr.rbxcdn.com", "discord.com", "github.com",
    };

    /// <summary>The DNS servers Windows uses right now (IPv4, connected adapters with internet).</summary>
    public static List<string> CurrentServers()
    {
        var result = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
                foreach (var dns in props.DnsAddresses)
                    if (dns.AddressFamily == AddressFamily.InterNetwork && !result.Contains(dns.ToString()))
                        result.Add(dns.ToString());
            }
        }
        catch { /* no network info */ }
        return result;
    }

    /// <summary>The provider whose servers Windows uses right now (null = router / other).</summary>
    public static DnsProvider? CurrentProvider()
    {
        var current = CurrentServers();
        return Providers.FirstOrDefault(p => current.Contains(p.Primary));
    }

    /// <summary>Tests the current DNS and every provider at the same time. Fastest first.</summary>
    public static async Task<(DnsResult Current, List<DnsResult> Providers)> TestAllAsync()
    {
        var current = CurrentServers();
        var currentProvider = new DnsProvider("Windows (current)", current.ElementAtOrDefault(0) ?? "–", current.ElementAtOrDefault(1) ?? "–");
        var currentTask = current.Count > 0 ? MeasureAsync(current[0]) : Task.FromResult<double?>(null);
        var tasks = Providers.Select(async p => new DnsResult(p, await MeasureAsync(p.Primary))).ToList();

        var results = (await Task.WhenAll(tasks))
            .OrderBy(r => r.AverageMs ?? double.MaxValue)
            .ToList();
        return (new DnsResult(currentProvider, await currentTask), results);
    }

    /// <summary>Average answer time of one server over several lookups (the first one only warms up).</summary>
    private static async Task<double?> MeasureAsync(string server)
    {
        if (!IPAddress.TryParse(server, out var ip)) return null;
        var times = new List<double>();
        for (var i = 0; i < TestNames.Length; i++)
        {
            var ms = await QueryAsync(ip, TestNames[i]);
            if (ms is { } t && i > 0) times.Add(t);
        }
        return times.Count == 0 ? null : Math.Round(times.Average(), 0);
    }

    /// <summary>Sends one DNS question (A record) over UDP and measures how long the answer takes.</summary>
    private static async Task<double?> QueryAsync(IPAddress server, string name)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var id = (ushort)Random.Shared.Next(ushort.MaxValue);
            var packet = BuildQuery(id, name);
            var watch = Stopwatch.StartNew();
            await udp.SendAsync(packet, packet.Length, new IPEndPoint(server, 53));
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            var answer = await udp.ReceiveAsync(cts.Token);
            watch.Stop();
            if (answer.Buffer.Length < 2 || (answer.Buffer[0] << 8 | answer.Buffer[1]) != id) return null;
            return watch.Elapsed.TotalMilliseconds;
        }
        catch
        {
            return null; // timeout or blocked
        }
    }

    private static byte[] BuildQuery(ushort id, string name)
    {
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        bytes.AddRange(new byte[] { 0, 0, 1, 0, 1 }); // end of name, type A, class IN
        return bytes.ToArray();
    }

    // ---------------- Changing Windows' DNS (needs administrator rights) ----------------

    /// <summary>Uses this provider on every connected network adapter. Windows asks for permission once.</summary>
    public static Task<string?> ApplyAsync(DnsProvider provider) =>
        RunElevatedAsync($"Set-DnsClientServerAddress -InterfaceIndex $_.InterfaceIndex -ServerAddresses ('{provider.Primary}','{provider.Secondary}')");

    /// <summary>Back to the DNS from the router (automatic).</summary>
    public static Task<string?> ResetAsync() =>
        RunElevatedAsync("Set-DnsClientServerAddress -InterfaceIndex $_.InterfaceIndex -ResetServerAddresses");

    /// <returns>null on success, otherwise a message for the user.</returns>
    private static async Task<string?> RunElevatedAsync(string perAdapter)
    {
        var script =
            "$ErrorActionPreference='Stop'; " +
            "Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -ne $null -and $_.NetAdapter.Status -eq 'Up' } | " +
            "ForEach-Object { " + perAdapter + " }; Clear-DnsClientCache";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"" + script.Replace("\"", "\\\"") + "\"",
                UseShellExecute = true,
                Verb = "runas", // administrator rights – Windows shows its permission prompt
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process == null) return "Windows didn't start the change.";
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? null : "Windows couldn't change the DNS (error " + process.ExitCode + ").";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Cancelled – Windows needs your permission to change the DNS.";
        }
        catch (Exception ex)
        {
            return "Couldn't change the DNS: " + ex.Message;
        }
    }
}
