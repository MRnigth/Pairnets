using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace Pairnets.Core.Client;

/// <summary>
/// The name a new computer gets in its nest: the one its owner already knows it by. Windows: its name as typed when it
/// was set up ("StudyPC", where <see cref="Environment.MachineName"/> says "STUDYPC"); a Mac: its Computer Name in
/// Sharing settings ("Family MacBook Pro"); Linux: its pretty hostname when set, otherwise its hostname. The name
/// is cleaned like the nest cleans it (<see cref="PlainText"/>) and kept to 64 characters, so the service and the
/// nest take it as it is.
/// </summary>
public static partial class ComputerName
{
    /// <summary>The longest name the service and the nest take.</summary>
    public const int MaxLength = 64;

    private static readonly Lazy<string> Own = new(() => Choose(Pretty(), HostName(), Environment.MachineName, PairingFlow.SystemName()));

    /// <summary>This computer's own name, cleaned (worked out once).</summary>
    public static string Default() => Own.Value;

    /// <summary>A typed or received name without hidden characters, trimmed, at most 64 characters; empty when nothing is left.</summary>
    public static string Clean(string? name) => PlainText.Clean(name, MaxLength);

    /// <summary>
    /// The name to use from what the system says: the pretty name, else the hostname without its domain
    /// ("kitchen-laptop.local" → "kitchen-laptop"), else the machine name, else a plain word for the system.
    /// </summary>
    internal static string Choose(string? pretty, string? hostName, string? machineName, string system)
    {
        foreach (var candidate in new[] { Clean(pretty), Clean(WithoutDomain(hostName)), Clean(machineName) })
        {
            if (candidate.Length > 0)
                return candidate;
        }
        return system switch
        {
            "Windows" => "Windows PC",
            "macOS" => "Mac",
            "Linux" => "Linux computer",
            _ => "Computer",
        };
    }

    /// <summary>"kitchen-laptop" from "kitchen-laptop.local" or "kitchen-laptop.home.lan"; a name with a space is left alone.</summary>
    internal static string? WithoutDomain(string? hostName)
    {
        var name = hostName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Contains(' ', StringComparison.Ordinal) || IPAddress.TryParse(name, out _))
            return name;
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>
    /// True for a name worth offering this computer's own name instead of, once: a numbered placeholder ("PC-1",
    /// "pc 2") or the all-capitals machine name older versions gave ("STUDYPC" for "StudyPC"), when the own name differs.
    /// </summary>
    public static bool WorthOffering(string? current, string own)
    {
        var name = Clean(current);
        if (name.Length == 0 || own.Length == 0 || string.Equals(name, own, StringComparison.Ordinal))
            return false;
        if (Placeholder().IsMatch(name))
            return true;
        return string.Equals(name, own, StringComparison.OrdinalIgnoreCase) && name.Any(char.IsLetter) && name == name.ToUpperInvariant();
    }

    [GeneratedRegex(@"^(pc|computer)[\s_-]?\d{1,3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    private static string? HostName()
    {
        try
        {
            return Dns.GetHostName();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The name people see: a Mac's Computer Name, or Linux's PRETTY_HOSTNAME. Null on Windows (its hostname is that name).</summary>
    private static string? Pretty()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                return Run("/usr/sbin/scutil", "--get ComputerName");
            if (OperatingSystem.IsLinux() && File.Exists("/etc/machine-info"))
                return PrettyHostName(File.ReadAllLines("/etc/machine-info"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
        return null;
    }

    /// <summary>PRETTY_HOSTNAME from the lines of /etc/machine-info (quotes and shell escapes removed), or null.</summary>
    internal static string? PrettyHostName(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith("PRETTY_HOSTNAME=", StringComparison.Ordinal))
                continue;
            var value = line["PRETTY_HOSTNAME=".Length..].Trim();
            if (value.Length >= 2 && (value[0] is '"' or '\'') && value[^1] == value[0])
                value = value[1..^1];
            return value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\'", "'", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }
        return null;
    }

    /// <summary>The first line a short command prints, or null when it fails or takes over two seconds.</summary>
    private static string? Run(string file, string arguments)
    {
        if (!File.Exists(file))
            return null;
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null)
            return null;
        var output = process.StandardOutput.ReadLineAsync();
        if (!process.WaitForExit(2000))
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            return null;
        }
        return process.ExitCode == 0 && output.Wait(500) ? output.Result : null;
    }
}
