using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ApiTester.Grpc;

// Why this file exists instead of Google.Protobuf.JsonParser: JsonParser needs an IMessage instance
// backed by a CLR type, and descriptors built at runtime via FileDescriptor.BuildFromByteStrings have
// ClrType == null and no Parser — there is no generated code for them. Google.Protobuf for C#, unlike
// the Java and Go runtimes, has no DynamicMessage that could stand in. So this project converts
// JSON<->Protobuf itself, descriptor-driven, on top of Google.Protobuf's own CodedInputStream /
// CodedOutputStream: the risky part (varints, zigzag, fixed widths, length delimiters) stays the
// library's code, and what we own here is only the field-mapping layer. Do not "fix" this back to
// JsonParser; it cannot work against runtime-built descriptors.
//
// Coverage: every scalar, message, repeated (packed and unpacked), map, enum, and oneof field kind
// proto3 JSON defines, following the canonical mapping JsonParser implements. Proven differentially in
// ProtoJsonTests against Google's own JsonParser/JsonFormatter over generated test types built from the
// same .proto — that comparison is what justifies owning this layer at all.

/// <summary>Encodes a JSON object to the Protobuf wire form of a descriptor, following the canonical
/// proto3 JSON mapping.</summary>
internal static class ProtoJsonWriter
{
    private const int MaxDepth = 100;

