using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Google.Protobuf.Reflection;

namespace ApiTester.Grpc;

// Why this file exists: the canonical ProtoJSON mapping for google.protobuf.Timestamp, Duration, the
// wrapper types, Struct/Value/ListValue, FieldMask, and Any (protobuf.dev "ProtoJSON Format") is
// identical whichever direction you are converting, so both ProtoJsonReader and ProtoJsonWriter need
// the exact same canonical-format logic — duplicating it in each file would let the two forms drift
// apart, which would break round-tripping silently. This module is that single shared truth.
//
// Detection is by MessageDescriptor.FullName, never by ClrType. A descriptor built at runtime from
// server-reflection bytes (FileDescriptor.BuildFromByteStrings) has ClrType == null and no generated
// CLR type behind it at all — that absence is the whole reason ProtoJsonReader/Writer exist instead of
// Google.Protobuf's own JsonFormatter/JsonParser (see those files' header comments). A full type name
// is the only identity a runtime-built descriptor carries that survives the round trip through
// server reflection.

/// <summary>Which well-known-type conversion, if any, applies to a message. There is deliberately no
/// <c>Empty</c> member: google.protobuf.Empty needs no special case at all, because an ordinary empty
/// message already renders as <c>{}</c> and parses from <c>{}</c> through the existing message path —
/// adding an Empty case here would be a no-op branch pretending to be necessary. Do not "fix" this
/// omission; it is intentional.</summary>
internal enum WellKnownKind
{
    None,
    Timestamp,
    Duration,
    DoubleValue,
    FloatValue,
    Int64Value,
    UInt64Value,
    Int32Value,
    UInt32Value,
    BoolValue,
    StringValue,
    BytesValue,
    Struct,
    Value,
    ListValue,
    FieldMask,
    Any
}

internal static partial class WellKnownTypes
{
    // The exact bounds Google.Protobuf's own Timestamp type enforces: the inclusive range of
    // seconds-since-epoch spanning 0001-01-01T00:00:00Z to 9999-12-31T23:59:59Z (nanos carries the
    // remainder up to 999999999). Kept as named constants rather than computed from DateTime.MinValue/
    // MaxValue so the boundary is a plain, checkable number rather than a derived one.
    private const long MinTimestampSeconds = -62135596800L;
    private const long MaxTimestampSeconds = 253402300799L;

    // protobuf.dev: a Duration's magnitude is capped at 315576000000 seconds — roughly ten thousand
    // years — in both directions.
    private const long MaxDurationSeconds = 315576000000L;

