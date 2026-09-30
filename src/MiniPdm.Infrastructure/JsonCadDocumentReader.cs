using System.Text;
using System.Text.Json;
using MiniPdm.Application;

namespace MiniPdm.Infrastructure;

public sealed class JsonCadDocumentReader : ICadDocumentReader
{
    public async Task<IReadOnlyList<CadReadResult>> ReadSourceAsync(string source, CancellationToken ct = default)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Папка не найдена: {source}");
        var files = Directory.EnumerateFiles(source)
            .Where(path => path.EndsWith(".a3d", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".m3d", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
        var results = new List<CadReadResult>(files.Length);
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);
            try
            {
                var json = await File.ReadAllTextAsync(path, new UTF8Encoding(false, true), ct);
                using var parsed = JsonDocument.Parse(json);
                var root = parsed.RootElement;
                var properties = root.GetProperty("properties");
                var kindText = root.GetProperty("type").GetString();
                var kind = Enum.TryParse<CadKind>(kindText, false, out var parsedKind) ? parsedKind : (CadKind)0;
                var components = root.GetProperty("components").EnumerateArray()
                    .Select(item => new CadComponent(item.GetProperty("file").GetString() ?? "", item.GetProperty("count").GetInt32()))
                    .ToArray();
                var document = new CadDocument(
                    root.GetProperty("fileName").GetString() ?? "", kind,
                    OptionalString(root.GetProperty("designation")), root.GetProperty("name").GetString() ?? "",
                    OptionalString(properties.GetProperty("material")), OptionalDecimal(properties.GetProperty("mass")),
                    components, root.GetProperty("formatVersion").GetInt32());
                results.Add(new CadReadResult(fileName, document, null));
            }
            catch (DecoderFallbackException)
            {
                results.Add(new CadReadResult(fileName, null, "Файл содержит недопустимую последовательность UTF-8"));
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or IOException or UnauthorizedAccessException or KeyNotFoundException)
            {
                results.Add(new CadReadResult(fileName, null, "Не удалось прочитать JSON-документ"));
            }
        }
        return results;
    }

    private static string? OptionalString(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : element.GetString();
    private static decimal? OptionalDecimal(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : element.GetDecimal();
}
