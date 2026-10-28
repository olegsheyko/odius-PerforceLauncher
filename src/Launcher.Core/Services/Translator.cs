using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PerforceLauncher.Core.Services;

/// <summary>
/// Перевод описаний changelist'ов на английский. Без ключей: публичный endpoint Google Translate,
/// при его недоступности MyMemory. Текст уходит на сторонний сервис, поэтому вызывается только по кнопке.
/// </summary>
public static class Translator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Regex Cyrillic = new(@"\p{IsCyrillic}");
    private const int ChunkLimit = 1800;   // длина запроса в URL ограничена

    public static bool HasCyrillic(string text) => Cyrillic.IsMatch(text);

    /// <summary>Перевод на английский с сохранением переносов строк. Бросает <see cref="InvalidOperationException"/> с понятной причиной.</summary>
    public static async Task<string> ToEnglishAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var result = new StringBuilder();
        foreach (var chunk in Chunks(lines))
        {
            if (result.Length > 0) result.Append('\n');
            // Строки без кириллицы (пути, номера CL, уже английский текст) не трогаем.
            result.Append(HasCyrillic(chunk) ? await TranslateChunkAsync(chunk, ct) : chunk);
        }
        return result.ToString().Replace("\n", Environment.NewLine);
    }

    private static IEnumerable<string> Chunks(string[] lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length > 0 && sb.Length + line.Length + 1 > ChunkLimit) { yield return sb.ToString(); sb.Clear(); }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        yield return sb.ToString();
    }

    private static async Task<string> TranslateChunkAsync(string chunk, CancellationToken ct)
    {
        try { return await GoogleAsync(chunk, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception first)
        {
            try
            {
                // MyMemory принимает до ~500 байт за запрос: переводим построчно.
                var parts = new List<string>();
                foreach (var line in chunk.Split('\n'))
                    parts.Add(HasCyrillic(line) ? await MyMemoryAsync(line, ct) : line);
                return string.Join("\n", parts);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                throw new InvalidOperationException(first is HttpRequestException or TaskCanceledException
                    ? "No connection to the translation service."
                    : "The translation service returned an unexpected response.", first);
            }
        }
    }

    private static async Task<string> GoogleAsync(string text, CancellationToken ct)
    {
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=en&dt=t&q=" + Uri.EscapeDataString(text);
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        // [[["перевод","оригинал",...], ...], ...]: склеиваем переведённые сегменты (переносы строк в них сохраняются).
        var sb = new StringBuilder();
        foreach (var segment in doc.RootElement[0].EnumerateArray())
            if (segment[0].ValueKind == JsonValueKind.String) sb.Append(segment[0].GetString());
        return sb.Length > 0 ? sb.ToString().TrimEnd('\n') : throw new InvalidOperationException("Empty translation.");
    }

    private static async Task<string> MyMemoryAsync(string text, CancellationToken ct)
    {
        var url = "https://api.mymemory.translated.net/get?langpair=ru|en&q=" + Uri.EscapeDataString(text);
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        var translated = doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString();
        return string.IsNullOrEmpty(translated) ? throw new InvalidOperationException("Empty translation.") : translated;
    }
}