    private static readonly Regex TimestampPattern = TimestampPatternRegex();
    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,9}))?(Z|z|[+-]\d{2}:\d{2})$", RegexOptions.None)]
    private static partial Regex TimestampPatternRegex();

    private static readonly Regex DurationPattern = DurationPatternRegex();
    [GeneratedRegex(@"^(-)?(\d+)(?:\.(\d{1,9}))?s$", RegexOptions.None)]
    private static partial Regex DurationPatternRegex();

    internal static WellKnownKind KindOf(MessageDescriptor descriptor) => descriptor.FullName switch
    {
        "google.protobuf.Timestamp" => WellKnownKind.Timestamp,
        "google.protobuf.Duration" => WellKnownKind.Duration,
        "google.protobuf.DoubleValue" => WellKnownKind.DoubleValue,
        "google.protobuf.FloatValue" => WellKnownKind.FloatValue,
        "google.protobuf.Int64Value" => WellKnownKind.Int64Value,
        "google.protobuf.UInt64Value" => WellKnownKind.UInt64Value,
        "google.protobuf.Int32Value" => WellKnownKind.Int32Value,
        "google.protobuf.UInt32Value" => WellKnownKind.UInt32Value,
        "google.protobuf.BoolValue" => WellKnownKind.BoolValue,
        "google.protobuf.StringValue" => WellKnownKind.StringValue,
        "google.protobuf.BytesValue" => WellKnownKind.BytesValue,
        "google.protobuf.Struct" => WellKnownKind.Struct,
        "google.protobuf.Value" => WellKnownKind.Value,
        "google.protobuf.ListValue" => WellKnownKind.ListValue,
        "google.protobuf.FieldMask" => WellKnownKind.FieldMask,
        "google.protobuf.Any" => WellKnownKind.Any,
        _ => WellKnownKind.None
    };

    internal static bool IsWellKnown(MessageDescriptor descriptor) => KindOf(descriptor) != WellKnownKind.None;

    /// <summary>Whether an Any payload of this type nests under "value" rather than having its fields
    /// inlined. Mirrors Google.Protobuf's MessageDescriptor.IsWellKnownType, which is broader than this
    /// file's own WellKnownKind — google.protobuf.Empty has no WellKnownKind (it needs no conversion)
    /// yet still nests, so IsWellKnown alone would be wrong here.</summary>
    internal static bool NestsUnderAnyValue(MessageDescriptor descriptor) =>
        descriptor.File.Package == "google.protobuf" && descriptor.File.Name is
            "google/protobuf/any.proto" or
            "google/protobuf/api.proto" or
            "google/protobuf/duration.proto" or
            "google/protobuf/empty.proto" or
            "google/protobuf/field_mask.proto" or
            "google/protobuf/source_context.proto" or
            "google/protobuf/struct.proto" or
            "google/protobuf/timestamp.proto" or
            "google/protobuf/type.proto" or
            "google/protobuf/wrappers.proto";

    /// <summary>Formats a Timestamp's seconds/nanos as canonical RFC 3339: always UTC "Z", with 0, 3, 6,
    /// or 9 fractional digits. Returns false — rather than throwing — when the value is not normalized
    /// (nanos outside [0, 999999999], or seconds outside the range Protobuf permits), so the read path
    /// can fall back to the ordinary {"seconds":…,"nanos":…} rendering instead of letting one malformed
    /// field sink an entire response.</summary>
    internal static bool TryFormatTimestamp(long seconds, int nanos, out string text)
    {
        text = "";
        if (nanos < 0 || nanos > 999999999) return false;
        if (seconds < MinTimestampSeconds || seconds > MaxTimestampSeconds) return false;

        DateTime instant = DateTime.UnixEpoch.AddSeconds(seconds);
        string datePart = instant.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", CultureInfo.InvariantCulture);
        text = $"{datePart}{FormatFractionalDigits(nanos)}Z";
        return true;
    }

    /// <summary>Parses RFC 3339 into seconds-since-epoch and nanos. Accepts a trailing "Z" or a numeric
    /// offset ("+01:00", "-05:00"), which is converted to UTC, and 1–9 fractional digits. Returns false
    /// for anything else; the caller turns that into a GrpcJsonException naming the field.</summary>
    internal static bool TryParseTimestamp(string text, out long seconds, out int nanos)
    {
        seconds = 0;
        nanos = 0;

        var match = TimestampPattern.Match(text);
        if (!match.Success) return false;

        int year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        int day = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        int hour = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
        int minute = int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);
        int second = int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture);
        string fractionalGroup = match.Groups[7].Value;
        string offsetGroup = match.Groups[8].Value;

        DateTime instant;
        try
        {
            instant = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Rejects a calendrically impossible date (e.g. day 31 of a 30-day month) rather than
            // letting DateTime's own constructor exception escape as an unhandled crash.
            return false;
        }

        long offsetSeconds = 0;
        if (offsetGroup is not ("Z" or "z"))
        {
            int sign = offsetGroup[0] == '-' ? -1 : 1;
            int offsetHours = int.Parse(offsetGroup.AsSpan(1, 2), CultureInfo.InvariantCulture);
            int offsetMinutes = int.Parse(offsetGroup.AsSpan(4, 2), CultureInfo.InvariantCulture);
            offsetSeconds = sign * (offsetHours * 3600L + offsetMinutes * 60L);
        }

        // Computed from ticks rather than via DateTime subtraction, so an offset near the 0001/9999
        // boundary cannot overflow DateTime's own range before the seconds bound below rejects it.
        long rawSeconds = (instant.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond;
        long candidateSeconds = rawSeconds - offsetSeconds;
        if (candidateSeconds < MinTimestampSeconds || candidateSeconds > MaxTimestampSeconds) return false;

        seconds = candidateSeconds;
        nanos = int.Parse(fractionalGroup.PadRight(9, '0'), CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Formats a Duration's seconds/nanos as the canonical seconds-with-"s" form, e.g.
    /// "1.500s", "-3s", "-0.500s". Seconds and nanos must carry the same sign and |seconds| must be
    /// within 315576000000; returns false otherwise, for the same fall-back-rather-than-fail reason as
    /// <see cref="TryFormatTimestamp"/>.</summary>
    internal static bool TryFormatDuration(long seconds, int nanos, out string text)
    {
        text = "";
        if (seconds < -MaxDurationSeconds || seconds > MaxDurationSeconds) return false;
        if (nanos <= -1_000_000_000 || nanos >= 1_000_000_000) return false;
        if (seconds > 0 && nanos < 0) return false;
        if (seconds < 0 && nanos > 0) return false;

        bool negative = seconds < 0 || nanos < 0;
        long absSeconds = Math.Abs(seconds);
        int absNanos = Math.Abs(nanos);

        text = $"{(negative ? "-" : "")}{absSeconds.ToString(CultureInfo.InvariantCulture)}{FormatFractionalDigits(absNanos)}s";
        return true;
    }

    /// <summary>Parses the seconds-with-"s" form into seconds and nanos, both carrying the sign. Returns
    /// false for anything else.</summary>
    internal static bool TryParseDuration(string text, out long seconds, out int nanos)
    {
        seconds = 0;
        nanos = 0;

        var match = DurationPattern.Match(text);
        if (!match.Success) return false;

        bool negative = match.Groups[1].Success;
        if (!long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long absSeconds))
            return false;
        if (absSeconds > MaxDurationSeconds) return false;

        int absNanos = int.Parse(match.Groups[3].Value.PadRight(9, '0'), CultureInfo.InvariantCulture);

        // A negative duration below one second in magnitude ("-0.500s") must still carry its sign on
        // nanos even though seconds itself is zero — negating 0 gives 0, so the sign would otherwise be
        // lost entirely, the classic bug this format invites.
        seconds = negative ? -absSeconds : absSeconds;
        nanos = negative ? -absNanos : absNanos;
        return true;
    }

    /// <summary>0, 3, 6, or 9 fractional digits: 0 when <paramref name="nanos"/> is zero, 3 when it is a
    /// whole millisecond, 6 when a whole microsecond, 9 otherwise. Shared by Timestamp and Duration
    /// formatting alike; the caller passes an already-non-negative magnitude.</summary>
    private static string FormatFractionalDigits(int nanos)
    {
        if (nanos == 0) return "";
        if (nanos % 1_000_000 == 0) return "." + (nanos / 1_000_000).ToString("D3", CultureInfo.InvariantCulture);
        if (nanos % 1_000 == 0) return "." + (nanos / 1_000).ToString("D6", CultureInfo.InvariantCulture);
        return "." + nanos.ToString("D9", CultureInfo.InvariantCulture);
    }

    private static readonly char[] FieldMaskPathSeparator = { ',' };

    /// <summary>Joins wire paths into the canonical comma-separated lowerCamelCase JSON form. Returns false when
    /// any path cannot be represented that way, so the read path falls back to the ordinary
    /// {"paths":[…]} rendering rather than letting one malformed mask sink an entire response.</summary>
    internal static bool TryFormatFieldMask(IEnumerable<string> paths, out string text)
    {
        text = "";
        var converted = new List<string>();
        foreach (string path in paths)
        {
            if (!IsFieldMaskPathValidForJson(path)) return false;
            converted.Add(FieldMaskPathToJsonName(path));
        }
        text = string.Join(",", converted);
        return true;
    }

    /// <summary>Mirrors Google.Protobuf's own FieldMask.IsPathValid: an uppercase letter anywhere makes a
    /// path unrepresentable in JSON, and a literal '_' is only legal when it is not the path's very last
    /// character and is immediately followed by a lowercase letter — a trailing underscore alone is left
    /// alone (Google's own check skips it too), because there is nothing after it that could disambiguate
    /// what the underscore "means".</summary>
    private static bool IsFieldMaskPathValidForJson(string path)
    {
        for (int i = 0; i < path.Length; i++)
        {
            char c = path[i];
            if (c >= 'A' && c <= 'Z') return false;
            if (c == '_' && i < path.Length - 1)
            {
                char next = path[i + 1];
                if (next < 'a' || next > 'z') return false;
            }
        }
        return true;
    }

    /// <summary>Ported from Google.Protobuf's own JsonFormatter.ToJsonName, run across the WHOLE path
    /// string (dots included — a '.' is just an ordinary character to this transform, so it does not need
    /// splitting first): drop each '_' and uppercase the character that follows.</summary>
    private static string FieldMaskPathToJsonName(string path)
    {
        var builder = new StringBuilder(path.Length);
        bool upperNext = false;
        foreach (char c in path)
        {
            if (c == '_') upperNext = true;
            else if (upperNext) { builder.Append(char.ToUpperInvariant(c)); upperNext = false; }
            else builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>Splits the canonical JSON form back into wire (snake_case) paths. Returns false for input that
    /// cannot be converted; the caller turns that into a GrpcJsonException naming the field.</summary>
    internal static bool TryParseFieldMask(string text, out IReadOnlyList<string> paths)
    {
        paths = Array.Empty<string>();
        // Matches Google's own JsonParser.MergeFieldMask: a comma-separated piece that is empty (from
        // "", a leading/trailing comma, or "a,,b") is dropped rather than kept as a literal empty path —
        // confirmed differentially in ProtoJsonTests against JsonParser.Default.Parse, not assumed.
        string[] pieces = text.Split(FieldMaskPathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var converted = new List<string>(pieces.Length);
        foreach (string piece in pieces)
        {
            if (!TryConvertFieldMaskPieceToSnakeCase(piece, out string snake)) return false;
            converted.Add(snake);
        }
        paths = converted;
        return true;
    }

    /// <summary>Ported from Google.Protobuf's own JsonParser.ToSnakeCase (itself ported from
    /// src/google/protobuf/util/internal/utility.cc), because the obvious "insert '_' before every
    /// uppercase letter" rule disagrees with Google for a run of two or more consecutive uppercase
    /// letters: "userID" becomes "user_id", not "user_i_d" — a run of capitals collapses to a single
    /// leading underscore, confirmed differentially against JsonParser.Default.Parse. A literal '_'
    /// anywhere in the JSON-form path is invalid (Google's own parser throws
    /// InvalidProtocolBufferException for it); this returns false instead, for the same
    /// fall-back/caller-throws split every other Try* helper in this file uses.</summary>
    private static bool TryConvertFieldMaskPieceToSnakeCase(string text, out string snake)
    {
        var builder = new StringBuilder(text.Length * 2);
        bool wasNotUnderscore = false;
        bool wasNotCap = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= 'A' && c <= 'Z')
            {
                // An underscore precedes this uppercase letter unless: (1) it is the very first
                // character of the piece, or (2) it is itself immediately preceded by another
                // uppercase letter AND immediately followed by yet another uppercase letter (or is the
                // last character) — i.e. it is a non-final member of a run of capitals, which Google
                // folds into the single underscore that opened the run.
                if (wasNotUnderscore && (wasNotCap || (i + 1 < text.Length && text[i + 1] >= 'a' && text[i + 1] <= 'z')))
                {
                    builder.Append('_');
                }
                builder.Append((char)(c + 'a' - 'A'));
                wasNotUnderscore = true;
                wasNotCap = false;
            }
            else
            {
                if (c == '_')
                {
                    snake = "";
                    return false;
                }
                builder.Append(c);
                wasNotUnderscore = true;
                wasNotCap = true;
            }
        }
        snake = builder.ToString();
        return true;
    }
}
