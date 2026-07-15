using System;
using System.Buffers;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace P2PFileExchange.Core.Serialization;

/// <summary>
/// Converts <see cref="TimeSpan"/> to and from ISO 8601 duration format.
/// </summary>
public sealed class TimeSpanIso8601Converter : JsonConverter<TimeSpan>
{
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
            throw new JsonException("Expected ISO 8601 duration.");
        }

        if (reader.ValueSpan.Length == 0)
        {
            return TimeSpan.Zero;
        }

        char[] buffer = ArrayPool<char>.Shared.Rent(reader.ValueSpan.Length);
        try
        {
            int charsWritten = reader.CopyString(buffer);
            return XmlConvert.ToTimeSpan(
                buffer.AsSpan(0, charsWritten).ToString()
            );
        }
        catch (FormatException ex)
        {
            throw new JsonException("Invalid ISO 8601 duration.", ex);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

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
    public override IPAddress Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return IPAddress.None;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected IP address string.");
        }

        ReadOnlySpan<byte> utf8 = reader.HasValueSequence
            ? reader.ValueSequence.ToArray()
            : reader.ValueSpan;

        if (utf8.Length == 0)
        {
            throw new JsonException("Empty IP address string.");
        }

        char[] buffer = ArrayPool<char>.Shared.Rent(utf8.Length);
        try
        {
            int charsWritten = reader.CopyString(buffer);
            ReadOnlySpan<char> chars = buffer.AsSpan(0, charsWritten);

            if (!IPAddress.TryParse(chars, out IPAddress? address))
            {
                throw new JsonException("Invalid IP address.");
            }

            return address;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        IPAddress value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStringValue(value.ToString());
    }
}
