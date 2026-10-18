using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pitwall.Domain;

public enum FactQuality { Valid, Unsupported, PermissionDenied, Unavailable, Stale, Invalid, Estimated }

// Payload access always supplies quality; missing is a separate branch, never default(T).
[JsonConverter(typeof(ObservationConverterFactory))]
public sealed class Observation<T>
{
    private readonly T? payload;
    public FactQuality Quality { get; }
    public bool HasPayload { get; }

    private Observation(FactQuality quality, bool hasPayload, T? payload)
    {
        if (!Enum.IsDefined(quality)) throw new ArgumentOutOfRangeException(nameof(quality));
        if (hasPayload && quality is FactQuality.Unsupported or FactQuality.PermissionDenied or FactQuality.Unavailable)
            throw new ArgumentException("observation.quality_forbids_payload", nameof(quality));
        if (!hasPayload && quality is FactQuality.Valid or FactQuality.Estimated or FactQuality.Stale)
            throw new ArgumentException("observation.quality_requires_payload", nameof(quality));
        Quality = quality;
        HasPayload = hasPayload;
        this.payload = payload;
    }

    public static Observation<T> Present(FactQuality quality, T payload) => new(quality, true, payload);
    public static Observation<T> Missing(FactQuality quality) => new(quality, false, default);

    public TResult Match<TResult>(Func<FactQuality, T, TResult> present, Func<FactQuality, TResult> missing)
    {
        ArgumentNullException.ThrowIfNull(present);
        ArgumentNullException.ThrowIfNull(missing);
        return HasPayload ? present(Quality, payload!) : missing(Quality);
    }
}

public sealed class ObservationConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsGenericType &&
        typeToConvert.GetGenericTypeDefinition() == typeof(Observation<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(ObservationConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()))!;

    private sealed class ObservationConverter<T> : JsonConverter<Observation<T>>
    {
        public override Observation<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (!root.TryGetProperty("quality", out var qualityNode) || qualityNode.ValueKind != JsonValueKind.String ||
                !Enum.TryParse<FactQuality>(qualityNode.GetString(), false, out var quality) || !Enum.IsDefined(quality) || qualityNode.GetString() != quality.ToString())
                throw new JsonException("observation.quality_required");
            if (!root.TryGetProperty("hasPayload", out var hasPayload) || hasPayload.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException("observation.presence_required");
            try
            {
                if (hasPayload.GetBoolean())
                {
                    if (!root.TryGetProperty("payload", out var payloadNode)) throw new JsonException("observation.payload_required");
                    return Observation<T>.Present(quality, payloadNode.Deserialize<T>(options)!);
                }
                if (root.TryGetProperty("payload", out _)) throw new JsonException("observation.unexpected_payload");
                return Observation<T>.Missing(quality);
            }
            catch (ArgumentException exception) { throw new JsonException("observation.invalid_quality_payload", exception); }
        }

        public override void Write(Utf8JsonWriter writer, Observation<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("quality", value.Quality.ToString());
            writer.WriteBoolean("hasPayload", value.HasPayload);
            value.Match((_, payload) => { writer.WritePropertyName("payload"); JsonSerializer.Serialize(writer, payload, options); return true; }, _ => false);
            writer.WriteEndObject();
        }
    }
}
