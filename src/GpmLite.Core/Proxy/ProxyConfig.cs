namespace GpmLite.Core.Proxy;

public enum ProxyType { Http, Socks4, Socks5 }

public sealed record ProxyConfig(ProxyType Type, string Host, int Port, string? Username = null, string? Password = null)
{
    public bool HasAuth => !string.IsNullOrEmpty(Username);

    public override string ToString()
    {
        var scheme = Type switch { ProxyType.Socks4 => "socks4", ProxyType.Socks5 => "socks5", _ => "http" };
        return HasAuth ? $"{scheme}://{Username}:***@{Host}:{Port}" : $"{scheme}://{Host}:{Port}";
    }

    /// <summary>
    /// Accepts the formats users typically paste:
    /// <c>host:port</c>, <c>host:port:user:pass</c>, <c>user:pass@host:port</c>,
    /// optionally prefixed with <c>http://</c>, <c>https://</c>, <c>socks4://</c>, <c>socks5://</c>.
    /// Returns null for an empty string (= direct connection).
    /// </summary>
    public static ProxyConfig? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        var type = ProxyType.Http;
        var schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            type = s[..schemeEnd].ToLowerInvariant() switch
            {
                "http" or "https" => ProxyType.Http,
                "socks4" or "socks4a" => ProxyType.Socks4,
                "socks5" or "socks5h" or "socks" => ProxyType.Socks5,
                var other => throw new FormatException($"Unsupported proxy scheme '{other}'"),
            };
            s = s[(schemeEnd + 3)..].TrimEnd('/');
        }

        string? user = null, pass = null;
        string hostPort;
        var at = s.LastIndexOf('@');
        if (at >= 0)
        {
            var cred = s[..at];
            hostPort = s[(at + 1)..];
            var colon = cred.IndexOf(':');
            if (colon < 0) throw new FormatException("Proxy credentials must be user:pass");
            user = Uri.UnescapeDataString(cred[..colon]);
            pass = Uri.UnescapeDataString(cred[(colon + 1)..]);
        }
        else
        {
            var parts = s.Split(':');
            if (parts.Length == 4)
            {
                hostPort = $"{parts[0]}:{parts[1]}";
                user = parts[2];
                pass = parts[3];
            }
            else if (parts.Length > 4)
            {
                // host:port:user:pass where the password itself contains ':'
                hostPort = $"{parts[0]}:{parts[1]}";
                user = parts[2];
                pass = string.Join(':', parts.Skip(3));
            }
            else hostPort = s;
        }

        var sep = hostPort.LastIndexOf(':');
        if (sep <= 0) throw new FormatException("Proxy must include host:port");
        var host = hostPort[..sep].Trim('[', ']');
        if (!int.TryParse(hostPort[(sep + 1)..], out var port) || port is < 1 or > 65535)
            throw new FormatException($"Invalid proxy port in '{raw}'");
        if (string.IsNullOrWhiteSpace(host)) throw new FormatException("Proxy host is empty");
        if (type == ProxyType.Socks4 && user is not null && pass is not null && pass.Length > 0)
            throw new FormatException("SOCKS4 does not support password authentication");

        return new ProxyConfig(type, host, port, string.IsNullOrEmpty(user) ? null : user, pass);
    }

    public static bool TryParse(string? raw, out ProxyConfig? config, out string? error)
    {
        try
        {
            config = Parse(raw);
            error = null;
            return true;
        }
        catch (FormatException e)
        {
            config = null;
            error = e.Message;
            return false;
        }
    }
}
