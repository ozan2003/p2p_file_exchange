using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using P2PFileTransfer.Desktop.Settings;

namespace P2PFileTransfer.Desktop.Services;

/// <summary>
/// Loads and saves application settings to disk.
/// </summary>
public sealed class SettingsStore
{
    private const string SettingsDirectoryName = "P2PFileTransfer";
    private const string SettingsFileName = "settings.json";
    private readonly string m_settingsPath;
    private readonly JsonSerializerOptions m_jsonOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsStore"/> class.
    /// </summary>
    /// <param name="settingsPath">Optional custom settings path.</param>
    public SettingsStore(string? settingsPath = null)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            this.m_settingsPath = GetDefaultSettingsPath();
        }
        else
        {
            this.m_settingsPath = Path.GetFullPath(settingsPath);
        }
        this.m_jsonOptions = CreateJsonOptions();
    }

    /// <summary>
    /// Gets the settings file path.
    /// </summary>
    public string SettingsPath => this.m_settingsPath;

    /// <summary>
    /// Loads persisted settings or returns defaults when unavailable.
    /// </summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(this.m_settingsPath))
            {
                return CreateDefaultSettings();
            }

            using FileStream stream = File.OpenRead(this.m_settingsPath);
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(
                stream,
                this.m_jsonOptions
            );
            settings ??= CreateDefaultSettings();
            settings.Normalize();
            return settings;
        }
        catch (IOException)
        {
            return CreateDefaultSettings();
        }
        catch (JsonException)
        {
            return CreateDefaultSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return CreateDefaultSettings();
        }
    }

    /// <summary>
    /// Persists settings to disk.
    /// </summary>
    /// <param name="settings">The settings to persist.</param>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings, nameof(settings));
        settings.Normalize();

        string directory =
            Path.GetDirectoryName(this.m_settingsPath) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{this.m_settingsPath}.tmp";
        string json = JsonSerializer.Serialize(settings, this.m_jsonOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, this.m_settingsPath, overwrite: true);
    }

    /// <summary>
    /// Creates default settings with normalization applied.
    /// </summary>
    private static AppSettings CreateDefaultSettings()
    {
        AppSettings settings = new();
        settings.Normalize();
        return settings;
    }

    /// <summary>
    /// Builds JSON serializer options for settings persistence.
    /// </summary>
    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        options.Converters.Add(new TimeSpanIso8601Converter());
        options.Converters.Add(new IPAddressConverter());
        return options;
    }

    /// <summary>
    /// Resolves the default settings file path.
    /// </summary>
    private static string GetDefaultSettingsPath()
    {
        string basePath = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData
        );
        if (string.IsNullOrWhiteSpace(basePath))
        {
            basePath = Environment.CurrentDirectory;
        }

        return Path.Combine(basePath, SettingsDirectoryName, SettingsFileName);
    }

    private sealed class TimeSpanIso8601Converter : JsonConverter<TimeSpan>
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
                throw new JsonException(
                    "Expected an ISO 8601 duration string."
                );
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

        public override void Write(
            Utf8JsonWriter writer,
            TimeSpan value,
            JsonSerializerOptions options
        )
        {
            writer.WriteStringValue(XmlConvert.ToString(value));
        }
    }

    private sealed class IPAddressConverter : JsonConverter<IPAddress>
    {
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

        public override void Write(
            Utf8JsonWriter writer,
            IPAddress value,
            JsonSerializerOptions options
        )
        {
            writer.WriteStringValue(value.ToString());
        }
    }
}
