using System.Globalization;
using System.Text.RegularExpressions;

namespace SmokePing.Net.Alerting;

/// <summary>One reading in the history an alert pattern is matched against.</summary>
/// <param name="Value">The measured value, or null when there was no data ("U").</param>
/// <param name="IsStart">
/// True for the synthetic entry that marks "SmokePing had not been running yet" - the
/// value matched by the <c>S</c> token.
/// </param>
public readonly record struct Reading(double? Value, bool IsStart = false)
{
    public static readonly Reading Start = new(null, true);
}

/// <summary>
/// A compiled SmokePing alert detector pattern.
///
/// A pattern is a comma separated list of tokens matched against the most recent
/// readings, anchored so that the last token always tests the newest reading:
///
///   <c>&gt;20%,&gt;20%,&gt;20%</c>   three consecutive rounds above 20% loss
///   <c>&lt;10,&lt;10,&gt;100,&gt;100</c>  latency stepping up from below 10ms to above 100ms
///   <c>&gt;0%,*12*,&gt;0%,*12*,&gt;0%</c> loss three times, with up to 12 arbitrary rounds between
///   <c>==U</c>                 no data at all for the newest round
///   <c>==S</c>                 the target had not been measured before
///   <c>&gt;100&lt;200</c>            newest round between 100 and 200 (both conditions must hold)
///
/// Tokens:
///   <c>== != &lt; &gt; &lt;= &gt;=</c> followed by a number (loss patterns take a trailing <c>%</c>),
///   optionally followed by a second comparison to form a range;
///   <c>==*</c> matches any single reading;
///   <c>*N*</c> matches between zero and N arbitrary readings (a variable length gap);
///   <c>==U</c> / <c>!=U</c> test for missing data; <c>==S</c> tests for the start marker.
/// </summary>
public sealed class AlertPattern
{
    private static readonly Regex TokenPattern = new(
        @"^(==|!=|<=|>=|<|>|\*)(\d+(?:\.\d*)?|U|S|\d*\*)(%?)(?:(<=|>=|<|>)(\d+(?:\.\d*)?)(%?))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReadOnlyList<IToken> _tokens;

    private AlertPattern(IReadOnlyList<IToken> tokens, string source, bool isLoss)
    {
        _tokens = tokens;
        Source = source;
        IsLoss = isLoss;
        MinimumLength = tokens.Count(t => t is not GapToken);
        MaximumLength = MinimumLength + tokens.OfType<GapToken>().Sum(t => t.MaxLength);
    }

    /// <summary>The pattern text this was compiled from.</summary>
    public string Source { get; }

    /// <summary>True for loss patterns (values in percent), false for rtt patterns (milliseconds).</summary>
    public bool IsLoss { get; }

    /// <summary>Readings needed before the pattern can possibly match.</summary>
    public int MinimumLength { get; }

    /// <summary>Readings the pattern can consume at most.</summary>
    public int MaximumLength { get; }

    /// <summary>
    /// Compiles a pattern. <paramref name="type"/> must be "loss" or "rtt"; it decides
    /// whether a <c>%</c> suffix is required on numeric tokens, exactly as upstream does.
    /// </summary>
    /// <exception cref="FormatException">The pattern is not valid.</exception>
    public static AlertPattern Compile(string pattern, string type)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var isLoss = type.Equals("loss", StringComparison.OrdinalIgnoreCase);
        if (!isLoss && !type.Equals("rtt", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"Unknown alert type '{type}'. Expected 'loss' or 'rtt'.");
        }

        var parts = pattern.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        if (parts.Length == 0)
        {
            throw new FormatException("An alert pattern must contain at least one token.");
        }

        var tokens = new List<IToken>(parts.Length);
        foreach (var part in parts)
        {
            tokens.Add(ParseToken(part, isLoss));
        }

        if (tokens[^1] is GapToken)
        {
            throw new FormatException("An alert pattern must not end with a gap token.");
        }

        return new AlertPattern(tokens, pattern, isLoss);
    }

