using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ZetPDF.Core.Documents;

/// <summary>
/// Pomocnik PDFsharp 6.2.4 (MIT) dla modułów: otwarcie bajtów, edycja, zapis do bajtów.
/// Bajty wejściowe muszą być BEZ szyfrowania (tak trzyma je sesja).
/// UWAGA: nie używać PDFsharp 6.5.0+ – zmienił licencję na AGPL-3.0 (patrz docs/DECYZJE.md).
/// </summary>
public static class PdfSharpEdit
{
    private static readonly object InitGate = new();
    private static bool _initialized;

    /// <summary>
    /// Konfiguracja globalna PDFsharp (czcionki systemowe Windows – polskie znaki). Wołane automatycznie
    /// przez metody tej klasy; wołaj ręcznie, jeśli używasz PDFsharp bezpośrednio (XGraphics/XFont).
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (InitGate)
        {
            if (_initialized) return;
            try { GlobalFontSettings.UseWindowsFontsUnderWindows = true; }
            catch (InvalidOperationException) { /* ustawione wcześniej przez kogoś innego */ }
            _initialized = true;
        }
    }

    /// <summary>
    /// Jedyne wejście do PdfReader: najpierw <see cref="PdfStructureGuard"/>, potem otwarcie na wątku z dużym stosem. Plik z niesprawdzalnymi
    /// strumieniami obiektów (szyfrowanie) dostaje stos 1 GB; patrz docs/DECYZJE.md D-42. Wyjątki PdfReader przechodzą bez zmian.
    /// </summary>
    public static PdfDocument OpenGuarded(byte[] pdf, string? password, PdfDocumentOpenMode mode)
    {
        EnsureInitialized();
        var guard = PdfStructureGuard.Check(pdf);
        PdfStructureGuard.CheckUnverified(pdf, password, guard);
        return BigStack.Run(() => password is null
            ? PdfReader.Open(new MemoryStream(pdf, writable: false), mode)
            : PdfReader.Open(new MemoryStream(pdf, writable: false), password, mode),
            guard.UnverifiedObjectStreamBytes > 0 ? BigStack.LargeBytes : BigStack.DefaultBytes);
    }

    /// <summary>Otwiera do modyfikacji. Pamiętaj o Dispose.</summary>
    public static PdfDocument OpenForModify(byte[] pdf)
    {
        return OpenGuarded(pdf, null, PdfDocumentOpenMode.Modify);
    }

    /// <summary>Otwiera do odczytu/importu stron (szybciej, bez możliwości zapisu tego dokumentu).</summary>
    public static PdfDocument OpenForImport(byte[] pdf)
    {
        return OpenGuarded(pdf, null, PdfDocumentOpenMode.Import);
    }

    /// <summary>Zapisuje dokument do bajtów.</summary>
    public static byte[] Save(PdfDocument document)
    {
        using var ms = new MemoryStream();
        document.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    /// <summary>Otwórz → edytuj → zapisz. Zwraca nowe bajty.</summary>
    public static byte[] Modify(byte[] pdf, Action<PdfDocument> edit)
    {
        using var doc = OpenForModify(pdf);
        edit(doc);
        return Save(doc);
    }

    /// <summary>Odczyt z dokumentu (tryb Import).</summary>
    public static T Read<T>(byte[] pdf, Func<PdfDocument, T> read)
    {
        using var doc = OpenForImport(pdf);
        return read(doc);
    }

    /// <summary>Wersja asynchroniczna Modify (Task.Run) – dla metod usług "bajty → bajty".</summary>
    public static Task<byte[]> ModifyAsync(byte[] pdf, Action<PdfDocument> edit, CancellationToken ct = default) =>
        Task.Run(() => { ct.ThrowIfCancellationRequested(); return Modify(pdf, edit); }, ct);
}
