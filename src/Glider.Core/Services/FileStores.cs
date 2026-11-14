using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glider.Core.Services;

internal static class SmallFiles
{
    /// <summary>Write-to-temp then rename, so a crash never leaves a half-written file.</summary>
    public static async Task WriteAtomicAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, new UTF8Encoding(false)); // v0.1 wrote a BOM; readers accept both
        File.Move(tmp, path, overwrite: true);
    }

    public static JsonObject? TryReadObject(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Reads JSON-ish values off disk without throwing (folder inspection).</summary>
internal static class JsonFiles
{
    public static string? ReadString(string path, string key)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
                {
                    return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
                }
            }
        }
        catch (Exception)
        {
            // Unreadable or malformed — the caller falls back.
        }

        return null;
    }
}
