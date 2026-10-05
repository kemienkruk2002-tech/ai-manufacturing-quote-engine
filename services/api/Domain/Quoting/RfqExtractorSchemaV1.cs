using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuoteEngine.Domain.Quoting;

public sealed record RfqExtractorSchemaDefinition(string RawJson, JsonElement Schema, string Sha256);

public static class RfqExtractorSchemaV1
{
    public const string ResourceName = "QuoteEngine.Domain.Quoting.rfq-extractor-v1.schema.json";
    public const string SchemaName = "RFQ_EXTRACTOR";
    public const string SchemaVersion = "v1";

    public static RfqExtractorSchemaDefinition Load()
    {
        using var resource = typeof(RfqExtractorSchemaV1).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded RFQ schema resource '{ResourceName}' is missing.");
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        var content = bytes.ToArray();
        var rawJson = new UTF8Encoding(false, true).GetString(content);
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !HasExactConst(root, "schema", SchemaName)
            || !HasExactConst(root, "version", SchemaVersion))
            throw new InvalidOperationException("Embedded RFQ schema identity is invalid.");
        return new(rawJson, root.Clone(), sha256);
    }

    private static bool HasExactConst(JsonElement root, string propertyName, string expected) =>
        root.TryGetProperty("properties", out var properties)
        && properties.ValueKind == JsonValueKind.Object
        && properties.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Object
        && property.TryGetProperty("const", out var constant)
        && constant.ValueKind == JsonValueKind.String
        && StringComparer.Ordinal.Equals(constant.GetString(), expected);
}
