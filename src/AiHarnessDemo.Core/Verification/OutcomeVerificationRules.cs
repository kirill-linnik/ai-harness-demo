using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiHarnessDemo.Core.Verification;

public static partial class OutcomeVerificationRules
{
    public static string HashCandidateManifest(CandidateManifest manifest) =>
        ComputeSha256(SerializeCanonical(manifest));

    public static string ComputeSha256(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public static bool IsSha256(string? value) =>
        value is not null && DigestPattern().IsMatch(value);

    private static string SerializeCanonical<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var document = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(value));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(document.RootElement, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(
        JsonElement element,
        Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element
                             .EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported JSON value kind '{element.ValueKind}'.");
        }
    }

    [GeneratedRegex(
        "^sha256:[0-9a-f]{64}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();
}
