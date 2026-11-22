using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ApiTester.Grpc;

// Why this file exists instead of Google.Protobuf.JsonFormatter: JsonFormatter needs an IMessage
// instance backed by a CLR type, and descriptors built at runtime via FileDescriptor.BuildFromByteStrings
// have ClrType == null and no Parser — there is no generated code for them. Google.Protobuf for C#,
// unlike the Java and Go runtimes, has no DynamicMessage that could stand in. So this project converts
// JSON<->Protobuf itself, descriptor-driven, on top of Google.Protobuf's own CodedInputStream /
// CodedOutputStream: the risky part (varints, zigzag, fixed widths, length delimiters) stays the
// library's code, and what we own here is only the field-mapping layer. Do not "fix" this back to
// JsonFormatter; it cannot work against runtime-built descriptors.
//
// Coverage: every scalar, message, repeated (packed and unpacked), map, enum, and oneof field kind
// proto3 JSON defines, following the canonical mapping JsonFormatter implements. Proven differentially
// in ProtoJsonTests against Google's own JsonParser/JsonFormatter over generated test types built from
// the same .proto — that comparison is what justifies owning this layer at all.

/// <summary>Renders a Protobuf payload as proto3-canonical JSON.</summary>
internal static class ProtoJsonReader
{
    // Governs how deep a message may nest before this converter refuses to recurse further. Decode
    // (see its own depth parameter below) applies this to reading a wire message, so a payload
    // cannot recurse without limit. It also governs the RENDER path's own Any chain, via the depth
    // check in TryWriteAny: rendering an ordinary nested message costs no extra depth (that
    // structure was already produced by a Decode call this same cap already bounded), but a
    // google.protobuf.Any is the one shape rendering can expand by calling Decode again, on fresh
    // bytes, from a fresh call with no depth of its own — an Any wrapping an Any wrapping an Any...
    // would otherwise recurse on the render side with nothing at all limiting it.
    private const int MaxDepth = 100;

