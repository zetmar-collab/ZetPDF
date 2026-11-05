using System.Globalization;
using System.Text;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;

namespace ZetPDF.Core.Documents;

/// <summary>Wynik kontroli: liczba bajtów strumieni obiektów, których nie dało się sprawdzić (szyfrowanie, inne filtry).</summary>
public readonly record struct PdfGuardResult(long UnverifiedObjectStreamBytes);

/// <summary>
/// Kontrola bajtów pliku przed PdfReader z PDFsharp, który jest rekurencyjny: tablica lub słownik zagnieżdżone na około 3000 poziomów
/// wywracają cały program (zmierzone, docs/DECYZJE.md D-42), nawet w pliku kilkunastokilobajtowym. Skan jest iteracyjny. Zaczyna od każdego
/// wystąpienia „n g obj” i „trailer” w pliku, także wewnątrz danych binarnych, bo parser wchodzi do obiektu wyłącznie w takim miejscu, oraz
/// od każdego przesunięcia ze strumieni obiektów. Niepasujące zamknięcie nie zmniejsza głębokości, więc błąd liczony jest zawsze w górę.
/// Plik zbyt głęboki to <see cref="PdfCorruptException"/> z czytelnym komunikatem.
/// </summary>
public static class PdfStructureGuard
{
    public const int MaxDepth = 256;

    /// <summary>Limit łącznego rozmiaru rozpakowanych strumieni obiektów.</summary>
    public const int MaxObjectStreamBytes = 256 * 1024 * 1024;

    /// <summary>Limit liczby wpisów nagłówka jednego strumienia obiektów.</summary>
    public const int MaxObjectStreamEntries = 1_000_000;

    public static PdfGuardResult Check(byte[] pdf, CancellationToken ct = default)
    {
        var scan = new Scanner(pdf, ct);
        scan.Run();
        return new PdfGuardResult(scan.Unverified);
    }

    /// <summary>
    /// Sprawdzenie plików zaszyfrowanych po odszyfrowaniu, ustawiane przez moduł Security (własny parser COS z limitem głębokości): zwraca false,
    /// gdy któryś obiekt jest zbyt głęboki. Strumieni obiektów pliku zaszyfrowanego nie da się sprawdzić tu, w Core.
    /// </summary>
    public static Func<byte[], string?, bool>? EncryptedVerifier { get; set; }

    /// <summary>
    /// Strumień obiektów, którego nie rozpakowano. Bez szyfrowania jest to tylko nietypowy łańcuch filtrów, którym plik mógłby ukryć zagnieżdżenie,
    /// więc to odrzucenie. W pliku zaszyfrowanym sprawdza <see cref="EncryptedVerifier"/>.
    /// </summary>
    public static void CheckUnverified(byte[] pdf, string? password, PdfGuardResult guard)
    {
        if (guard.UnverifiedObjectStreamBytes == 0) return;
        if (pdf.AsSpan().IndexOf("/Encrypt"u8) < 0) throw TooComplex();
        if (EncryptedVerifier is { } verifier && !verifier(pdf, password)) throw TooComplex();
    }

    private static Exception TooComplex() =>
        new PdfCorruptException("The file has a structure nested too deeply or too complex to read safely.").WithMessageKey("Core.Error.TooComplex");

    private sealed class Scanner
    {
        private readonly byte[] _file;
        private readonly CancellationToken _ct;
        private long _budget;
        private int _inflated;
        private readonly byte[] _open = new byte[MaxDepth + 1];

        public Scanner(byte[] file, CancellationToken ct)
        {
            _file = file;
            _ct = ct;
            _budget = file.Length * 16L + 1024 * 1024;
        }

        public long Unverified { get; private set; }

        public void Run()
        {
            var span = _file.AsSpan();
            int pos = 0;
            while (true)
            {
                _ct.ThrowIfCancellationRequested();
                int rel = span[pos..].IndexOf("obj"u8);
                if (rel < 0) break;
                int at = pos + rel;
                pos = at + 3;
                if (IsObjectStart(at)) ScanObject(at);
            }
            pos = 0;
            while (true)
            {
                int rel = span[pos..].IndexOf("trailer"u8);
                if (rel < 0) break;
                pos += rel + 7;
                ScanValue(_file, pos, stopAtValueEnd: false, out _);
            }
        }

