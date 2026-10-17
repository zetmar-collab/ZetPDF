# Licencje bibliotek ZetPDF (lista wstępna – szkielet)

Sprawdzone w plikach `.nuspec` pobranych pakietów (16.10.2026) i `dotnet list package --include-transitive`. Wszystkie licencje pozwalają na użycie komercyjne (zamknięty kod) przy zachowaniu informacji o prawach autorskich i treści licencji (MIT/BSD/Apache – dołączyć teksty w oknie „O programie”/„Licencje bibliotek” i w `dist\`).

| Pakiet / komponent | Wersja | Licencja | Gdzie | Uwagi |
|---|---|---|---|---|
| PDFsharp | **6.2.4** | MIT | Core (wszystkie moduły pośrednio) | **6.5.0 = AGPL-3.0-only – ZAKAZANY** (DECYZJE D-01). Pakiet zawiera też PdfSharp.Cryptography, Snippets, Charting, BarCodes, Quality, WPFonts (MIT) |
| PDFiumCore | 157.0.8100 | Apache-2.0 | Rendering | wrapper .NET |
| bblanchon.PDFium.Win32 (+ .Linux, .macOS – zależności przechodnie, nieużywane) | 157.0.8100 | Apache-2.0 (pakowanie) | Rendering | binarny PDFium: **BSD-3-Clause** (Google/Foxit) + komponenty: FreeType (FTL), libjpeg-turbo (IJG/BSD), OpenJPEG (BSD-2), Little CMS (MIT), zlib, libpng, abseil (Apache-2.0), ICU (Unicode). Pakiet nie zawiera pliku z tekstami licencji – teksty z repozytorium bblanchon/pdfium-binaries (plik LICENSE) należy dołączyć do `dist\` (zadanie ui-shell/recenzent) |
| PdfPig | 0.1.16 | Apache-2.0 | Viewer, Pages, Annotations, Editing, Security | |
| Tesseract (charlesw) | 5.2.0 | Apache-2.0 | Ocr | natywne: Tesseract 5 (Apache-2.0), Leptonica 1.82 (BSD-2-Clause) |
| tessdata_fast `pol.traineddata`, `eng.traineddata` | main (16.10.2026) | Apache-2.0 | `src\ZetPDF.Ocr\tessdata\` (osadzone) | plik `LICENSE` obok danych |
| Fluent.Ribbon | 11.0.2 | MIT | App | |
| ControlzEx | 7.0.3 | MIT | App (przez Fluent.Ribbon) | |
| Microsoft.Xaml.Behaviors.Wpf | 1.1.135 | MIT | App (przez ControlzEx) | |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | Core | |
| Microsoft.Extensions.DependencyInjection (+Abstractions) | 8.0.1 / 8.0.2 | MIT | App / Core | |
| Microsoft.Extensions.Logging (+Abstractions, Options, Primitives) | 8.0.x | MIT | Core | |
| System.Security.Cryptography.Pkcs | 8.0.2 | MIT | Core | podniesione z 8.0.1 (CVE) |
| System.Security.Cryptography.ProtectedData | 8.0.0 | MIT | Core | DPAPI dla zrzutów |
| System.Text.Json, System.IO.Packaging, System.Memory i inne System.* | 8.0.x | MIT | przechodnie | |
| DocumentFormat.OpenXml (+ .Framework) | 3.5.1 | MIT | Editing | PDF → DOCX |
| ZXing.Net | 0.16.11 | Apache-2.0 | (etap 3 – jeszcze nieużyty) | |
| DiffPlex | 1.9.0 | Apache-2.0 | (kandydat dla porównania – security-signatures decyduje) | |
| Newtonsoft.Json | 13.0.3 | MIT | tylko testy (przez Test SDK) | |
| xunit, xunit.runner.visualstudio | 2.9.3 / 3.1.4 | Apache-2.0 | tylko testy | |
| Microsoft.NET.Test.Sdk (+ TestPlatform, CodeCoverage) | 17.14.1 | MIT | tylko testy | |
| .NET 8 runtime (self-contained) | 8.0.x | MIT | dist | |
| Czcionki Windows (Arial, Segoe UI) | – | licencja systemu | osadzane podzbiory w PDF (prawo osadzania TrueType – standardowe) | ikony: Segoe Fluent Icons / MDL2 z systemu, NIE dołączane |

Zakazane: AGPL/GPL (iText 7, MuPDF, Ghostscript, **PDFsharp ≥ 6.5**), LGPL z linkowaniem statycznym. Nowy pakiet: agent sprawdza `.nuspec` (`<license>`) i wpisuje go do raportu; koordynator dopisuje tutaj.