    /// <summary>A Protobuf payload rendered as proto3-canonical JSON. A field absent from the wire is
    /// simply omitted from the output — proto3 default-omission, for free. Fields are emitted in
    /// ascending field-number order regardless of the order they arrived on the wire, so output is
    /// deterministic.
    /// <para><paramref name="resolveMessage"/> resolves a google.protobuf.Any's type URL to the
    /// MessageDescriptor naming its payload, so the Any can be expanded rather than rendered as raw
    /// bytes. Null — the default — means no Any in this payload can be expanded; every Any then
    /// renders as the "@type"/base64-"value" fallback (see <see cref="TryWriteWellKnown"/>), which is
    /// always a legal rendering, never a failure.</para></summary>
    internal static string ToJson(MessageDescriptor descriptor, byte[] payload, bool indented,
        Func<string, MessageDescriptor?>? resolveMessage = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
        {
            WriteObject(writer, descriptor, Decode(descriptor, payload, depth: 0), depth: 0, resolveMessage);
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static Dictionary<int, object> Decode(MessageDescriptor descriptor, byte[] payload, int depth)
    {
        using var input = new CodedInputStream(payload);
        return Decode(descriptor, input, depth);
    }

    private static Dictionary<int, object> Decode(MessageDescriptor descriptor, ByteString payload, int depth)
    {
        using var input = payload.CreateCodedInput();
        return Decode(descriptor, input, depth);
    }

    private static Dictionary<int, object> Decode(MessageDescriptor descriptor, CodedInputStream input, int depth)
    {
        if (depth > MaxDepth)
            throw new GrpcJsonException($"'{descriptor.FullName}': message nesting exceeds the maximum supported depth ({MaxDepth}).");

        var values = new Dictionary<int, object>();

        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            int fieldNumber = WireFormat.GetTagFieldNumber(tag);
            var field = descriptor.FindFieldByNumber(fieldNumber);
            if (field is null)
            {
                // A server may legitimately send a field newer than the descriptor we fetched.
                input.SkipLastField();
                continue;
            }

            if (field.FieldType == FieldType.Group)
            {
                throw new GrpcJsonException(
                    $"Field '{field.JsonName}': the proto2 'group' wire type is not supported (proto3 cannot express it).");
            }

            if (field.IsMap)
            {
                var (key, entryValue) = DecodeMapEntry(field, input.ReadBytes(), depth);
                if (!values.TryGetValue(fieldNumber, out var existingMap))
                {
                    existingMap = new Dictionary<string, object>();
                    values[fieldNumber] = existingMap;
                }
                ((Dictionary<string, object>)existingMap)[key] = entryValue;
                continue;
            }

            if (field.IsRepeated)
            {
                if (!values.TryGetValue(fieldNumber, out var existingList))
                {
                    existingList = new List<object>();
                    values[fieldNumber] = existingList;
                }
                var list = (List<object>)existingList;

                // A packable field may legitimately arrive packed (one length-delimited run) or
                // unpacked (one tag per element); a sender is free to choose either.
                if (IsPackable(field.FieldType) && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
                {
                    list.AddRange(ReadPackedRun(input, field));
                }
                else
                {
                    list.Add(ReadFieldValue(input, field, depth));
                }
                continue;
            }

            // A field belonging to a non-synthetic oneof clears any previously-read member of the
            // same group: the last one on the wire is the one that emits.
            if (field.RealContainingOneof is { } oneof)
            {
                foreach (var sibling in oneof.Fields) values.Remove(sibling.FieldNumber);
            }

            values[fieldNumber] = ReadFieldValue(input, field, depth);
        }

        return values;
    }

    /// <summary>Reads one value already known to belong to <paramref name="field"/> — a singular
    /// field, one element of an unpacked repeated field, or a map's key/value field.</summary>
    private static object ReadFieldValue(CodedInputStream input, FieldDescriptor field, int depth) => field.FieldType switch
    {
        FieldType.Double => input.ReadDouble(),
        FieldType.Float => input.ReadFloat(),
        FieldType.Int32 => input.ReadInt32(),
        FieldType.Int64 => input.ReadInt64(),
        FieldType.UInt32 => input.ReadUInt32(),
        FieldType.UInt64 => input.ReadUInt64(),
        FieldType.SInt32 => input.ReadSInt32(),
        FieldType.SInt64 => input.ReadSInt64(),
        FieldType.Fixed32 => input.ReadFixed32(),
        FieldType.Fixed64 => input.ReadFixed64(),
        FieldType.SFixed32 => input.ReadSFixed32(),
        FieldType.SFixed64 => input.ReadSFixed64(),
        FieldType.Bool => input.ReadBool(),
        FieldType.String => input.ReadString(),
        FieldType.Bytes => input.ReadBytes(),
        FieldType.Enum => input.ReadEnum(),
        FieldType.Message => Decode(field.MessageType, input.ReadBytes(), depth + 1),
        FieldType.Group => throw new GrpcJsonException(
            $"Field '{field.JsonName}': the proto2 'group' wire type is not supported (proto3 cannot express it)."),
        _ => throw new GrpcJsonException($"Field '{field.JsonName}' has an unrecognized Protobuf field type ({field.FieldType}).")
    };

    /// <summary>Reads a packed run: a single length-delimited blob holding the concatenated raw values
    /// of a packable repeated field, with no per-element tags. Uses a fresh <see cref="CodedInputStream"/>
    /// over the extracted bytes and <see cref="CodedInputStream.IsAtEnd"/> to know when the run is
    /// exhausted, since <c>PushLimit</c>/<c>PopLimit</c> are not accessible here.</summary>
    private static List<object> ReadPackedRun(CodedInputStream outer, FieldDescriptor field)
    {
        var items = new List<object>();
        using var inner = outer.ReadBytes().CreateCodedInput();
        while (!inner.IsAtEnd)
        {
            // Packable types are all scalar (never Message), so depth cannot matter here.
            items.Add(ReadFieldValue(inner, field, depth: 0));
        }
        return items;
    }

    /// <summary>Decodes one map entry submessage (field 1 = key, field 2 = value) into the entry's
    /// JSON property name and the value's natural representation. A key or value never present on the
    /// wire (because it held its type's default) is reconstructed as that default — proto3's
    /// default-omission applies inside a map entry exactly as it does everywhere else, but a map entry
    /// must still produce a concrete key and value.</summary>
    private static (string Key, object Value) DecodeMapEntry(FieldDescriptor mapField, ByteString entryBytes, int depth)
    {
        var keyField = mapField.MessageType.FindFieldByNumber(1);
        var valueField = mapField.MessageType.FindFieldByNumber(2);
        object? keyValue = null;
        object? valueValue = null;

        using var input = entryBytes.CreateCodedInput();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            int number = WireFormat.GetTagFieldNumber(tag);
            if (number == 1) keyValue = ReadFieldValue(input, keyField, depth);
            else if (number == 2) valueValue = ReadFieldValue(input, valueField, depth + 1);
            else input.SkipLastField();
        }

        keyValue ??= DefaultValueFor(keyField);
        valueValue ??= DefaultValueFor(valueField);

        return (StringifyKey(keyField, keyValue), valueValue);
    }

    private static object DefaultValueFor(FieldDescriptor field) => field.FieldType switch
    {
        FieldType.Double => 0d,
        FieldType.Float => 0f,
        FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => 0,
        FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => 0L,
        FieldType.UInt32 or FieldType.Fixed32 => 0u,
        FieldType.UInt64 or FieldType.Fixed64 => 0ul,
        FieldType.Bool => false,
        FieldType.String => "",
        FieldType.Bytes => ByteString.Empty,
        FieldType.Enum => 0,
        FieldType.Message => new Dictionary<int, object>(),
        _ => throw new GrpcJsonException($"Field '{field.JsonName}' has an unrecognized Protobuf field type ({field.FieldType}).")
    };

    private static string StringifyKey(FieldDescriptor keyField, object keyValue) => keyField.FieldType switch
    {
        FieldType.String => (string)keyValue,
        FieldType.Bool => (bool)keyValue ? "true" : "false",
        _ => Convert.ToString(keyValue, CultureInfo.InvariantCulture) ?? ""
    };

    private static void WriteObject(Utf8JsonWriter writer, MessageDescriptor descriptor, Dictionary<int, object> values,
        int depth, Func<string, MessageDescriptor?>? resolveMessage)
    {
        var kind = WellKnownTypes.KindOf(descriptor);
        if (kind != WellKnownKind.None && TryWriteWellKnown(writer, descriptor, kind, values, depth, resolveMessage)) return;

        writer.WriteStartObject();
        WriteMessageFields(writer, descriptor, values, depth, resolveMessage);
        writer.WriteEndObject();
    }

    /// <summary>Writes an ordinary message's properties in field-number order, WITHOUT the enclosing
    /// braces. Factored out of <see cref="WriteObject"/> so the Any branch of
    /// <see cref="TryWriteWellKnown"/> can inline a resolved payload's fields directly alongside its
    /// own "@type" property, sharing this loop rather than duplicating it.</summary>
    private static void WriteMessageFields(Utf8JsonWriter writer, MessageDescriptor descriptor, Dictionary<int, object> values,
        int depth, Func<string, MessageDescriptor?>? resolveMessage)
    {
        foreach (var field in descriptor.Fields.InFieldNumberOrder())
        {
            if (values.TryGetValue(field.FieldNumber, out var value)) WriteFieldValue(writer, field, value, depth, resolveMessage);
        }
    }

    /// <summary>Renders a well-known-type message in its canonical JSON form. Returns false when the
    /// value cannot be rendered that way (a non-normalized Timestamp/Duration, a FieldMask path that
    /// cannot be expressed as lowerCamelCase, or an Any missing its type_url), so <see cref="WriteObject"/>
    /// falls through to the ordinary rendering instead — a malformed value from a server must not sink
    /// the whole response. Timestamp, Duration, the nine wrapper types, Struct/Value/ListValue,
    /// FieldMask, and Any are all handled here. google.protobuf.Empty needs no case at all — see the
    /// comment on <see cref="WellKnownKind"/>.</summary>
    private static bool TryWriteWellKnown(Utf8JsonWriter writer, MessageDescriptor descriptor, WellKnownKind kind, Dictionary<int, object> values,
        int depth, Func<string, MessageDescriptor?>? resolveMessage)
    {
        switch (kind)
        {
            case WellKnownKind.Timestamp:
            {
                if (!TryReadSecondsNanos(descriptor, values, out long seconds, out int nanos)) return false;
                if (!WellKnownTypes.TryFormatTimestamp(seconds, nanos, out string text)) return false;
                writer.WriteStringValue(text);
                return true;
            }
            case WellKnownKind.Duration:
            {
                if (!TryReadSecondsNanos(descriptor, values, out long seconds, out int nanos)) return false;
                if (!WellKnownTypes.TryFormatDuration(seconds, nanos, out string text)) return false;
                writer.WriteStringValue(text);
                return true;
            }
            // A wrapper's canonical JSON form is its single "value" field's bare value — no enclosing
            // object — and every one of the nine types shares that identical shape, so one branch
            // handles all of them via the existing scalar-rendering machinery below.
            case WellKnownKind.DoubleValue or WellKnownKind.FloatValue or WellKnownKind.Int64Value
                or WellKnownKind.UInt64Value or WellKnownKind.Int32Value or WellKnownKind.UInt32Value
                or WellKnownKind.BoolValue or WellKnownKind.StringValue or WellKnownKind.BytesValue:
            {
                var valueField = descriptor.FindFieldByName("value");
                if (valueField is null) return false;
                // proto3 omits a field holding its type's default, so a wrapper carrying (say) Int32
                // 0 arrives here as a zero-length submessage with nothing in `values` — it must still
                // render as the bare default (0), never as {}, which is exactly what DefaultValueFor
                // supplies and what distinguishes a present-but-default wrapper from an absent one.
                object inner = values.TryGetValue(valueField.FieldNumber, out var found)
                    ? found
                    : DefaultValueFor(valueField);
                WriteScalarOrMessage(writer, valueField, inner, depth, resolveMessage);
                return true;
            }
            // A Struct's canonical JSON form is a plain object: one property per entry of the
            // "fields" map<string, Value>. Every lookup is by name off the descriptor, so a
            // malformed descriptor degrades to `false` (ordinary rendering) rather than crashing.
            case WellKnownKind.Struct:
            {
                var fieldsField = descriptor.FindFieldByName("fields");
                if (fieldsField is null) return false;
                var entryValueField = fieldsField.MessageType.FindFieldByNumber(2);
                if (entryValueField is null) return false;

                writer.WriteStartObject();
                if (values.TryGetValue(fieldsField.FieldNumber, out var fieldsValue))
                {
                    foreach (var entry in (Dictionary<string, object>)fieldsValue)
                    {
                        writer.WritePropertyName(entry.Key);
                        // Recursing through WriteObject — rather than duplicating the Value case's
                        // rendering here — is deliberate: WriteObject is the seam that dispatches by
                        // descriptor kind, so this re-enters the Value case below with no logic
                        // duplicated between Struct, Value, and ListValue. depth passes through
                        // unchanged: this structure was already produced by a Decode call the
                        // decode-side MaxDepth already bounded, so re-rendering it costs no extra depth.
                        WriteObject(writer, entryValueField.MessageType, (Dictionary<int, object>)entry.Value, depth, resolveMessage);
                    }
                }
                writer.WriteEndObject();
                return true;
            }
            // A ListValue's canonical JSON form is a plain array: one element per entry of the
            // repeated "values" field.
            case WellKnownKind.ListValue:
            {
                var valuesField = descriptor.FindFieldByName("values");
                if (valuesField is null) return false;

                writer.WriteStartArray();
                if (values.TryGetValue(valuesField.FieldNumber, out var listValues))
                {
                    foreach (var item in (List<object>)listValues)
                    {
                        WriteObject(writer, valuesField.MessageType, (Dictionary<int, object>)item, depth, resolveMessage);
                    }
                }
                writer.WriteEndArray();
                return true;
            }
            // A Value is whatever JSON value its single set oneof member represents. Exactly one of
            // the six members is ever present on the wire at a time (it is a oneof); scan them in
            // field-number order and render whichever is present.
            case WellKnownKind.Value:
            {
                var nullValueField = descriptor.FindFieldByName("null_value");
                var numberValueField = descriptor.FindFieldByName("number_value");
                var stringValueField = descriptor.FindFieldByName("string_value");
                var boolValueField = descriptor.FindFieldByName("bool_value");
                var structValueField = descriptor.FindFieldByName("struct_value");
                var listValueField = descriptor.FindFieldByName("list_value");
                if (nullValueField is null || numberValueField is null || stringValueField is null ||
                    boolValueField is null || structValueField is null || listValueField is null)
                {
                    return false;
                }

                if (values.ContainsKey(nullValueField.FieldNumber)) { writer.WriteNullValue(); return true; }
                if (values.TryGetValue(numberValueField.FieldNumber, out var numberValue))
                {
                    // Routed through the shared WriteDoubleValue helper so a non-finite number_value
                    // follows the exact same "NaN"/"Infinity"/"-Infinity" convention as every other
                    // double this converter renders, rather than a second, divergent implementation.
                    WriteDoubleValue(writer, (double)numberValue);
                    return true;
                }
                if (values.TryGetValue(stringValueField.FieldNumber, out var stringValue))
                {
                    writer.WriteStringValue((string)stringValue);
                    return true;
                }
                if (values.TryGetValue(boolValueField.FieldNumber, out var boolValue))
                {
                    writer.WriteBooleanValue((bool)boolValue);
                    return true;
                }
                if (values.TryGetValue(structValueField.FieldNumber, out var structValue))
                {
                    WriteObject(writer, structValueField.MessageType, (Dictionary<int, object>)structValue, depth, resolveMessage);
                    return true;
                }
                if (values.TryGetValue(listValueField.FieldNumber, out var listValueValue))
                {
                    WriteObject(writer, listValueField.MessageType, (Dictionary<int, object>)listValueValue, depth, resolveMessage);
                    return true;
                }

                // None of the six oneof members is present: the server sent a genuinely empty Value
                // submessage. That is itself a legal (if unusual) way to encode null — it must render
                // as JSON null rather than being silently dropped, which is what would happen if this
                // fell through to the ordinary-message rendering (an empty {} for a message with no
                // fields set).
                writer.WriteNullValue();
                return true;
            }
            // A FieldMask's canonical JSON form is its wire (snake_case) paths, joined by commas into
            // ONE string, each converted to lowerCamelCase. A mask with zero paths is not merely "empty
            // object" or omitted — proto3 omits the empty repeated "paths" field from the wire entirely,
            // so it never appears in `values` at all — but Google's own JsonFormatter still renders it
            // as the empty string, not {} and not nothing, so that is treated as the (valid) empty list
            // rather than as "field absent".
            case WellKnownKind.FieldMask:
            {
                var pathsField = descriptor.FindFieldByName("paths");
                if (pathsField is null) return false;

                var paths = values.TryGetValue(pathsField.FieldNumber, out var pathsValue)
                    ? ((List<object>)pathsValue).Cast<string>()
                    : Enumerable.Empty<string>();
                if (!WellKnownTypes.TryFormatFieldMask(paths, out string text)) return false;

                writer.WriteStringValue(text);
                return true;
            }
            // An Any's canonical JSON form is best-effort by design (ruling 4 of this release): when
            // the type URL resolves and the payload bytes decode cleanly it expands to
            // {"@type":…,<payload fields>} (or {"@type":…,"value":<payload>} for a nested well-known
            // payload — see NestsUnderAnyValue); otherwise it degrades to the visible
            // {"@type":…,"value":"<base64>"} fallback rather than throwing, so one unresolvable Any
            // never sinks the rest of the response.
            case WellKnownKind.Any:
                return TryWriteAny(writer, descriptor, values, depth, resolveMessage);
            default:
                return false;
        }
    }

    /// <summary>Expands a google.protobuf.Any. Looking up "type_url" and "value" BY NAME (never by
    /// hard-coded field number) means a differently-shaped Any-like descriptor degrades to `false`
    /// (ordinary rendering) rather than crashing. An absent or empty type_url returns false too — an
    /// empty Any is honestly rendered as the ordinary {} an empty message is, rather than a
    /// meaningless fallback. Everything past that point is best-effort (ruling 4): a null resolver, an
    /// unresolved type name, or payload bytes that do not decode against the resolved descriptor all
    /// degrade to the same visible "@type"/base64-"value" fallback rather than throwing — deliberately
    /// "@type" rather than the ordinary rendering's "typeUrl", so a script can always read "@type"
    /// regardless of whether expansion succeeded, and the base64 "value" keeps the payload visible
    /// instead of discarding it. That shape also round-trips symmetrically back through
    /// ProtoJsonWriter's own EncodeAny (see AcceptsJsonObject there).</summary>
    private static bool TryWriteAny(Utf8JsonWriter writer, MessageDescriptor descriptor, Dictionary<int, object> values,
        int depth, Func<string, MessageDescriptor?>? resolveMessage)
    {
        var typeUrlField = descriptor.FindFieldByName("type_url");
        var valueField = descriptor.FindFieldByName("value");
        if (typeUrlField is null) return false;
        if (!values.TryGetValue(typeUrlField.FieldNumber, out var typeUrlValue) || (string)typeUrlValue is not { Length: > 0 } typeUrl)
        {
            return false;
        }

        ByteString payloadBytes = valueField is not null && values.TryGetValue(valueField.FieldNumber, out var rawValue)
            ? (ByteString)rawValue
            : ByteString.Empty;

        int lastSlash = typeUrl.LastIndexOf('/');
        string typeName = lastSlash >= 0 ? typeUrl[(lastSlash + 1)..] : typeUrl;

        void WriteFallback()
        {
            writer.WriteStartObject();
            writer.WriteString("@type", typeUrl);
            writer.WriteString("value", payloadBytes.ToBase64());
            writer.WriteEndObject();
        }

        var payloadDescriptor = resolveMessage?.Invoke(typeName);
        if (payloadDescriptor is null)
        {
            WriteFallback();
            return true;
        }

        // Every other well-known shape (Struct, Value, ListValue, the wrappers) only re-renders a
        // Dictionary<int, object> that Decode already produced elsewhere, so recursing through
        // WriteObject for one of those costs no extra depth — Decode's own MaxDepth check already
        // bounded that structure when it was first read off the wire. An Any is different: its
        // payload is still raw bytes at this point, and expanding it means calling Decode again,
        // fresh, on a brand-new message, from a render call that carries no depth limit of its own.
        // That makes Any the one construct that lets rendering re-enter decoding, and so the one
        // place the render path can recurse without bound — an Any wrapping an Any wrapping an
        // Any... — unless this call is checked against the same cap Decode already enforces. This
        // matters because every level of Any expansion opens one JSON object, so JSON nesting depth
        // tracks render recursion 1:1: left unchecked, a long enough Any-of-Any chain does not blow
        // the native stack first — it escapes ToJson as an uncaught InvalidOperationException from
        // Utf8JsonWriter's own structural nesting cap (1000), which would fail the whole call and is
        // exactly the outcome ruling 4 (Any must never fail a call) exists to rule out. So this
        // degrades to the same visible fallback as an unresolved Any instead, well before that cap
        // is ever reached.
        if (depth >= MaxDepth)
        {
            WriteFallback();
            return true;
        }

        try
        {
            var payloadValues = Decode(payloadDescriptor, payloadBytes, depth + 1);
            writer.WriteStartObject();
            writer.WriteString("@type", typeUrl);
            if (WellKnownTypes.NestsUnderAnyValue(payloadDescriptor))
            {
                writer.WritePropertyName("value");
                WriteObject(writer, payloadDescriptor, payloadValues, depth + 1, resolveMessage);
            }
            else
            {
                WriteMessageFields(writer, payloadDescriptor, payloadValues, depth + 1, resolveMessage);
            }
            writer.WriteEndObject();
            return true;
        }
        catch (Exception ex) when (ex is GrpcJsonException or InvalidProtocolBufferException)
        {
            // The payload bytes do not match the shape the type URL claims (a corrupt message, or a
            // server bug pairing the wrong type URL with a value) — degrade to the visible fallback
            // rather than letting one bad Any sink the rest of the response (ruling 4).
            WriteFallback();
            return true;
        }
    }

    /// <summary>Reads Timestamp/Duration's shared seconds/nanos shape out of the decoded field map, by
    /// field name rather than hard-coded field numbers 1 and 2 (both types happen to use those numbers,
    /// but looking them up keeps this independent of that coincidence). A field absent from
    /// <paramref name="values"/> held its type's default and was omitted on the wire — proto3
    /// default-omission — so it is treated as 0, not as missing: the Unix epoch itself transmits as
    /// zero bytes and must still render as a timestamp string, not as {}.</summary>
    private static bool TryReadSecondsNanos(MessageDescriptor descriptor, Dictionary<int, object> values, out long seconds, out int nanos)
    {
        seconds = 0;
        nanos = 0;
        var secondsField = descriptor.FindFieldByName("seconds");
        var nanosField = descriptor.FindFieldByName("nanos");
        if (secondsField is null || nanosField is null) return false;

        if (values.TryGetValue(secondsField.FieldNumber, out var secondsValue)) seconds = (long)secondsValue;
        if (values.TryGetValue(nanosField.FieldNumber, out var nanosValue)) nanos = (int)nanosValue;
        return true;
    }

    private static void WriteFieldValue(Utf8JsonWriter writer, FieldDescriptor field, object value,
        int depth, Func<string, MessageDescriptor?>? resolveMessage)
    {
        if (field.IsMap)
        {
            var map = (Dictionary<string, object>)value;
            if (map.Count == 0) return; // omitted entirely when empty
            var valueField = field.MessageType.FindFieldByNumber(2);
            writer.WritePropertyName(field.JsonName);
            writer.WriteStartObject();
            foreach (var entry in map)
            {
                writer.WritePropertyName(entry.Key);
                WriteScalarOrMessage(writer, valueField, entry.Value, depth, resolveMessage);
            }
            writer.WriteEndObject();
            return;
        }

        if (field.IsRepeated)
        {
            var list = (List<object>)value;
            if (list.Count == 0) return; // omitted entirely when empty
            writer.WritePropertyName(field.JsonName);
            writer.WriteStartArray();
            foreach (var item in list) WriteScalarOrMessage(writer, field, item, depth, resolveMessage);
            writer.WriteEndArray();
            return;
        }

        writer.WritePropertyName(field.JsonName);
        WriteScalarOrMessage(writer, field, value, depth, resolveMessage);
    }

    private static void WriteScalarOrMessage(Utf8JsonWriter writer, FieldDescriptor field, object value,
        int depth, Func<string, MessageDescriptor?>? resolveMessage)
    {
        switch (field.FieldType)
        {
            case FieldType.Double: WriteDoubleValue(writer, (double)value); break;
            case FieldType.Float: WriteFloatValue(writer, (float)value); break;
            case FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32:
                writer.WriteNumberValue((int)value);
                break;
            case FieldType.UInt32 or FieldType.Fixed32:
                writer.WriteNumberValue((uint)value);
                break;
            // A 64-bit integer is rendered as a JSON string: it does not survive a JavaScript number.
            case FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64:
                writer.WriteStringValue(((long)value).ToString(CultureInfo.InvariantCulture));
                break;
            case FieldType.UInt64 or FieldType.Fixed64:
                writer.WriteStringValue(((ulong)value).ToString(CultureInfo.InvariantCulture));
                break;
            case FieldType.Bool:
                writer.WriteBooleanValue((bool)value);
                break;
            case FieldType.String:
                writer.WriteStringValue((string)value);
                break;
            case FieldType.Bytes:
                writer.WriteStringValue(((ByteString)value).ToBase64());
                break;
            case FieldType.Enum:
                int number = (int)value;
                var enumValue = field.EnumType.FindValueByNumber(number);
                // The wire may carry a number the descriptor does not name (a value added to the
                // enum after this client fetched its descriptor); render the bare number rather than
                // inventing a name or failing.
                if (enumValue is null) writer.WriteNumberValue(number);
                else writer.WriteStringValue(enumValue.Name);
                break;
            case FieldType.Message:
                WriteObject(writer, field.MessageType, (Dictionary<int, object>)value, depth, resolveMessage);
                break;
            default:
                throw new GrpcJsonException(
                    $"Field '{field.JsonName}' has an unrecognized Protobuf field type ({field.FieldType}) that this build cannot render.");
        }
    }

    private static void WriteDoubleValue(Utf8JsonWriter writer, double value)
    {
        if (double.IsNaN(value)) writer.WriteStringValue("NaN");
        else if (double.IsPositiveInfinity(value)) writer.WriteStringValue("Infinity");
        else if (double.IsNegativeInfinity(value)) writer.WriteStringValue("-Infinity");
        else writer.WriteNumberValue(value);
    }

    private static void WriteFloatValue(Utf8JsonWriter writer, float value)
    {
        if (float.IsNaN(value)) writer.WriteStringValue("NaN");
        else if (float.IsPositiveInfinity(value)) writer.WriteStringValue("Infinity");
        else if (float.IsNegativeInfinity(value)) writer.WriteStringValue("-Infinity");
        else writer.WriteNumberValue(value);
    }

    /// <summary>The packable Protobuf types: every numeric type plus bool and enum. string, bytes,
    /// message, and group are never packed regardless of repetition.</summary>
    private static bool IsPackable(FieldType type) => type switch
    {
        FieldType.String or FieldType.Bytes or FieldType.Message or FieldType.Group => false,
        _ => true
    };
}
