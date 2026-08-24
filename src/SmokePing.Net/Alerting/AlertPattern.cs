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

        var gaps = _tokens.OfType<GapToken>().ToList();
        if (gaps.Count == 0)
        {
            return MatchesWith(history, []);
        }

        return SearchGaps(history, gaps, new int[gaps.Count], 0, 0);
    }

    /// <summary>
    /// Enumerates the gap lengths, reproducing the original's bounds exactly.
    ///
    /// Those bounds are narrower than the documentation suggests. The original's loop
    /// is <c>for(i=0; i &lt; min(maxlength - consumed, imax); i++)</c> with
    /// <c>imax = min(history - minlength, N)</c>: the comparison is strict, and the
    /// running total of earlier gaps is subtracted from a limit the loop bound did not
    /// include. So <c>*12*</c> between two tests over a fourteen-reading history
    /// tolerates six intervening readings, not twelve, and a gap pattern cannot match
    /// a history exactly as long as its own fixed tokens.
    ///
    /// That is odd, and it contradicts the original's own manual page, which says
    /// <c>*X*</c> ignores up to X values. It is reproduced deliberately: a rule carried
    /// over from an existing installation has to fire on the same rounds here, and
    /// alerting differently would be worse than alerting oddly.
    /// </summary>
    private bool SearchGaps(
        IReadOnlyList<Reading> history,
        IReadOnlyList<GapToken> gaps,
        int[] lengths,
        int gapIndex,
        int consumed)
    {
        if (gapIndex == gaps.Count)
        {
            return MatchesWith(history, lengths);
        }

        var available = Math.Min(history.Count - MinimumLength, gaps[gapIndex].MaxLength);
        var bound = Math.Min(MaximumLength - consumed, available);

        for (var length = 0; length < bound; length++)
        {
            // The original tests the same limit twice: once as the loop bound above,
            // and once afterwards with the gap length itself also subtracted. The
            // second test is the binding one, and it halves the usable gap.
            if (2 * length >= MaximumLength - consumed)
            {
                break;
            }

            lengths[gapIndex] = length;
            if (SearchGaps(history, gaps, lengths, gapIndex + 1, consumed + length))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tests the comparison tokens for one choice of gap lengths. The window is the
    /// tail of the history, so the last comparison token always lands on the newest
    /// reading.
    /// </summary>
    private bool MatchesWith(IReadOnlyList<Reading> history, IReadOnlyList<int> gapLengths)
    {
        var span = MinimumLength;
        foreach (var length in gapLengths)
        {
            span += length;
        }

        var index = history.Count - span;
        if (index < 0)
        {
            return false;
        }

        var gap = 0;

        foreach (var token in _tokens)
        {
            if (token is GapToken)
            {
                index += gapLengths[gap++];
                continue;
            }

            if (index >= history.Count || !token.Matches(history[index]))
            {
                return false;
            }

            index++;
        }

        return true;
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