    // System.Text.Json's own JsonDocument caps object/array nesting at 64 by default — below our own
    // message-nesting cap — so a request that is deep but still within MaxDepth would otherwise fail
    // during parsing itself, with a raw, unhelpful System.Text.Json.JsonException, before Encode's own
    // depth check ever runs. Raised generously past MaxDepth so our own check — reported as a clear
    // GrpcJsonException naming the field — is what actually governs.
    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = MaxDepth * 4 };

    /// <summary>A JSON object encoded as the Protobuf wire form of <paramref name="descriptor"/>. An
    /// empty or whitespace-only <paramref name="json"/> is treated as <c>{}</c> (an empty message):
    /// plenty of methods take no arguments, and forcing callers to pass an explicit empty object would
    /// be noise.
    /// <para><paramref name="resolveMessage"/> resolves a google.protobuf.Any's "@type" to the
    /// MessageDescriptor naming its payload, so the payload can be encoded properly rather than only
    /// accepted as raw base64. Null — the default — means an Any can only be supplied via its
    /// "@type"/base64-"value" fallback shape or the ordinary "typeUrl"/"value" shape; see
    /// <see cref="EncodeAny"/>.</para></summary>
    internal static byte[] ToProtobuf(MessageDescriptor descriptor, string json, Func<string, MessageDescriptor?>? resolveMessage = null)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json, ParseOptions);
        return Encode(descriptor, document.RootElement, path: "", depth: 0, resolveMessage);
    }

    private static byte[] Encode(MessageDescriptor descriptor, JsonElement element, string path, int depth,
        Func<string, MessageDescriptor?>? resolveMessage)
    {
        if (depth > MaxDepth)
            throw new GrpcJsonException($"'{Label(path, descriptor)}': message nesting exceeds the maximum supported depth ({MaxDepth}).");

        // The general rule, frozen for backward compatibility: when the incoming JSON element for a
        // well-known type IS an object, fall through to the ordinary message path below unchanged —
        // every script already sending today's {"seconds":5,"nanos":0} shape keeps working, and this
        // costs nothing for Timestamp, Duration, and the nine wrappers because none of their canonical
        // forms is a JSON object. Struct and Value are the two exceptions — a plain JSON object IS
        // their canonical form — so AcceptsJsonObject below routes an object element for exactly those
        // two kinds to EncodeWellKnown instead of letting it fall through. ListValue keeps the old
        // rule (its canonical form is an array, so an object still means "ordinary message" for it).
        // Any is a third exception, but only conditionally: an object carrying an "@type" property is
        // routed to EncodeWellKnown/EncodeAny, while an object without one keeps meaning "the ordinary
        // typeUrl/value shape" for backward compatibility — see AcceptsJsonObject.
        var kind = WellKnownTypes.KindOf(descriptor);
        if (kind != WellKnownKind.None && (element.ValueKind != JsonValueKind.Object || AcceptsJsonObject(kind, element)))
            return EncodeWellKnown(descriptor, kind, element, path, depth, resolveMessage);

        return EncodeOrdinaryMessage(descriptor, element, path, depth, resolveMessage);
    }

    /// <summary>True for the well-known kinds whose canonical JSON form is itself a plain object —
    /// Struct and Value — so an object-shaped JSON element must still route to
    /// <see cref="EncodeWellKnown"/> rather than falling through to the ordinary message path. This is
    /// an unavoidable behavior change: <c>{"fStruct":{"fields":{...}}}</c> is now read as a Struct
    /// containing a key literally named "fields", not as today's ordinary-message shape, because a
    /// Struct with arbitrary keys cannot be distinguished from a hand-shaped wrapper object — that is
    /// exactly what Google's own JsonParser does too. ListValue is deliberately excluded: its canonical
    /// form is a JSON array, not an object. Any routes to <see cref="EncodeWellKnown"/> only when the
    /// object carries an "@type" property: that discriminator is what keeps today's
    /// <c>{"typeUrl":"…","value":"&lt;base64&gt;"}</c> shape working through the ordinary message path
    /// unchanged — the same backward-compatibility rule this method already applies to Struct and
    /// Value.</summary>
    private static bool AcceptsJsonObject(WellKnownKind kind, JsonElement element) => kind switch
    {
        WellKnownKind.Struct or WellKnownKind.Value => true,
        WellKnownKind.Any => element.TryGetProperty("@type", out _),
        _ => false
    };

    private static byte[] EncodeOrdinaryMessage(MessageDescriptor descriptor, JsonElement element, string path, int depth,
        Func<string, MessageDescriptor?>? resolveMessage, string? ignoreProperty = null)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new GrpcJsonException(
                $"'{Label(path, descriptor)}': expected a JSON object for message '{descriptor.FullName}', found {Describe(element.ValueKind)}.");
        }

        // Track JSON property names in source order so an unrecognized property can be named
        // deterministically, and matched properties are struck off as fields consume them.
        // ignoreProperty is never added at all: it exists so EncodeAny's inline case can pass the
        // whole Any element straight through (its payload's own fields sit alongside "@type" in the
        // same JSON object) without "@type" itself being reported as an unrecognized field.
        var propertyOrder = new List<string>();
        var remaining = new HashSet<string>(StringComparer.Ordinal);
        var propertiesByName = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name == ignoreProperty) continue;
            propertyOrder.Add(property.Name);
            remaining.Add(property.Name);
            propertiesByName[property.Name] = property.Value;
        }

        using var buffer = new MemoryStream();
        using var output = new CodedOutputStream(buffer, leaveOpen: true);

        // A non-synthetic oneof only allows one member set at a time; track which member (if any)
        // this message has already seen so a second one can be reported by name.
        Dictionary<OneofDescriptor, string>? setOneofFields = null;

        foreach (var field in descriptor.Fields.InFieldNumberOrder())
        {
            if (!TryGetField(propertiesByName, field, out var value, out var matchedName)) continue;
            remaining.Remove(matchedName);

            // A JSON null ordinarily means "field absent", per proto3 JSON mapping — except for a
            // singular google.protobuf.Value field, where JSON null IS a legal value (NullValue), not
            // an absence: Google's own JsonParser sets null_value for {"fValue":null}, and its
            // JsonFormatter renders such a message back as "fValue":null, so skipping here would
            // silently drop a real field the differential tests would catch.
            bool isSingularValueField = !field.IsMap && !field.IsRepeated
                && field.FieldType == FieldType.Message
                && WellKnownTypes.KindOf(field.MessageType) == WellKnownKind.Value;
            if (value.ValueKind == JsonValueKind.Null && !isSingularValueField) continue;

            string fieldPath = string.IsNullOrEmpty(path) ? field.JsonName : $"{path}.{field.JsonName}";

            if (field.RealContainingOneof is { } oneof)
            {
                setOneofFields ??= new Dictionary<OneofDescriptor, string>();
                if (setOneofFields.TryGetValue(oneof, out var earlier))
                {
                    throw new GrpcJsonException(
                        $"'{Label(path, descriptor)}': fields '{earlier}' and '{field.JsonName}' are both set, but only one member of oneof '{oneof.Name}' may be present.");
                }
                setOneofFields[oneof] = field.JsonName;
            }

            WriteField(output, field, value, fieldPath, depth, resolveMessage);
        }

        output.Flush();

        if (remaining.Count > 0)
        {
            string offending = propertyOrder.First(remaining.Contains);
            throw new GrpcJsonException(
                $"Unrecognized field '{(string.IsNullOrEmpty(path) ? offending : $"{path}.{offending}")}' for message '{descriptor.FullName}'.");
        }

        return buffer.ToArray();
    }

    /// <summary>Encodes a well-known-type message from its canonical JSON form. Reached when
    /// <paramref name="element"/> is NOT a JSON object (see the comment at the top of <see cref="Encode"/>)
    /// for Timestamp, Duration, the nine wrapper types, and FieldMask, and also — because of
    /// <see cref="AcceptsJsonObject"/> — when it IS an object for Struct, Value, and an Any carrying
    /// "@type". Every other kind falls back to <see cref="EncodeOrdinaryMessage"/>, which throws its
    /// existing "expected a JSON object" error for ListValue given a non-array (ListValue's canonical
    /// form is an array, so it only ever reaches here as a non-object), and also handles an Any
    /// WITHOUT "@type" (today's <c>{"typeUrl":"…","value":"…"}</c> shape) via that same ordinary
    /// path.</summary>
    private static byte[] EncodeWellKnown(MessageDescriptor descriptor, WellKnownKind kind, JsonElement element, string path, int depth,
        Func<string, MessageDescriptor?>? resolveMessage) => kind switch
    {
        WellKnownKind.Timestamp => EncodeTimestamp(descriptor, element, path),
        WellKnownKind.Duration => EncodeDuration(descriptor, element, path),
        WellKnownKind.DoubleValue or WellKnownKind.FloatValue or WellKnownKind.Int64Value
            or WellKnownKind.UInt64Value or WellKnownKind.Int32Value or WellKnownKind.UInt32Value
            or WellKnownKind.BoolValue or WellKnownKind.StringValue or WellKnownKind.BytesValue
            => EncodeWrapper(descriptor, element, path, depth, resolveMessage),
        WellKnownKind.Struct => EncodeStruct(descriptor, element, path, depth, resolveMessage),
        WellKnownKind.ListValue => EncodeListValue(descriptor, element, path, depth, resolveMessage),
        WellKnownKind.Value => EncodeValue(descriptor, element, path, depth, resolveMessage),
        WellKnownKind.FieldMask => EncodeFieldMask(descriptor, element, path, depth, resolveMessage),
        WellKnownKind.Any => EncodeAny(descriptor, element, path, depth, resolveMessage),
        _ => EncodeOrdinaryMessage(descriptor, element, path, depth, resolveMessage)
    };

    /// <summary>Encodes a wrapper type (DoubleValue, FloatValue, Int64Value, UInt64Value, Int32Value,
    /// UInt32Value, BoolValue, StringValue, BytesValue) from its canonical bare-value JSON form. A JSON
    /// null here means the wrapper itself is absent/default — it can only reach this point at the root
    /// or inside a repeated/map element, since the ordinary field loop already skips a null property —
    /// so it encodes as zero bytes exactly like an unset field. Otherwise the inner value is written via
    /// the existing <see cref="WriteSingularValue"/>, which reuses every scalar rule this converter
    /// already implements (the int64-as-string convention, base64 for bytes, and so on) and already
    /// throws a GrpcJsonException naming the field for a type mismatch. The inner value is written even
    /// when it equals the field's default: Google's own serializer omits a default-valued singular
    /// scalar, so our bytes differ from Google's for e.g. Int32Value{0}, but that is fine — an explicit
    /// default is legal on the wire and parses back identically, which is what the differential test
    /// checks (parsed messages, not raw bytes). Do not "fix" this into a default-omission branch; doing
    /// so would not change correctness but would only add complexity for no benefit.</summary>
    private static byte[] EncodeWrapper(MessageDescriptor descriptor, JsonElement element, string path, int depth,
        Func<string, MessageDescriptor?>? resolveMessage)
    {
        var valueField = descriptor.FindFieldByName("value");
        if (valueField is null) return EncodeOrdinaryMessage(descriptor, element, path, depth, resolveMessage);
        if (element.ValueKind == JsonValueKind.Null) return Array.Empty<byte>();

        using var buffer = new MemoryStream();
        using (var output = new CodedOutputStream(buffer, leaveOpen: true))
        {
            WriteSingularValue(output, valueField, element, path, depth, resolveMessage);
            output.Flush();
        }
        return buffer.ToArray();
    }

    /// <summary>Encodes a google.protobuf.Struct from its canonical plain-JSON-object form: each
    /// property becomes one entry of the "fields" map&lt;string, Value&gt;, key in the entry
    /// submessage's field 1 and the rendered Value in field 2 — the same map-entry shape
    /// <see cref="WriteMapField"/> builds, with one deliberate difference: WriteMapField skips a
    /// property whose value is JSON null (that is proto3's absent-field convention for an ordinary
    /// map), but a Struct entry explicitly set to null is a real NullValue, not an absent field, so it
    /// must be encoded rather than dropped — this is why Struct gets its own loop instead of reusing
    /// WriteMapField outright.</summary>
    private static byte[] EncodeStruct(MessageDescriptor descriptor, JsonElement element, string path, int depth,
        Func<string, MessageDescriptor?>? resolveMessage)
    {
        var fieldsField = descriptor.FindFieldByName("fields");
        if (fieldsField is null) return EncodeOrdinaryMessage(descriptor, element, path, depth, resolveMessage);

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new GrpcJsonException(
                $"'{Label(path, descriptor)}': expected a JSON object for message '{descriptor.FullName}' (google.protobuf.Struct expects a JSON object).");
        }

        var keyField = fieldsField.MessageType.FindFieldByNumber(1);
        var entryValueField = fieldsField.MessageType.FindFieldByNumber(2);
        if (keyField is null || entryValueField is null) return EncodeOrdinaryMessage(descriptor, element, path, depth, resolveMessage);

        using var buffer = new MemoryStream();
        using (var output = new CodedOutputStream(buffer, leaveOpen: true))
        {
            foreach (var property in element.EnumerateObject())
            {
                string entryPath = $"{path}.{property.Name}";
                // Routed through the main Encode(...) — rather than a dedicated Value encoder call —
                // so a maliciously deep struct hits Encode's own MaxDepth check and fails with the
                // converter's normal clear depth error instead of blowing the stack.
                byte[] entryValueBytes = Encode(entryValueField.MessageType, property.Value, entryPath, depth + 1, resolveMessage);

                using var entryBuffer = new MemoryStream();
                using (var entryOutput = new CodedOutputStream(entryBuffer, leaveOpen: true))
                {
                    entryOutput.WriteTag(keyField.FieldNumber, WireFormat.WireType.LengthDelimited);
                    // A Struct's map key is always a string per struct.proto, so it is written
                    // directly rather than dispatched through WriteSingularValue's generic switch.
                    entryOutput.WriteString(property.Name);
                    entryOutput.WriteTag(entryValueField.FieldNumber, WireFormat.WireType.LengthDelimited);
                    entryOutput.WriteBytes(ByteString.CopyFrom(entryValueBytes));
                    entryOutput.Flush();
                }

                output.WriteTag(fieldsField.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(entryBuffer.ToArray()));
            }
            output.Flush();
        }
        return buffer.ToArray();
    }

    /// <summary>Encodes a google.protobuf.ListValue from its canonical JSON-array form: each element
    /// becomes one entry of the repeated "values" field (each a Value submessage).</summary>
    private static byte[] EncodeListValue(MessageDescriptor descriptor, JsonElement element, string path, int depth,
        Func<string, MessageDescriptor?>? resolveMessage)
    {
        var valuesField = descriptor.FindFieldByName("values");
        if (valuesField is null) return EncodeOrdinaryMessage(descriptor, element, path, depth, resolveMessage);

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new GrpcJsonException(
                $"'{Label(path, descriptor)}': expected a JSON array for message '{descriptor.FullName}' (google.protobuf.ListValue expects a JSON array).");
        }

        using var buffer = new MemoryStream();
        using (var output = new CodedOutputStream(buffer, leaveOpen: true))
        {
            int index = 0;
            foreach (var item in element.EnumerateArray())
            {
                // Routed through the main Encode(...), for the same depth-cap reason as EncodeStruct
                // above.
                byte[] itemBytes = Encode(valuesField.MessageType, item, $"{path}[{index}]", depth + 1, resolveMessage);
                output.WriteTag(valuesField.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(itemBytes));
                index++;
            }
            output.Flush();
        }
        return buffer.ToArray();
    }

    /// <summary>Encodes a google.protobuf.Value from whatever JSON value it holds, by writing
    /// whichever of the six oneof members corresponds to <paramref name="element"/>'s JSON kind. Every
    /// branch writes its tag even when the value equals that member's type default — <c>false</c>,
    /// <c>0</c>, <c>""</c>, and null_value's own <c>0</c> included. That is deliberate, not an
    /// oversight: these six fields are oneof members, and a oneof member has explicit wire presence —
    /// proto3's usual "omit a default-valued singular field" rule governs an ordinary field, not which
    /// oneof member is set. Omitting bool_value for a JSON <c>false</c>, for instance, would produce a
    /// zero-length Value submessage — indistinguishable on the wire from an unset Value — which the
    /// read side would then render back as <c>null</c> instead of <c>false</c>.</summary>
    private static byte[] EncodeValue(MessageDescriptor descriptor, JsonElement element, string path, int depth,
        Func<string, MessageDescriptor?>? resolveMessage)
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
            return EncodeOrdinaryMessage(descriptor, element, path, depth, resolveMessage);
        }

        using var buffer = new MemoryStream();
        using (var output = new CodedOutputStream(buffer, leaveOpen: true))
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Null:
                    output.WriteTag(nullValueField.FieldNumber, WireFormat.WireType.Varint);
                    output.WriteEnum(0);
                    break;
                case JsonValueKind.Number:
                    output.WriteTag(numberValueField.FieldNumber, WireFormat.WireType.Fixed64);
                    output.WriteDouble(element.GetDouble());
                    break;
                case JsonValueKind.String:
                    output.WriteTag(stringValueField.FieldNumber, WireFormat.WireType.LengthDelimited);
                    output.WriteString(element.GetString()!);
                    break;
                case JsonValueKind.True or JsonValueKind.False:
                    output.WriteTag(boolValueField.FieldNumber, WireFormat.WireType.Varint);
                    output.WriteBool(element.GetBoolean());
                    break;
                case JsonValueKind.Object:
                {
                    // Routed through the main Encode(...), for the same depth-cap reason as
                    // EncodeStruct above — this is also what re-enters EncodeStruct itself for a
                    // nested object with no logic duplicated between Value and Struct.
                    byte[] structBytes = Encode(structValueField.MessageType, element, path, depth + 1, resolveMessage);
                    output.WriteTag(structValueField.FieldNumber, WireFormat.WireType.LengthDelimited);
                    output.WriteBytes(ByteString.CopyFrom(structBytes));
                    break;
                }
                case JsonValueKind.Array:
                {
                    byte[] listBytes = Encode(listValueField.MessageType, element, path, depth + 1, resolveMessage);
                    output.WriteTag(listValueField.FieldNumber, WireFormat.WireType.LengthDelimited);
                    output.WriteBytes(ByteString.CopyFrom(listBytes));
                    break;
                }
                default:
                    throw new GrpcJsonException(
                        $"'{Label(path, descriptor)}': unsupported JSON value kind ({Describe(element.ValueKind)}) for google.protobuf.Value.");
            }
            output.Flush();
        }
        return buffer.ToArray();
    }

    /// <summary>Encodes a google.protobuf.FieldMask from its canonical comma-separated lowerCamelCase
    /// string form: each comma-separated piece is converted to a wire (snake_case) path and written as
    /// one element of the repeated "paths" field. "paths" is a repeated string — never packable — so
    /// each path gets its own tag + length-delimited value; a mask with zero paths (the canonical string
    /// "") therefore encodes to zero bytes, exactly like an unset field.</summary>
    private static byte[] EncodeFieldMask(MessageDescriptor descriptor, JsonElement element, string path, int depth,
        Func<string, MessageDescriptor?>? resolveMessage)
    {
        var pathsField = descriptor.FindFieldByName("paths");
        if (pathsField is null) return EncodeOrdinaryMessage(descriptor, element, path, depth, resolveMessage);

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new GrpcJsonException(
                $"'{Label(path, descriptor)}': expected a comma-separated string for message '{descriptor.FullName}' (google.protobuf.FieldMask expects e.g. \"user.displayName,photo\"), found {Describe(element.ValueKind)}.");
        }

        string raw = element.GetString()!;
        if (!WellKnownTypes.TryParseFieldMask(raw, out IReadOnlyList<string> paths))
        {
            throw new GrpcJsonException(
                $"'{Label(path, descriptor)}': '{raw}' is not a valid field mask (google.protobuf.FieldMask expects e.g. \"user.displayName,photo\").");
        }

        using var buffer = new MemoryStream();
        using (var output = new CodedOutputStream(buffer, leaveOpen: true))
        {
            foreach (string p in paths)
            {
                output.WriteTag(pathsField.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteString(p);
            }
            output.Flush();
        }
        return buffer.ToArray();
    }

    /// <summary>Encodes a google.protobuf.Any carrying an "@type" property (see
    /// <see cref="AcceptsJsonObject"/> for the shape without one, which stays on the ordinary message
    /// path unchanged). The asymmetry with the read side is deliberate: a failed resolve here is NOT
    /// silently swallowed into garbage — it still encodes faithfully, as the type URL plus whatever
    /// raw bytes the caller supplied via "value" — because that is the correct best-effort behavior for
    /// a caller who already knows the payload's bytes but whose client cannot resolve the type name.
    /// But a "value" that is present and is not valid base64 in that unresolved case IS a caller
    /// mistake (not a server-side ambiguity to be tolerant of) and must throw a GrpcJsonException naming
    /// the field, per this converter's usual data-error rule.</summary>
    private static byte[] EncodeAny(MessageDescriptor descriptor, JsonElement element, string path, int depth,
        Func<string, MessageDescriptor?>? resolveMessage)
    {
        if (!element.TryGetProperty("@type", out var typeProperty) || typeProperty.ValueKind != JsonValueKind.String)
        {
            throw new GrpcJsonException(
                $"'{path}.@type': expected a string for property '@type' of message '{descriptor.FullName}' (google.protobuf.Any expects e.g. {{\"@type\":\"type.googleapis.com/google.protobuf.Timestamp\",\"value\":\"...\"}}).");
        }
        string typeUrl = typeProperty.GetString()!;

        var typeUrlField = descriptor.FindFieldByName("type_url");
        var valueField = descriptor.FindFieldByName("value");
        if (typeUrlField is null || valueField is null)
            return EncodeOrdinaryMessage(descriptor, element, path, depth, resolveMessage);

        int lastSlash = typeUrl.LastIndexOf('/');
        string typeName = lastSlash >= 0 ? typeUrl[(lastSlash + 1)..] : typeUrl;
        var payloadDescriptor = resolveMessage?.Invoke(typeName);

        byte[] payloadBytes;
        if (payloadDescriptor is not null && WellKnownTypes.NestsUnderAnyValue(payloadDescriptor))
        {
            // Absent "value" means an empty payload (Timestamp's own epoch default, Empty's only
            // possible value, and so on) rather than an error: the same proto3 default-omission
            // convention every other well-known type in this converter already follows.
            payloadBytes = element.TryGetProperty("value", out var valueElement)
                ? Encode(payloadDescriptor, valueElement, $"{path}.value", depth + 1, resolveMessage)
                : Array.Empty<byte>();
        }
        else if (payloadDescriptor is not null)
        {
            // The payload's own fields sit inline, alongside "@type", in this same JSON object — so
            // the whole element is re-encoded against the payload's descriptor, with "@type" excluded
            // from the unrecognized-field check rather than split into a synthetic sub-object first.
            payloadBytes = EncodeOrdinaryMessage(payloadDescriptor, element, path, depth + 1, resolveMessage, ignoreProperty: "@type");
        }
        else
        {
            // Best-effort, ruling 4: the type name did not resolve (no resolver at all, or the
            // resolver does not know it), so the payload can only be accepted as opaque bytes — which
            // is exactly the shape TryWriteAny's own fallback rendering produces, making this the
            // other half of that round trip.
            payloadBytes = element.TryGetProperty("value", out var valueProp)
                ? ParseBytes(valueProp, valueField, $"{path}.value").ToByteArray()
                : Array.Empty<byte>();
        }

        using var buffer = new MemoryStream();
        using (var output = new CodedOutputStream(buffer, leaveOpen: true))
        {
            // Omitted when empty, exactly like every other proto3 default-omission in this file, so
            // the bytes this produces match what Google's own serializer would emit.
            if (typeUrl.Length > 0)
            {
                output.WriteTag(typeUrlField.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteString(typeUrl);
            }
            if (payloadBytes.Length > 0)
            {
                output.WriteTag(valueField.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(payloadBytes));
            }
            output.Flush();
        }
        return buffer.ToArray();
    }

    private static byte[] EncodeTimestamp(MessageDescriptor descriptor, JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new GrpcJsonException(
                $"'{Label(path, descriptor)}': expected an RFC 3339 timestamp string for message '{descriptor.FullName}', found {Describe(element.ValueKind)}.");
        }

        string raw = element.GetString()!;
        if (!WellKnownTypes.TryParseTimestamp(raw, out long seconds, out int nanos))
        {
            throw new GrpcJsonException(
                $"'{Label(path, descriptor)}': '{raw}' is not a valid RFC 3339 timestamp (google.protobuf.Timestamp expects e.g. \"2023-11-14T22:13:20Z\").");
        }

        return EncodeSecondsNanos(descriptor, seconds, nanos);
    }

    private static byte[] EncodeDuration(MessageDescriptor descriptor, JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new GrpcJsonException(
                $"'{Label(path, descriptor)}': expected a Duration string for message '{descriptor.FullName}', found {Describe(element.ValueKind)}.");
        }

        string raw = element.GetString()!;
        if (!WellKnownTypes.TryParseDuration(raw, out long seconds, out int nanos))
        {
            throw new GrpcJsonException(
                $"'{Label(path, descriptor)}': '{raw}' is not a valid duration (google.protobuf.Duration expects e.g. \"1.500s\").");
        }

        return EncodeSecondsNanos(descriptor, seconds, nanos);
    }

    /// <summary>Writes Timestamp/Duration's shared seconds/nanos shape, by field name off the descriptor
    /// exactly as the read side looks them up, omitting either field when it is 0 — proto3
    /// default-omission — which is what makes our bytes equal to Google's for the epoch/zero case.</summary>
    private static byte[] EncodeSecondsNanos(MessageDescriptor descriptor, long seconds, int nanos)
    {
        var secondsField = descriptor.FindFieldByName("seconds");
        var nanosField = descriptor.FindFieldByName("nanos");

        using var buffer = new MemoryStream();
        using (var output = new CodedOutputStream(buffer, leaveOpen: true))
        {
            if (seconds != 0)
            {
                output.WriteTag(secondsField.FieldNumber, WireFormat.WireType.Varint);
                output.WriteInt64(seconds);
            }
            if (nanos != 0)
            {
                output.WriteTag(nanosField.FieldNumber, WireFormat.WireType.Varint);
                output.WriteInt32(nanos);
            }
            output.Flush();
        }
        return buffer.ToArray();
    }

    private static void WriteField(CodedOutputStream output, FieldDescriptor field, JsonElement value, string path, int depth,
        Func<string, MessageDescriptor?>? resolveMessage)
    {
        if (field.IsMap) { WriteMapField(output, field, value, path, depth, resolveMessage); return; }
        if (field.IsRepeated) { WriteRepeatedField(output, field, value, path, depth, resolveMessage); return; }
        WriteSingularValue(output, field, value, path, depth, resolveMessage);
    }

    /// <summary>Writes one non-repeated field's tag and value. Also used, directly, for each element
    /// of a non-packable repeated field (string/bytes/message — the tag simply repeats), and for a map
    /// entry's key and value fields (whose "field number" is 1 or 2 within the synthetic entry
    /// message).</summary>
    private static void WriteSingularValue(CodedOutputStream output, FieldDescriptor field, JsonElement value, string path, int depth,
        Func<string, MessageDescriptor?>? resolveMessage)
    {
        switch (field.FieldType)
        {
            case FieldType.Double:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Fixed64);
                output.WriteDouble(ParseDouble(value, field, path));
                break;
            case FieldType.Float:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Fixed32);
                output.WriteFloat(ParseFloat(value, field, path));
                break;
            case FieldType.Int32:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteInt32(ParseInt32(value, field, path));
                break;
            case FieldType.Int64:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteInt64(ParseInt64(value, field, path));
                break;
            case FieldType.UInt32:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteUInt32(ParseUInt32(value, field, path));
                break;
            case FieldType.UInt64:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteUInt64(ParseUInt64(value, field, path));
                break;
            case FieldType.SInt32:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteSInt32(ParseInt32(value, field, path));
                break;
            case FieldType.SInt64:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteSInt64(ParseInt64(value, field, path));
                break;
            case FieldType.Fixed32:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Fixed32);
                output.WriteFixed32(ParseUInt32(value, field, path));
                break;
            case FieldType.Fixed64:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Fixed64);
                output.WriteFixed64(ParseUInt64(value, field, path));
                break;
            case FieldType.SFixed32:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Fixed32);
                output.WriteSFixed32(ParseInt32(value, field, path));
                break;
            case FieldType.SFixed64:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Fixed64);
                output.WriteSFixed64(ParseInt64(value, field, path));
                break;

            case FieldType.Bool:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new GrpcJsonException(
                        $"'{path}': expected a boolean for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
                }
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteBool(value.GetBoolean());
                break;

            case FieldType.String:
                if (value.ValueKind != JsonValueKind.String)
                {
                    throw new GrpcJsonException(
                        $"'{path}': expected a string for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
                }
                output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteString(value.GetString()!);
                break;

            case FieldType.Bytes:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ParseBytes(value, field, path));
                break;

            case FieldType.Enum:
                output.WriteTag(field.FieldNumber, WireFormat.WireType.Varint);
                output.WriteEnum(ResolveEnumValue(field, value, path));
                break;

            case FieldType.Message:
                // A well-known-type field's canonical JSON form is not necessarily an object (a
                // Timestamp arrives as a string), so this check must not fire for one — Encode's own
                // seam decides what shape that field accepts.
                if (!WellKnownTypes.IsWellKnown(field.MessageType) && value.ValueKind != JsonValueKind.Object)
                {
                    throw new GrpcJsonException(
                        $"'{path}': expected a JSON object for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
                }
                byte[] nested = Encode(field.MessageType, value, path, depth + 1, resolveMessage);
                output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
                // A message field's wire encoding — length followed by the submessage's raw bytes —
                // is structurally identical to a bytes field's, so WriteBytes is the correct way to
                // emit already-encoded submessage bytes without an IMessage instance to hand it.
                output.WriteBytes(ByteString.CopyFrom(nested));
                break;

            case FieldType.Group:
                throw new GrpcJsonException(
                    $"'{path}': field '{field.JsonName}' uses the proto2 'group' wire type, which proto3 cannot express and this build does not support.");

            default:
                throw new GrpcJsonException(
                    $"'{path}': field '{field.JsonName}' has an unrecognized Protobuf field type ({field.FieldType}).");
        }
    }

    /// <summary>Writes the raw value only (no tag) for one element of a packed repeated field. Only
    /// ever called for a packable <see cref="FieldType"/> (see <see cref="IsPackable"/>) — string,
    /// bytes, message, and group are never packed and go through <see cref="WriteSingularValue"/>
    /// instead, tag included, once per element.</summary>
    private static void WriteRawScalarValue(CodedOutputStream output, FieldDescriptor field, JsonElement value, string path)
    {
        switch (field.FieldType)
        {
            case FieldType.Double: output.WriteDouble(ParseDouble(value, field, path)); break;
            case FieldType.Float: output.WriteFloat(ParseFloat(value, field, path)); break;
            case FieldType.Int32: output.WriteInt32(ParseInt32(value, field, path)); break;
            case FieldType.Int64: output.WriteInt64(ParseInt64(value, field, path)); break;
            case FieldType.UInt32: output.WriteUInt32(ParseUInt32(value, field, path)); break;
            case FieldType.UInt64: output.WriteUInt64(ParseUInt64(value, field, path)); break;
            case FieldType.SInt32: output.WriteSInt32(ParseInt32(value, field, path)); break;
            case FieldType.SInt64: output.WriteSInt64(ParseInt64(value, field, path)); break;
            case FieldType.Fixed32: output.WriteFixed32(ParseUInt32(value, field, path)); break;
            case FieldType.Fixed64: output.WriteFixed64(ParseUInt64(value, field, path)); break;
            case FieldType.SFixed32: output.WriteSFixed32(ParseInt32(value, field, path)); break;
            case FieldType.SFixed64: output.WriteSFixed64(ParseInt64(value, field, path)); break;
            case FieldType.Bool:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new GrpcJsonException(
                        $"'{path}': expected a boolean for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
                }
                output.WriteBool(value.GetBoolean());
                break;
            case FieldType.Enum:
                output.WriteEnum(ResolveEnumValue(field, value, path));
                break;
            default:
                // Unreachable: WriteRepeatedField only calls this for a packable FieldType, and every
                // packable FieldType is handled above.
                throw new GrpcJsonException(
                    $"'{path}': field '{field.JsonName}' has Protobuf type {field.FieldType}, which is not a packable repeated type.");
        }
    }

    private static void WriteRepeatedField(CodedOutputStream output, FieldDescriptor field, JsonElement value, string path, int depth,
        Func<string, MessageDescriptor?>? resolveMessage)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new GrpcJsonException(
                $"'{path}': expected a JSON array for repeated field '{field.JsonName}', found {Describe(value.ValueKind)}.");
        }

        if (IsPackable(field.FieldType))
        {
            // Packed by default in proto3: one tag, one length, the concatenated raw values.
            byte[] packedBytes;
            using (var innerBuffer = new MemoryStream())
            {
                using (var innerOutput = new CodedOutputStream(innerBuffer, leaveOpen: true))
                {
                    int index = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        WriteRawScalarValue(innerOutput, field, item, $"{path}[{index}]");
                        index++;
                    }
                    innerOutput.Flush();
                }
                packedBytes = innerBuffer.ToArray();
            }
            if (packedBytes.Length == 0) return; // an explicit empty array behaves like an absent field
            output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(packedBytes));
            return;
        }

        // Non-packable (string, bytes, message): repeat the tag once per element.
        int i = 0;
        foreach (var item in value.EnumerateArray())
        {
            WriteSingularValue(output, field, item, $"{path}[{i}]", depth, resolveMessage);
            i++;
        }
    }

    private static void WriteMapField(CodedOutputStream output, FieldDescriptor field, JsonElement value, string path, int depth,
        Func<string, MessageDescriptor?>? resolveMessage)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new GrpcJsonException(
                $"'{path}': expected a JSON object for map field '{field.JsonName}', found {Describe(value.ValueKind)}.");
        }

        var keyField = field.MessageType.FindFieldByNumber(1);
        var valueField = field.MessageType.FindFieldByNumber(2);

        using var entryBuffer = new MemoryStream();
        foreach (var property in value.EnumerateObject())
        {
            string entryPath = $"{path}.{property.Name}";
            entryBuffer.SetLength(0);
            using (var entryOutput = new CodedOutputStream(entryBuffer, leaveOpen: true))
            {
                if (keyField.FieldType == FieldType.String)
                {
                    if (property.Name.Length > 0)
                    {
                        entryOutput.WriteTag(keyField.FieldNumber, WireFormat.WireType.LengthDelimited);
                        entryOutput.WriteString(property.Name);
                    }
                }
                else
                {
                    var keyElement = ParseMapKey(keyField, property.Name, entryPath);
                    WriteSingularValue(entryOutput, keyField, keyElement, entryPath, depth, resolveMessage);
                }
                if (property.Value.ValueKind != JsonValueKind.Null)
                    WriteSingularValue(entryOutput, valueField, property.Value, entryPath, depth + 1, resolveMessage);
                entryOutput.Flush();
            }

            output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(UnsafeByteOperations.UnsafeWrap(entryBuffer.GetBuffer().AsMemory(0, (int)entryBuffer.Length)));
        }
    }

    /// <summary>A map's JSON property name, reparsed into the JSON representation the key field's own
    /// type expects: a quoted JSON string for a string key, or the bare literal (<c>7</c>,
    /// <c>true</c>) for every other legal key type — which is exactly what the property name's raw
    /// text already looks like for those types.</summary>
    private static JsonElement ParseMapKey(FieldDescriptor keyField, string propertyName, string path)
    {
        string literal = keyField.FieldType == FieldType.String
            ? JsonSerializer.Serialize(propertyName)
            : propertyName;
        try
        {
            using var document = JsonDocument.Parse(literal);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new GrpcJsonException(
                $"'{path}': map key '{propertyName}' is not valid for key type {keyField.FieldType}.");
        }
    }

    private static double ParseDouble(JsonElement value, FieldDescriptor field, string path)
    {
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        if (value.ValueKind == JsonValueKind.String)
        {
            string s = value.GetString()!;
            if (s == "NaN") return double.NaN;
            if (s == "Infinity") return double.PositiveInfinity;
            if (s == "-Infinity") return double.NegativeInfinity;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) return parsed;
        }
        throw new GrpcJsonException(
            $"'{path}': expected a number for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
    }

    private static float ParseFloat(JsonElement value, FieldDescriptor field, string path)
    {
        if (value.ValueKind == JsonValueKind.Number) return value.GetSingle();
        if (value.ValueKind == JsonValueKind.String)
        {
            string s = value.GetString()!;
            if (s == "NaN") return float.NaN;
            if (s == "Infinity") return float.PositiveInfinity;
            if (s == "-Infinity") return float.NegativeInfinity;
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)) return parsed;
        }
        throw new GrpcJsonException(
            $"'{path}': expected a number for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
    }

    private static int ParseInt32(JsonElement value, FieldDescriptor field, string path)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n)) return n;
        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s)) return s;
        throw new GrpcJsonException(
            $"'{path}': expected a number or numeric string for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
    }

    private static uint ParseUInt32(JsonElement value, FieldDescriptor field, string path)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint n)) return n;
        if (value.ValueKind == JsonValueKind.String &&
            uint.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint s)) return s;
        throw new GrpcJsonException(
            $"'{path}': expected a number or numeric string for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
    }

    private static long ParseInt64(JsonElement value, FieldDescriptor field, string path)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long n)) return n;
        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s)) return s;
        throw new GrpcJsonException(
            $"'{path}': expected a number or numeric string for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
    }

    private static ulong ParseUInt64(JsonElement value, FieldDescriptor field, string path)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong n)) return n;
        if (value.ValueKind == JsonValueKind.String &&
            ulong.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong s)) return s;
        throw new GrpcJsonException(
            $"'{path}': expected a number or numeric string for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
    }

    /// <summary>Accepts standard base64 (padded) as well as the URL-safe alphabet and missing padding,
    /// since a real sender is not obliged to use the canonical form on the way in.</summary>
    private static ByteString ParseBytes(JsonElement value, FieldDescriptor field, string path)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new GrpcJsonException(
                $"'{path}': expected a base64 string for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
        }
        string raw = value.GetString()!;
        string normalized = raw.Replace('-', '+').Replace('_', '/');
        int padding = (4 - normalized.Length % 4) % 4;
        normalized = normalized.PadRight(normalized.Length + padding, '=');
        try
        {
            return ByteString.CopyFrom(Convert.FromBase64String(normalized));
        }
        catch (FormatException)
        {
            throw new GrpcJsonException($"'{path}': field '{field.JsonName}' is not valid base64.");
        }
    }

    private static int ResolveEnumValue(FieldDescriptor field, JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string name = value.GetString()!;
            var found = field.EnumType.FindValueByName(name);
            if (found is null)
            {
                throw new GrpcJsonException(
                    $"'{path}': unknown enum value '{name}' for field '{field.JsonName}' ({field.EnumType.FullName}).");
            }
            return found.Number;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;

        throw new GrpcJsonException(
            $"'{path}': expected an enum name or number for field '{field.JsonName}', found {Describe(value.ValueKind)}.");
    }

    /// <summary>The packable Protobuf types: every numeric type plus bool and enum. string, bytes,
    /// message, and group are never packed regardless of repetition.</summary>
    private static bool IsPackable(FieldType type) => type switch
    {
        FieldType.String or FieldType.Bytes or FieldType.Message or FieldType.Group => false,
        _ => true
    };

    private static bool TryGetField(Dictionary<string, JsonElement> properties, FieldDescriptor field, out JsonElement value, out string matchedName)
    {
        if (properties.TryGetValue(field.JsonName, out value)) { matchedName = field.JsonName; return true; }
        if (field.JsonName != field.Name && properties.TryGetValue(field.Name, out value)) { matchedName = field.Name; return true; }
        value = default;
        matchedName = "";
        return false;
    }

    private static string Label(string path, MessageDescriptor descriptor) =>
        string.IsNullOrEmpty(path) ? descriptor.FullName : path;

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        JsonValueKind.Null => "null",
        _ => kind.ToString()
    };
}
