using System.Text;
using System.Text.RegularExpressions;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;

namespace Filee.Engines.Office;

/// <summary>
/// Reads Word 97-2003 (.doc), .dot, and disguised Word documents into the <see cref="HDocument"/> model.
/// Supports OpenXML ZIPs named .doc, RTF files named .doc, HTML/XML exports named .doc,
/// and binary OLE WordDocument streams.
/// </summary>
internal static class DocReader
{
    public static HDocument Read(string path, string mediaFolder)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0)
            return new HDocument();

        // 1. Check for DOCX disguised as .doc (ZIP PK header 0x50, 0x4B, 0x03, 0x04)
        if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04)
        {
            try
            {
                return DocxReader.Read(path, mediaFolder);
            }
            catch
            {
                // If corrupted DOCX or not standard, continue to fallback
            }
        }

        // 2. Check for RTF disguised as .doc ({\rtf)
        if (bytes.Length >= 5 && bytes[0] == (byte)'{' && bytes[1] == (byte)'\\' && bytes[2] == (byte)'r' && bytes[3] == (byte)'t' && bytes[4] == (byte)'f')
        {
            return ReadRtf(bytes);
        }

        // 3. Check for HTML or XML disguised as .doc
        var preview = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 512)).TrimStart();
        if (preview.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
            preview.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
            preview.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return HtmlReader.Read(path, mediaFolder);
            }
            catch
            {
                // Fallback to text if HTML reader fails
            }
        }

        // 4. Binary Word 97-2003 (.doc) OLE Compound Document (0xD0, 0xCF, 0x11, 0xE0)
        if (bytes.Length >= 8 && bytes[0] == 0xD0 && bytes[1] == 0xCF && bytes[2] == 0x11 && bytes[3] == 0xE0)
        {
            return ReadBinaryWord(bytes);
        }

        // 5. Generic fallback: decode text
        return TextToDocument(DocumentReaders.DecodeText(bytes));
    }

    /// <summary>Extracts paragraphs and tables from binary Word 97-2003 (.doc) files.</summary>
    private static HDocument ReadBinaryWord(byte[] bytes)
    {
        var text = ExtractBinaryWordText(bytes);
        if (string.IsNullOrWhiteSpace(text))
        {
            return TextToDocument(DocumentReaders.DecodeText(bytes));
        }
        return TextToDocument(text);
    }

    /// <summary>Scans WordDocument stream / OLE payload for Unicode UTF-16LE and 8-bit text runs.</summary>
    private static string ExtractBinaryWordText(byte[] bytes)
    {
        var sb = new StringBuilder();

        // Scan for runs of UTF-16LE text (common in Word 97-2003 documents)
        var utf16Run = new StringBuilder();
        for (var i = 0; i < bytes.Length - 1; i += 2)
        {
            var b1 = bytes[i];
            var b2 = bytes[i + 1];
            var ch = (char)(b1 | (b2 << 8));

            if (ch == '\r' || ch == '\n' || ch == '\t' || ch == '\f' || ch == '\x07' ||
                (ch >= 0x20 && ch < 0xD800) || (ch >= 0xE000 && ch <= 0xFFFD))
            {
                utf16Run.Append(ch);
            }
            else
            {
                if (utf16Run.Length >= 10)
                {
                    sb.Append(utf16Run);
                    sb.Append('\n');
                }
                utf16Run.Clear();
            }
        }
        if (utf16Run.Length >= 10)
        {
            sb.Append(utf16Run);
        }

        if (sb.Length >= 50)
            return CleanWordControlChars(sb.ToString());

        // Fallback: scan for 8-bit ASCII / CP1252 text runs
        sb.Clear();
        var asciiRun = new StringBuilder();
        foreach (var b in bytes)
        {
            if (b == '\r' || b == '\n' || b == '\t' || b == '\f' || b == '\x07' || (b >= 32 && b <= 126) || (b >= 160 && b <= 255))
            {
                asciiRun.Append((char)b);
            }
            else
            {
                if (asciiRun.Length >= 10)
                {
                    sb.Append(asciiRun);
                    sb.Append('\n');
                }
                asciiRun.Clear();
            }
        }
        if (asciiRun.Length >= 10)
        {
            sb.Append(asciiRun);
        }

        return CleanWordControlChars(sb.ToString());
    }

    private static string CleanWordControlChars(string text)
    {
        // In Word: \x07 is table cell separator, \r is paragraph end, \x0C is page break
        var cleaned = text.Replace('\f', '\n').Replace('\r', '\n');
        return cleaned;
    }

    /// <summary>Extracts text and paragraphs from Rich Text Format (RTF) data.</summary>
    private static HDocument ReadRtf(byte[] bytes)
    {
        var raw = Encoding.ASCII.GetString(bytes);
        var sb = new StringBuilder();
        var stack = new Stack<bool>(); // true if group should be ignored
        var i = 0;
        var len = raw.Length;

        while (i < len)
        {
            var ch = raw[i];
            if (ch == '{')
            {
                // Check if this group is ignored
                var isIgnored = stack.Count > 0 && stack.Peek();
                if (!isIgnored && i + 1 < len && raw[i + 1] == '\\')
                {
                    var word = ReadWord(raw, i + 2);
                    if (word is "fonttbl" or "colortbl" or "stylesheet" or "info" or "*" or "pict" or "header" or "footer")
                    {
                        isIgnored = true;
                    }
                }
                stack.Push(isIgnored);
                i++;
            }
            else if (ch == '}')
            {
                if (stack.Count > 0) stack.Pop();
                i++;
            }
            else if (ch == '\\')
            {
                i++;
                if (i >= len) break;
                var next = raw[i];
                if (next is '{' or '}' or '\\')
                {
                    if (stack.Count == 0 || !stack.Peek()) sb.Append(next);
                    i++;
                }
                else if (next == '\'')
                {
                    // Hex character: \'xx
                    if (i + 2 < len && (stack.Count == 0 || !stack.Peek()))
                    {
                        var hex = raw.Substring(i + 1, 2);
                        if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var b))
                        {
                            sb.Append((char)b);
                        }
                    }
                    i += 3;
                }
                else
                {
                    // Control word
                    var wordStart = i;
                    while (i < len && char.IsLetter(raw[i])) i++;
                    var word = raw.Substring(wordStart, i - wordStart);

                    // Skip any optional integer parameter
                    var numStart = i;
                    if (i < len && (raw[i] == '-' || char.IsDigit(raw[i])))
                    {
                        i++;
                        while (i < len && char.IsDigit(raw[i])) i++;
                    }

                    // Skip optional trailing space
                    if (i < len && raw[i] == ' ') i++;

                    if (stack.Count == 0 || !stack.Peek())
                    {
                        if (word is "par" or "line") sb.Append('\n');
                        else if (word == "tab") sb.Append('\t');
                        else if (word == "u" && numStart < i)
                        {
                            var numStr = raw.Substring(numStart, i - numStart).Trim();
                            if (short.TryParse(numStr, out var unicodeVal))
                            {
                                sb.Append((char)unicodeVal);
                                if (i < len && raw[i] == '?') i++; // Skip fallback char
                            }
                        }
                    }
                }
            }
            else
            {
                if (ch != '\r' && ch != '\n')
                {
                    if (stack.Count == 0 || !stack.Peek()) sb.Append(ch);
                }
                i++;
            }
        }

        return TextToDocument(sb.ToString());
    }

    private static string ReadWord(string text, int start)
    {
        var i = start;
        while (i < text.Length && char.IsLetter(text[i])) i++;
        return text.Substring(start, i - start);
    }

    /// <summary>Builds an <see cref="HDocument"/> from plain or structured text paragraphs.</summary>
    private static HDocument TextToDocument(string text)
    {
        var doc = new HDocument();
        var section = new HSection();
        doc.Sections.Add(section);

        var lines = text.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                section.Blocks.Add(new HParagraph());
                continue;
            }

            // Check if this line is a table row (separated by \x07 or |)
            if (line.Contains('\x07') || (line.StartsWith('|') && line.EndsWith('|') && line.Count(c => c == '|') >= 3))
            {
                var parts = line.Contains('\x07') ? line.Split('\x07', StringSplitOptions.RemoveEmptyEntries)
                                                  : line.Trim('|').Split('|');
                var table = new HTable { ColumnCount = parts.Length };
                var row = new HRow();
                foreach (var part in parts)
                {
                    var cell = new HCell();
                    var p = new HParagraph();
                    p.Inlines.Add(new HText(part.Trim(), default));
                    cell.Blocks.Add(p);
                    row.Cells.Add(cell);
                }
                table.Rows.Add(row);
                section.Blocks.Add(table);
                continue;
            }

            var para = new HParagraph();
            para.Inlines.Add(new HText(line, default));
            section.Blocks.Add(para);
        }

        return doc;
    }
}
