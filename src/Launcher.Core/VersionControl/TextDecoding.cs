using System.Runtime.InteropServices;
using System.Text;

namespace PerforceLauncher.Core.VersionControl;

/// <summary>
/// Байты от p4 -> строка. Сервер без Unicode-режима хранит текст в кодировке клиента, который его записал
/// (у русскоязычной команды на Windows это CP1251), поэтому: строгий UTF-8, иначе системная ANSI-кодовая
/// страница — та же, которой пользуется сам p4.exe и P4V.
/// </summary>
public static class TextDecoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [DllImport("kernel32.dll")]
    private static extern int GetACP();

    /// <summary>Системная ANSI-кодовая страница (например, 1251).</summary>
    public static Encoding Legacy { get; } = CreateLegacy();

    private static Encoding CreateLegacy()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var acp = GetACP();
            // С включённой в Windows опцией «UTF-8 for worldwide language support» ACP = 65001, но команда на
            // не-Unicode сервере всё равно пишет в региональной кодировке (ru-RU -> 1251). Берём её из региональных настроек.
            if (acp == 65001 || acp == 0)
                acp = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            if (acp == 65001 || acp == 0) acp = 1252;
            return Encoding.GetEncoding(acp);
        }
        catch { return Encoding.Latin1; }
    }

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var ascii = true;
        foreach (var b in bytes) if (b >= 0x80) { ascii = false; break; }
        if (ascii) return Encoding.ASCII.GetString(bytes);
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Legacy.GetString(bytes); }
    }
}

/// <summary>
/// Разбор вывода «p4 -G» (Python marshal v0): поток словарей вида { 's'ключ 's'значение ... '0' }.
/// В отличие от -Mj (JSON), здесь байты строк сырые, и не-UTF-8 текст не превращается в «�».
/// </summary>
public static class P4Marshal
{
    public static List<Dictionary<string, string>> Parse(byte[] data)
    {
        var result = new List<Dictionary<string, string>>();
        var i = 0;
        while (i < data.Length)
        {
            if (data[i] != (byte)'{') throw new FormatException($"Unexpected p4 -G output at byte {i}.");
            i++;
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            while (true)
            {
                if (i >= data.Length) throw new FormatException("Truncated p4 -G output.");
                if (data[i] == (byte)'0') { i++; break; }   // конец словаря
                var key = ReadValue(data, ref i);
                row[key] = ReadValue(data, ref i);
            }
            result.Add(row);
        }
        return result;
    }

    private static string ReadValue(byte[] d, ref int i)
    {
        var type = (char)d[i++];
        switch (type)
        {
            case 's':
            {
                var len = BitConverter.ToInt32(d, i);
                i += 4;
                if (len < 0 || i + len > d.Length) throw new FormatException("Bad string length in p4 -G output.");
                var s = TextDecoding.Decode(d.AsSpan(i, len));
                i += len;
                return s;
            }
            case 'i':
            {
                var v = BitConverter.ToInt32(d, i);
                i += 4;
                return v.ToString();
            }
            default:
                throw new FormatException($"Unsupported value type '{type}' in p4 -G output.");
        }
    }
}
