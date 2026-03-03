using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace carton.Core.Serialization;

internal static class JsonSerialization
{
    private static readonly JsonWriterOptions UnescapedIndentedWriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task WriteIndentedAsync<T>(
        string path,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new Utf8JsonWriter(stream, UnescapedIndentedWriterOptions);
        JsonSerializer.Serialize(writer, value, typeInfo);
        await writer.FlushAsync(cancellationToken);
    }
}
