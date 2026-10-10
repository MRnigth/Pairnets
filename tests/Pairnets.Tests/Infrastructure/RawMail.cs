using System.Text;
using System.Text.RegularExpressions;

namespace Pairnets.Tests.Infrastructure;

/// <summary>
/// A mail as an SMTP server received it (the raw DATA), read the way a mail program would: headers, and the text of
/// every part with its transfer encoding (quoted-printable, base64) undone. Enough to find a sign-in link in it.
/// </summary>
public sealed partial class RawMail
{
    private RawMail(string to, string subject, string text)
    {
        To = to;
        Subject = subject;
        Text = text;
    }

    /// <summary>The address in the To header, without a display name.</summary>
    public string To { get; }

    public string Subject { get; }

    /// <summary>All text and HTML parts, decoded, one after the other.</summary>
    public string Text { get; }

    /// <summary>The first email-link address in the mail ("" when it has none, like a welcome mail).</summary>
    public string Link => LinkPattern().Match(Text) is { Success: true } m ? m.Value : string.Empty;

    /// <summary>The one-time code after "#code=" in <see cref="Link"/>.</summary>
    public string Code => Link.Length == 0 ? string.Empty : Link[(Link.IndexOf("#code=", StringComparison.Ordinal) + 6)..].Split('&')[0];

    public static RawMail Parse(string data)
    {
        var (headers, body) = Split(data.ReplaceLineEndings("\n"));
        var to = Header(headers, "To");
        var address = AddressPattern().Match(to) is { Success: true } a ? a.Groups[1].Value : to.Trim();
        var text = new StringBuilder();
        ReadPart(headers, body, text);
        return new RawMail(address, DecodeWords(Header(headers, "Subject")), text.ToString());
    }

    private static (List<(string Name, string Value)> Headers, string Body) Split(string part)
    {
        var end = part.IndexOf("\n\n", StringComparison.Ordinal);
        var head = end < 0 ? part : part[..end];
        var body = end < 0 ? string.Empty : part[(end + 2)..];
        var headers = new List<(string, string)>();
        foreach (var line in head.Split('\n'))
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && headers.Count > 0)
            {
                var (name, value) = headers[^1];
                headers[^1] = (name, value + " " + line.Trim()); // a folded header goes on
            }
            else if (line.IndexOf(':') is var colon and > 0)
            {
                headers.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }
        }
        return (headers, body);
    }

    private static string Header(List<(string Name, string Value)> headers, string name) =>
        headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;

    private static void ReadPart(List<(string Name, string Value)> headers, string body, StringBuilder text)
    {
        var type = Header(headers, "Content-Type");
        if (type.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) && BoundaryPattern().Match(type) is { Success: true } b)
        {
            var boundary = "--" + b.Groups[1].Value;
            foreach (var piece in body.Split(boundary).Skip(1))
            {
                if (piece.StartsWith("--", StringComparison.Ordinal))
                    break; // the closing boundary
                var (partHeaders, partBody) = Split(piece.TrimStart('\n'));
                ReadPart(partHeaders, partBody, text);
            }
            return;
        }
        var charset = CharsetPattern().Match(type) is { Success: true } c ? c.Groups[1].Value : "utf-8";
        var encoding = Encoding.GetEncoding(charset is "us-ascii" ? "utf-8" : charset);
        var transfer = Header(headers, "Content-Transfer-Encoding").ToLowerInvariant();
        var decoded = transfer switch
        {
            "quoted-printable" => encoding.GetString(QuotedPrintable(body)),
            "base64" => encoding.GetString(Convert.FromBase64String(string.Concat(body.Where(ch => !char.IsWhiteSpace(ch))))),
            _ => body,
        };
        text.Append(decoded).Append('\n');
    }

    private static byte[] QuotedPrintable(string body)
    {
        var joined = body.Replace("=\n", string.Empty, StringComparison.Ordinal); // soft line breaks
        var bytes = new List<byte>(joined.Length);
        for (var i = 0; i < joined.Length; i++)
        {
            if (joined[i] == '=' && i + 2 < joined.Length && Uri.IsHexDigit(joined[i + 1]) && Uri.IsHexDigit(joined[i + 2]))
            {
                bytes.Add(Convert.ToByte(joined.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(joined[i].ToString()));
            }
        }
        return [.. bytes];
    }

    /// <summary>"=?utf-8?B?...?=" and "=?utf-8?Q?...?=" in a header, as text.</summary>
    private static string DecodeWords(string value) => EncodedWordPattern().Replace(BetweenWordsPattern().Replace(value, "?==?"), m =>
    {
        var encoding = Encoding.GetEncoding(m.Groups["charset"].Value);
        var data = m.Groups["data"].Value;
        return m.Groups["kind"].Value.ToUpperInvariant() == "B"
            ? encoding.GetString(Convert.FromBase64String(data))
            : encoding.GetString(QuotedPrintable(data.Replace('_', ' ')));
    });

    /// <summary>The space between two encoded words in a row is not part of the text.</summary>
    [GeneratedRegex(@"\?=\s+=\?")]
    private static partial Regex BetweenWordsPattern();

    [GeneratedRegex(@"https://\S+?/email-link#code=[^\s""'<>]+")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"<([^>]+)>")]
    private static partial Regex AddressPattern();

    [GeneratedRegex(@"boundary=""?([^"";]+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex BoundaryPattern();

    [GeneratedRegex(@"charset=""?([^"";]+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex CharsetPattern();

    [GeneratedRegex(@"=\?(?<charset>[^?]+)\?(?<kind>[BbQq])\?(?<data>[^?]*)\?=")]
    private static partial Regex EncodedWordPattern();
}
