using System;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace P2PFileTransfer.Core.Serialization;

/// <summary>
/// Converts <see cref="TimeSpan"/> to and from ISO 8601 duration format.
/// </summary>
public sealed class TimeSpanIso8601Converter : JsonConverter<TimeSpan>
{
    /// <inheritdoc />
    public override TimeSpan Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return TimeSpan.Zero;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected an ISO 8601 duration string.");
        }

        string? value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.Zero;
        }

        try
        {
            return XmlConvert.ToTimeSpan(value);
        }
        catch (FormatException ex)
        {
            throw new JsonException("Invalid ISO 8601 duration.", ex);
        }
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        TimeSpan value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStringValue(XmlConvert.ToString(value));
    }
}

/// <summary>
/// Converts <see cref="IPAddress"/> to and from string representation.
/// </summary>
public sealed class IPAddressConverter : JsonConverter<IPAddress>
{
    /// <inheritdoc />
    public override IPAddress Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return IPAddress.Broadcast;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected an IP address string.");
        }

        string? value = reader.GetString();
        if (
            string.IsNullOrWhiteSpace(value)
            || !IPAddress.TryParse(value, out IPAddress? address)
        )
        {
            throw new JsonException("Invalid IP address.");
        }

        return address;
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        IPAddress value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStringValue(value.ToString());
    }
}
