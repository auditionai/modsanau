using System.Text.RegularExpressions;

namespace AuditionModStudio.Core.Diagnostics;

public interface ISensitiveDataRedactor
{
    string Redact(string value);
    bool IsSensitiveName(string name);
}

public sealed partial class SensitiveDataRedactor : ISensitiveDataRedactor
{
    private const string Redacted = "[REDACTED]";
    private const string RedactedSignedUrl = "[REDACTED_SIGNED_URL]";

    public string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var redacted = SignedUrlPattern().Replace(value, match => IsSensitiveUrl(match.Value)
            ? RedactedSignedUrl
            : match.Value);
        redacted = BearerPattern().Replace(redacted, "Bearer " + Redacted);
        redacted = JwtPattern().Replace(redacted, Redacted);
        redacted = QuotedNamedSecretPattern().Replace(redacted,
            match => match.Groups["prefix"].Value + Redacted + match.Groups["suffix"].Value);
        redacted = UnquotedNamedSecretPattern().Replace(redacted,
            match => match.Groups["prefix"].Value + Redacted);
        redacted = KnownSecretPrefixPattern().Replace(redacted, Redacted);
        redacted = SecretLikeWordPattern().Replace(redacted, Redacted);
        return redacted;
    }

    public bool IsSensitiveName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var normalized = new string(name.Where(char.IsAsciiLetterOrDigit)
            .Select(char.ToLowerInvariant).ToArray());
        return normalized is "password" or "passwd" or "pwd" or "authorization"
            or "accesstoken" or "refreshtoken" or "authtoken" or "apikey"
            or "servicerolekey" or "serviceroletoken" or "providerapikey" or "providersecret"
            or "paymentsecret" or "paymentwebhooksecret" or "webhooksecret"
            or "clientsecret" or "signingkey" or "signingsecret"
            or "signingpassword" or "stripesignature" or "signedurl";
    }

    private static bool IsSensitiveUrl(string value)
    {
        var queryStart = value.IndexOf('?');
        if (queryStart < 0) return false;
        var query = value[(queryStart + 1)..];
        return SignedQueryMarkerPattern().IsMatch(query);
    }

    [GeneratedRegex(@"https?://[^\s<>\""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        | RegexOptions.NonBacktracking)]
    private static partial Regex SignedUrlPattern();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]{4,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"(?<prefix>[\""']?(?:password|passwd|pwd|access[_-]?token|refresh[_-]?token|auth[_-]?token|authorization|api[_-]?key|service[_-]?role[_-]?key|provider[_-]?(?:api[_-]?key|secret)|payment[_-]?(?:webhook[_-]?secret|secret)|webhook[_-]?secret|client[_-]?secret|signing[_-]?(?:key|secret|password)|stripe[_-]?signature)[\""']?\s*[:=]\s*[\""'])(?<value>[^\""'\r\n]+)(?<suffix>[\""'])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex QuotedNamedSecretPattern();

    [GeneratedRegex(@"(?<prefix>\b(?:password|passwd|pwd|access[_-]?token|refresh[_-]?token|auth[_-]?token|authorization|api[_-]?key|service[_-]?role[_-]?key|provider[_-]?(?:api[_-]?key|secret)|payment[_-]?(?:webhook[_-]?secret|secret)|webhook[_-]?secret|client[_-]?secret|signing[_-]?(?:key|secret|password)|stripe[_-]?signature)\s*[:=]\s*)(?<value>[^\s,;\]}]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex UnquotedNamedSecretPattern();

    [GeneratedRegex(@"\b(?:sk-(?:proj-)?[A-Za-z0-9_-]{20,}|sb_secret_[A-Za-z0-9_-]{16,}|whsec_[A-Za-z0-9_-]{16,}|(?:sk|rk)_live_[A-Za-z0-9]{16,}|gh[pousr]_[A-Za-z0-9]{30,}|AIza[A-Za-z0-9_-]{30,})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex KnownSecretPrefixPattern();

    [GeneratedRegex(@"\b(?:password|provider[_-]?secret|payment[_-]?secret|webhook[_-]?secret)[-_:][A-Za-z0-9._~+/=-]{4,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SecretLikeWordPattern();

    [GeneratedRegex(@"(?:^|&)(?:token|access_token|refresh_token|sig|signature|secret|key|api_key|x-amz-[^=]+|sv|se|sp|sr)=[^&]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SignedQueryMarkerPattern();
}
