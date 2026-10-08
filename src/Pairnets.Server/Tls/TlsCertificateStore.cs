using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Pairnets.Server.Tls;

/// <summary>
/// The HTTPS certificate for the nest's own address (e.g. nest.pairnets.app), read from
/// <c>fullchain.pem</c> and <c>privkey.pem</c> in <see cref="SyncOptions.EffectiveTlsDir"/>. certbot's
/// deploy hook copies renewed files there; the next handshake after <see cref="SyncOptions.TlsRecheckInterval"/>
/// picks them up, so a renewal never needs a restart. The intermediates from fullchain.pem are sent
/// with the certificate, so no client (and not this server) has to fetch them from the network.
/// </summary>
public sealed class TlsCertificateStore
{
    public const string CertificateFile = "fullchain.pem";
    public const string KeyFile = "privkey.pem";

    private readonly string _certPath;
    private readonly string _keyPath;
    private readonly TimeSpan _recheck;
    private readonly ILogger<TlsCertificateStore> _log;
    private readonly object _gate = new();
    private volatile Loaded? _current;
    private (DateTime, long, DateTime, long) _stamp;
    private long _nextCheckTicks;
    private bool _warnedMissing;

    public TlsCertificateStore(SyncOptions options, ILogger<TlsCertificateStore> log)
    {
        _certPath = Path.Combine(options.EffectiveTlsDir, CertificateFile);
        _keyPath = Path.Combine(options.EffectiveTlsDir, KeyFile);
        _recheck = options.TlsRecheckInterval;
        _log = log;
    }

    /// <summary>The certificate to present, or null while none has been installed yet.</summary>
    public SslStreamCertificateContext? Current
    {
        get
        {
            Refresh();
            return _current?.Context;
        }
    }

    /// <summary>The leaf certificate currently in use (for logs and tests).</summary>
    public X509Certificate2? Certificate
    {
        get
        {
            Refresh();
            return _current?.Leaf;
        }
    }

    /// <summary>Re-reads the files when they changed (at most once per recheck interval while a certificate is loaded).</summary>
    public void Refresh()
    {
        var now = Environment.TickCount64;
        if (_current is not null && now < Interlocked.Read(ref _nextCheckTicks))
            return;
        lock (_gate)
        {
            if (_current is not null && now < _nextCheckTicks)
                return;
            _nextCheckTicks = now + (long)_recheck.TotalMilliseconds;
            try
            {
                var cert = new FileInfo(_certPath);
                var key = new FileInfo(_keyPath);
                if (!cert.Exists || !key.Exists)
                {
                    if (!_warnedMissing)
                    {
                        _log.LogWarning("No HTTPS certificate yet: expected {Cert} and {Key}. HTTPS connections fail until both files are there (behind the Cloudflare Tunnel, leave Sync:HttpsUrl unset instead).", _certPath, _keyPath);
                        _warnedMissing = true;
                    }
                    return;
                }
                var stamp = (cert.LastWriteTimeUtc, cert.Length, key.LastWriteTimeUtc, key.Length);
                if (_current is not null && stamp == _stamp)
                    return;
                var loaded = Load(_certPath, _keyPath);
                _current = loaded;
                _stamp = stamp;
                _warnedMissing = false;
                _log.LogInformation("HTTPS certificate loaded: {Subject}, valid until {NotAfter:yyyy-MM-dd} ({Count} intermediate(s))",
                    loaded.Leaf.Subject, loaded.Leaf.NotAfter.ToUniversalTime(), loaded.IntermediateCount);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException)
            {
                // Typically the hook is halfway through replacing the pair; keep the old one and look again soon.
                _log.LogWarning("Could not load the HTTPS certificate from {Dir}: {Error}", Path.GetDirectoryName(_certPath), ex.Message);
            }
        }
    }

    private static Loaded Load(string certPath, string keyPath)
    {
        var leaf = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        if (OperatingSystem.IsWindows())
        {
            // SChannel cannot use the ephemeral key that PEM import produces.
            using var ephemeral = leaf;
            leaf = new X509Certificate2(ephemeral.Export(X509ContentType.Pkcs12));
        }
        var all = new X509Certificate2Collection();
        all.ImportFromPemFile(certPath);
        var intermediates = new X509Certificate2Collection();
        foreach (var c in all)
        {
            if (c.Thumbprint != leaf.Thumbprint)
                intermediates.Add(c);
        }
        var context = SslStreamCertificateContext.Create(leaf, intermediates, offline: true);
        return new Loaded(leaf, context, intermediates.Count);
    }

    private sealed record Loaded(X509Certificate2 Leaf, SslStreamCertificateContext Context, int IntermediateCount);
}