        /// <summary>Czy przed „obj” stoją dwie liczby.</summary>
        private bool IsObjectStart(int at)
        {
            int i = at - 1;
            while (i >= 0 && IsWhite(_file[i])) i--;
            int digits = 0;
            while (i >= 0 && _file[i] is >= (byte)'0' and <= (byte)'9') { i--; digits++; }
            if (digits == 0) return false;
            int white = 0;
            while (i >= 0 && IsWhite(_file[i])) { i--; white++; }
            if (white == 0) return false;
            digits = 0;
            while (i >= 0 && _file[i] is >= (byte)'0' and <= (byte)'9') { i--; digits++; }
            return digits > 0;
        }

        private void ScanObject(int at)
        {
            int start = at + 3;
            int stream = ScanValue(_file, start, stopAtValueEnd: false, out bool isStream);
            if (!isStream) return;
            var dict = _file.AsSpan(start, Math.Max(0, stream - start));
            bool objectStream = dict.IndexOf("ObjStm"u8) >= 0 || dict.IndexOf("/First"u8) >= 0 || dict.IndexOf((byte)'#') >= 0;
            if (objectStream) ScanObjectStream(start, stream);
        }

        private void ScanObjectStream(int dictStart, int streamKeyword)
        {
            int data = streamKeyword + 6;
            if (data < _file.Length && _file[data] == '\r') data++;
            if (data < _file.Length && _file[data] == '\n') data++;
            var dict = Encoding.Latin1.GetString(_file, dictStart, streamKeyword - dictStart);
            bool plainFlate = dict.Contains("/FlateDecode", StringComparison.Ordinal) || dict.Contains("/Fl", StringComparison.Ordinal);
            int decodeFilters = CountFilters(dict);
            byte[]? inflated = null;
            if (plainFlate && decodeFilters <= 1)
            {
                try
                {
                    using var input = new MemoryStream(_file, data, _file.Length - data, false);
                    inflated = PdfStreamLimits.InflateLimited(input, MaxObjectStreamBytes - _inflated);
                }
                catch (InvalidDataException) { inflated = null; }
                catch (PdfContentLimitException) { throw TooComplex(); }
            }
            if (inflated is null)
            {
                int end = _file.AsSpan(data).IndexOf("endstream"u8);
                Unverified += end < 0 ? _file.Length - data : end;
                return;
            }
            _inflated += inflated.Length;
            _budget += inflated.Length * 16L;
            int first = FirstFrom(dict);
            foreach (int offset in HeaderOffsets(inflated))
            {
                _ct.ThrowIfCancellationRequested();
                if (first >= 0) ScanValue(inflated, (int)Math.Min(inflated.Length, (long)first + offset), stopAtValueEnd: true, out _);
                else ScanValue(inflated, Math.Min(inflated.Length, offset), stopAtValueEnd: true, out _);
            }
            // /First pośredni lub nieznany: także całość liniowo, jak zwykły tekst obiektów
            if (first < 0) ScanValue(inflated, 0, stopAtValueEnd: false, out _);
        }

        private static int CountFilters(string dict)
        {
            int count = 0, i = 0;
            while ((i = dict.IndexOf("Decode", i, StringComparison.Ordinal)) >= 0)
            {
                count++;
                i += 6;
            }
            return count;
        }

        private static int FirstFrom(string dict)
        {
            int i = dict.IndexOf("/First", StringComparison.Ordinal);
            if (i < 0) return -1;
            i += 6;
            while (i < dict.Length && char.IsWhiteSpace(dict[i])) i++;
            int start = i;
            while (i < dict.Length && char.IsAsciiDigit(dict[i])) i++;
            if (i == start || !int.TryParse(dict.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out int value)) return -1;
            // „n g R” po liczbie to odwołanie, wartości nie znamy
            int j = i;
            while (j < dict.Length && char.IsWhiteSpace(dict[j])) j++;
            return j < dict.Length && char.IsAsciiDigit(dict[j]) ? -1 : value;
        }

