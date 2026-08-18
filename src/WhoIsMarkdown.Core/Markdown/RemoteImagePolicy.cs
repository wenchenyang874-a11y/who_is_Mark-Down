using System.Globalization;
using System.Text.RegularExpressions;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Applies the user's explicit remote-image trust choice. Rules are compiled from
/// bounded, typed text entries; regex evaluation has a short timeout so a malformed
/// expression cannot stall WIMD's real-time preview pipeline.
/// </summary>
public sealed class RemoteImagePolicy
{
    public const int MaximumRuleCount = 100;
    public const int MaximumRuleLength = 512;

    public static RemoteImagePolicy BlockAll { get; } = new(
        RemoteImageTrustMode.BlockAll,
        []);

    private readonly CompiledRule[] compiledRules;

    public RemoteImagePolicy(RemoteImageTrustMode mode, IEnumerable<string>? rules)
    {
        Mode = Enum.IsDefined(mode) ? mode : RemoteImageTrustMode.BlockAll;
        Rules = NormalizeRules(rules);
        compiledRules = Rules.Select(CompiledRule.Create).ToArray();
    }

    public RemoteImageTrustMode Mode { get; }

    public IReadOnlyList<string> Rules { get; }

    public string Identity => $"{(int)Mode}:" + string.Join("|", Rules.Select(rule => $"{rule.Length}:{rule}"));

    public bool Allows(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        bool matched = compiledRules.Any(rule => rule.Matches(uri));
        return Mode switch
        {
            RemoteImageTrustMode.AllowList => matched,
            RemoteImageTrustMode.BlockList => !matched,
            RemoteImageTrustMode.TrustAll => true,
            _ => false,
        };
    }

    public IReadOnlyList<string> GetContentSecurityPolicySources()
    {
        if (Mode == RemoteImageTrustMode.AllowList && compiledRules.Length > 0
            && compiledRules.All(rule => rule.Kind == RuleKind.Domain))
        {
            return compiledRules
                .Select(rule => $"https://{rule.Value}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return Mode switch
        {
            RemoteImageTrustMode.AllowList when compiledRules.Length > 0 => ["https:"],
            RemoteImageTrustMode.BlockList or RemoteImageTrustMode.TrustAll => ["https:"],
            _ => [],
        };
    }

    public static IReadOnlyList<string> NormalizeRules(IEnumerable<string>? rules)
    {
        List<string> normalized = [];
        HashSet<string> knownRules = new(StringComparer.OrdinalIgnoreCase);
        foreach (string rawRule in rules ?? [])
        {
            string candidate = rawRule.Trim();
            if (candidate.Length == 0)
            {
                continue;
            }

            if (normalized.Count >= MaximumRuleCount)
            {
                throw new ArgumentException($"远程图片规则不能超过 {MaximumRuleCount} 条。", nameof(rules));
            }

            CompiledRule compiled = CompiledRule.Create(candidate);
            string canonicalRule = $"{compiled.KindName}:{compiled.Value}";
            if (knownRules.Add(canonicalRule))
            {
                normalized.Add(canonicalRule);
            }
        }

        return normalized;
    }

    private enum RuleKind
    {
        Domain,
        Prefix,
        Suffix,
        Keyword,
        Regex,
    }

    private sealed class CompiledRule
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

        private CompiledRule(RuleKind kind, string value, Regex? regex = null)
        {
            Kind = kind;
            Value = value;
            this.regex = regex;
        }

        private readonly Regex? regex;

        public RuleKind Kind { get; }

        public string KindName => Kind.ToString().ToLowerInvariant();

        public string Value { get; }

        public static CompiledRule Create(string rule)
        {
            if (rule.IndexOfAny(['\r', '\n', '\0']) >= 0)
            {
                throw new ArgumentException("远程图片规则不能包含换行或空字符。", nameof(rule));
            }

            if (rule.Length > MaximumRuleLength)
            {
                throw new ArgumentException(
                    $"单条远程图片规则不能超过 {MaximumRuleLength} 个字符。",
                    nameof(rule));
            }

            int separator = rule.IndexOf(':');
            string kindText = separator > 0 ? rule[..separator].Trim() : "domain";
            string value = (separator > 0 ? rule[(separator + 1)..] : rule).Trim();
            if (value.Length == 0)
            {
                throw new ArgumentException("远程图片规则内容不能为空。", nameof(rule));
            }

            RuleKind kind = kindText.ToLowerInvariant() switch
            {
                "domain" => RuleKind.Domain,
                "prefix" => RuleKind.Prefix,
                "suffix" => RuleKind.Suffix,
                "keyword" => RuleKind.Keyword,
                "regex" => RuleKind.Regex,
                _ => throw new ArgumentException(
                    $"不支持远程图片规则类型“{kindText}”。",
                    nameof(rule)),
            };

            if (kind == RuleKind.Domain)
            {
                return new CompiledRule(kind, NormalizeDomain(value));
            }

            if (kind == RuleKind.Regex)
            {
                try
                {
                    Regex regex = new(
                        value,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        RegexTimeout);
                    return new CompiledRule(kind, value, regex);
                }
                catch (ArgumentException exception)
                {
                    throw new ArgumentException(
                        $"远程图片正则表达式无效：{exception.Message}",
                        nameof(rule),
                        exception);
                }
            }

            return new CompiledRule(kind, value);
        }

        public bool Matches(Uri uri)
        {
            string absoluteUrl = uri.AbsoluteUri;
            return Kind switch
            {
                RuleKind.Domain => uri.IdnHost.Equals(Value, StringComparison.OrdinalIgnoreCase),
                RuleKind.Prefix => absoluteUrl.StartsWith(Value, StringComparison.OrdinalIgnoreCase),
                RuleKind.Suffix => absoluteUrl.EndsWith(Value, StringComparison.OrdinalIgnoreCase),
                RuleKind.Keyword => absoluteUrl.Contains(Value, StringComparison.OrdinalIgnoreCase),
                RuleKind.Regex => IsRegexMatch(absoluteUrl),
                _ => false,
            };
        }

        private bool IsRegexMatch(string value)
        {
            try
            {
                return regex?.IsMatch(value) == true;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        private static string NormalizeDomain(string value)
        {
            string candidate = value.Trim().TrimEnd('.');
            if (candidate.Contains("://", StringComparison.Ordinal)
                || candidate.Contains('/')
                || candidate.Contains('\\')
                || candidate.Contains(':')
                || candidate.Contains('*'))
            {
                throw new ArgumentException($"远程图片域名“{value}”格式无效；请只填写域名。", nameof(value));
            }

            try
            {
                string ascii = new IdnMapping().GetAscii(candidate).ToLowerInvariant();
                if (Uri.CheckHostName(ascii) != UriHostNameType.Dns)
                {
                    throw new ArgumentException($"远程图片域名“{value}”格式无效。", nameof(value));
                }

                return ascii;
            }
            catch (ArgumentException exception) when (exception.ParamName != nameof(value))
            {
                throw new ArgumentException($"远程图片域名“{value}”格式无效。", nameof(value), exception);
            }
        }
    }
}