    private static IToken ParseToken(string token, bool isLoss)
    {
        var match = TokenPattern.Match(token);
        if (!match.Success)
        {
            throw new FormatException($"Alert pattern entry '{token}' is invalid.");
        }

        var op = match.Groups[1].Value;
        var value = match.Groups[2].Value;
        var percent = match.Groups[3].Value == "%";

        if (op == "*")
        {
            if (!value.EndsWith('*') || value.Length < 2 ||
                !int.TryParse(value[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) ||
                length < 1)
            {
                throw new FormatException($"The multi-match operator in '{token}' must be written as *N* with N >= 1.");
            }

            return new GapToken(length);
        }

        switch (value)
        {
            case "U":
                if (op is not ("==" or "!="))
                {
                    throw new FormatException($"Operator '{op}' cannot be used with U in '{token}'.");
                }

                return new UndefinedToken(Negated: op == "!=");

            case "S":
                if (op != "==")
                {
                    throw new FormatException($"S is only valid with the == operator in '{token}'.");
                }

                return new StartToken();

            case "*":
                if (op != "==")
                {
                    throw new FormatException($"Operator '{op}' makes no sense with * in '{token}'.");
                }

                return new AnyToken();
        }

        if (isLoss && !percent)
        {
            throw new FormatException($"Loss must be given in percent in '{token}'.");
        }

        if (!isLoss && percent)
        {
            throw new FormatException($"An rtt pattern takes milliseconds, not percent, in '{token}'.");
        }

        var primary = new Comparison(op, double.Parse(value, CultureInfo.InvariantCulture));
        Comparison? secondary = null;

        if (match.Groups[4].Success)
        {
            if (isLoss && match.Groups[6].Value != "%")
            {
                throw new FormatException($"Loss must be given in percent in '{token}'.");
            }

            if (!isLoss && match.Groups[6].Value == "%")
            {
                throw new FormatException($"An rtt pattern takes milliseconds, not percent, in '{token}'.");
            }

            secondary = new Comparison(
                match.Groups[4].Value,
                double.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture));
        }

        return new ValueToken(primary, secondary);
    }

    /// <summary>
    /// Matches the pattern against a history of readings, oldest first. The pattern is
    /// anchored at the newest reading.
    /// </summary>
    public bool Matches(IReadOnlyList<Reading> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (history.Count < MinimumLength)
        {
            return false;
        }

        // Walk both lists from the newest entry backwards; gap tokens try every
        // allowed length, which is the backtracking upstream expresses as nested loops.
        return MatchFrom(history, _tokens.Count - 1, history.Count - 1, MinimumLength);
    }

    private bool MatchFrom(IReadOnlyList<Reading> history, int tokenIndex, int readingIndex, int remainingFixed)
    {
        if (tokenIndex < 0)
        {
            return true;
        }

        var token = _tokens[tokenIndex];

        if (token is GapToken gap)
        {
            // A gap may consume no more readings than are left once the remaining
            // fixed tokens have taken their share.
            var available = readingIndex + 1 - remainingFixed;
            var maximum = Math.Min(gap.MaxLength, Math.Max(available, 0));
            for (var consumed = 0; consumed <= maximum; consumed++)
            {
                if (MatchFrom(history, tokenIndex - 1, readingIndex - consumed, remainingFixed))
                {
                    return true;
                }
            }

            return false;
        }

        if (readingIndex < 0)
        {
            return false;
        }

        return token.Matches(history[readingIndex]) &&
               MatchFrom(history, tokenIndex - 1, readingIndex - 1, remainingFixed - 1);
    }

    private interface IToken
    {
        bool Matches(Reading reading);
    }

    private sealed record GapToken(int MaxLength) : IToken
    {
        public bool Matches(Reading reading) => true;
    }

    private sealed record AnyToken : IToken
    {
        public bool Matches(Reading reading) => true;
    }

    private sealed record StartToken : IToken
    {
        public bool Matches(Reading reading) => reading.IsStart;
    }

    private sealed record UndefinedToken(bool Negated) : IToken
    {
        public bool Matches(Reading reading)
        {
            var undefined = reading.Value is null && !reading.IsStart;
            return Negated ? !undefined : undefined;
        }
    }

    private sealed record ValueToken(Comparison Primary, Comparison? Secondary) : IToken
    {
        public bool Matches(Reading reading)
        {
            if (reading.IsStart || reading.Value is not { } value)
            {
                return false;
            }

            return Primary.Matches(value) && (Secondary?.Matches(value) ?? true);
        }
    }

    private sealed record Comparison(string Operator, double Value)
    {
        public bool Matches(double actual) => Operator switch
        {
            "==" => Math.Abs(actual - Value) < 1e-9,
            "!=" => Math.Abs(actual - Value) >= 1e-9,
            "<" => actual < Value,
            ">" => actual > Value,
            "<=" => actual <= Value,
            ">=" => actual >= Value,
            _ => false,
        };
    }
}