        /// <summary>Pary „numer przesunięcie” z początku rozpakowanych danych.</summary>
        private static IEnumerable<int> HeaderOffsets(byte[] data)
        {
            int i = 0, index = 0, entries = 0;
            while (true)
            {
                while (i < data.Length && IsWhite(data[i])) i++;
                int start = i;
                long value = 0;
                while (i < data.Length && data[i] is >= (byte)'0' and <= (byte)'9' && i - start < 11) value = value * 10 + (data[i++] - '0');
                if (i == start) yield break;
                if (index++ % 2 == 1)
                {
                    if (++entries > MaxObjectStreamEntries) throw TooComplex();
                    yield return (int)Math.Min(value, int.MaxValue);
                }
            }
        }

        /// <summary>
        /// Skan jednej wartości. Zwraca pozycję zatrzymania; <paramref name="isStream"/>, gdy zatrzymało słowo „stream” na poziomie zero.
        /// Słowa kluczowe wewnątrz tablicy nie zatrzymują, bo parser też ich tam nie uzna za koniec.
        /// </summary>
        private int ScanValue(byte[] d, int i, bool stopAtValueEnd, out bool isStream)
        {
            isStream = false;
            int n = d.Length, depth = 0, count = 0;
            while (i < n)
            {
                if ((++count & 0xFFFF) == 0) _ct.ThrowIfCancellationRequested();
                if (--_budget < 0) throw TooComplex();
                byte c = d[i];
                if (IsWhite(c)) { i++; continue; }
                switch (c)
                {
                    case (byte)'%':
                        while (i < n && d[i] != '\n' && d[i] != '\r') { i++; _budget--; }
                        continue;
                    case (byte)'(':
                    {
                        int level = 0;
                        for (; i < n; i++)
                        {
                            _budget--;
                            byte s = d[i];
                            if (s == '\\') i++;
                            else if (s == '(') level++;
                            else if (s == ')' && --level == 0) { i++; break; }
                        }
                        if (stopAtValueEnd && depth == 0) return i;
                        continue;
                    }
                    case (byte)'<':
                        if (i + 1 < n && d[i + 1] == '<')
                        {
                            Open(ref depth, (byte)'<');
                            i += 2;
                            continue;
                        }
                        while (i < n && d[i] != '>') { i++; _budget--; }
                        i++;
                        if (stopAtValueEnd && depth == 0) return i;
                        continue;
                    case (byte)'>':
                        if (i + 1 < n && d[i + 1] == '>')
                        {
                            if (Close(ref depth, (byte)'<') && depth == 0 && stopAtValueEnd) return i + 2;
                            i += 2;
                        }
                        else i++;
                        continue;
                    case (byte)'[':
                        Open(ref depth, (byte)'[');
                        i++;
                        continue;
                    case (byte)']':
                        if (Close(ref depth, (byte)'[') && depth == 0 && stopAtValueEnd) return i + 1;
                        i++;
                        continue;
                }
                int start = i;
                while (i < n && !IsWhite(d[i]) && !IsDelimiter(d[i])) i++;
                if (i == start) { i++; continue; }
                _budget -= i - start;
                if (depth == 0)
                {
                    var word = d.AsSpan(start, i - start);
                    if (word.SequenceEqual("stream"u8))
                    {
                        isStream = true;
                        return start;
                    }
                    if (word.SequenceEqual("endobj"u8) || word.SequenceEqual("endstream"u8) || word.SequenceEqual("xref"u8) || word.SequenceEqual("startxref"u8) || word.SequenceEqual("trailer"u8)) return start;
                    if (stopAtValueEnd && word[0] != '/') return i;
                }
            }
            return i;
        }

        private void Open(ref int depth, byte kind)
        {
            if (depth >= MaxDepth) throw TooComplex();
            _open[depth++] = kind;
        }

        /// <summary>Niepasujące zamknięcie nie zmniejsza głębokości.</summary>
        private bool Close(ref int depth, byte kind)
        {
            if (depth == 0 || _open[depth - 1] != kind) return false;
            depth--;
            return true;
        }
    }

    private static bool IsWhite(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
}
