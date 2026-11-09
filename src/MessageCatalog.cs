using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FakeCheats;

public sealed class MessageCatalog
{
    private static readonly string[] Languages = ["en", "zhcn"];
    private static readonly string DefaultJson = ReadDefaultJson();
    private static readonly Dictionary<string, Dictionary<string, string>> Defaults = Parse(DefaultJson);
    private readonly Dictionary<string, Dictionary<string, string>> _messages;

    private MessageCatalog(Dictionary<string, Dictionary<string, string>> messages) => _messages = messages;

    public static MessageCatalog CreateDefault() => new(Defaults);

    public static string NormalizeLanguage(string? language)
    {
        var normalized = language?.Trim().ToLowerInvariant();
        return normalized is "en" or "zhcn" ? normalized
            : throw new ArgumentException("Language must be 'en' or 'zhcn'.");
    }

    public static MessageCatalog Load(string path)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, DefaultJson, new UTF8Encoding(false));
        }

        var overrides = Parse(File.ReadAllText(path));
        var merged = Defaults.ToDictionary(pair => pair.Key,
            pair => new Dictionary<string, string>(pair.Value, StringComparer.Ordinal));
        foreach (var (key, translations) in overrides)
        {
            if (!Defaults.TryGetValue(key, out var defaultTranslations) || translations == null) continue;
            var argumentCount = CompositeFormat.Parse(defaultTranslations["en"]).MinimumArgumentCount;
            foreach (var language in Languages)
            {
                if (!translations.TryGetValue(language, out var template) || template == null) continue;
                // Invalid administrator edits must not break permission checks
                // or crash command callbacks. Keep the built-in translation.
                try
                {
                    if (CompositeFormat.Parse(template).MinimumArgumentCount <= argumentCount)
                        merged[key][language] = template;
                }
                catch (FormatException)
                {
                    // The built-in translation remains available as a fallback.
                }
            }
        }
        return new MessageCatalog(merged);
    }

    public string Get(string language, string key, params object?[] arguments)
    {
        language = NormalizeLanguage(language);
        if (!_messages.TryGetValue(key, out var translations))
            throw new KeyNotFoundException($"Unknown message key: {key}");
        try
        {
            return string.Format(CultureInfo.InvariantCulture, translations[language], arguments);
        }
        catch (FormatException)
        {
            // A structurally valid custom numeric format may still be invalid
            // for the runtime argument type. Fall back to the built-in text.
            return string.Format(CultureInfo.InvariantCulture, Defaults[key][language], arguments);
        }
    }

    private static Dictionary<string, Dictionary<string, string>> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json, new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? throw new JsonException("The language file must contain a JSON object.");

    private static string ReadDefaultJson()
    {
        using var resource = typeof(MessageCatalog).Assembly.GetManifestResourceStream("FakeCheats.messages.json")
            ?? throw new InvalidOperationException("The built-in message catalog is missing.");
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    }
}
