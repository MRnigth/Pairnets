using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Pairnets.Server.Auth;

namespace Pairnets.Tests.Infrastructure;

/// <summary>An <see cref="IEmailSender"/> that keeps what it was asked to send.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    public sealed record Sent(string To, string Subject, string Text, string Html)
    {
        /// <summary>The sign-in link in the message, and the code after "#code=".</summary>
        public string Link => Regex.Match(Text, @"https://\S+/email-link#code=\S+").Value;

        public string Code => Link[(Link.IndexOf("#code=", StringComparison.Ordinal) + 6)..];
    }

    private readonly List<Sent> _messages = [];

    public IReadOnlyList<Sent> Messages
    {
        get
        {
            lock (_messages)
                return _messages.ToList();
        }
    }

    public Task SendAsync(string to, string subject, string textBody, string htmlBody, CancellationToken ct = default)
    {
        lock (_messages)
            _messages.Add(new Sent(to, subject, textBody, htmlBody));
        return Task.CompletedTask;
    }
}

/// <summary>A tiny SMTP server on localhost that accepts mail without TLS or login and keeps it, to test the real sender.</summary>
public sealed class FakeSmtpServer : IAsyncDisposable
{
    public sealed record Mail(string From, string To, string Data);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly List<Mail> _mails = [];

    public FakeSmtpServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public IReadOnlyList<Mail> Mails
    {
        get
        {
            lock (_mails)
                return _mails.ToList();
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        string from = string.Empty, to = string.Empty;
        await writer.WriteLineAsync("220 fake ESMTP");
        while (await reader.ReadLineAsync() is { } line)
        {
            var command = line.ToUpperInvariant();
            if (command.StartsWith("EHLO", StringComparison.Ordinal) || command.StartsWith("HELO", StringComparison.Ordinal))
                await writer.WriteLineAsync("250 fake");
            else if (command.StartsWith("MAIL FROM", StringComparison.Ordinal))
            {
                from = line[(line.IndexOf(':') + 1)..].Trim().Trim('<', '>');
                await writer.WriteLineAsync("250 OK");
            }
            else if (command.StartsWith("RCPT TO", StringComparison.Ordinal))
            {
                to = line[(line.IndexOf(':') + 1)..].Trim().Trim('<', '>');
                await writer.WriteLineAsync("250 OK");
            }
            else if (command == "DATA")
            {
                await writer.WriteLineAsync("354 go on");
                var data = new StringBuilder();
                while (await reader.ReadLineAsync() is { } part && part != ".")
                    data.AppendLine(part.StartsWith("..", StringComparison.Ordinal) ? part[1..] : part);
                lock (_mails)
                    _mails.Add(new Mail(from, to, data.ToString()));
                await writer.WriteLineAsync("250 queued");
            }
            else if (command == "QUIT")
            {
                await writer.WriteLineAsync("221 bye");
                return;
            }
            else
                await writer.WriteLineAsync("250 OK");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _loop;
    }
}
