# Licencje bibliotek ZetPDF

Stan: 17.10.2026, recenzent końcowy; uzupełnienie etapu 3: 21.10.2026 (ZXing.Net, Jint, Acornima, System.Speech – porównane z `dotnet list package --include-transitive`, okno O programie i `dist\LICENSES`). Źródło listy: `dotnet list <projekt> package --include-transitive` dla **wszystkich projektów `src\`** (sumarycznie) i osobno dla projektów `tests\`. Licencje odczytane z plików `.nuspec` pakietów w `%USERPROFILE%\.nuget\packages` (element `<license>`), dla Fluent.Ribbon – z pliku `license/License.txt` w pakiecie.

**Wynik kontroli:** wszystkie biblioteki dołączane do programu mają licencje permisywne (MIT, Apache-2.0, BSD, zlib/libpng, FTL, IJG, Unicode) pozwalające na użycie komercyjne w programie o zamkniętym kodzie, pod warunkiem dołączenia informacji o prawach autorskich i tekstów licencji. **Brak AGPL/GPL/LGPL.** PDFsharp jest przypięty do **6.2.4 (MIT)** – wersja 6.5.0 ma licencję AGPL-3.0-only i jest zakazana (DECYZJE D-01, `$(PdfSharpVersion)` w `Directory.Build.props`).

## 1. Biblioteki programu (projekty `src\`, trafiają do `ZetPDF.exe`)

| Pakiet / komponent | Wersja | Licencja | Gdzie | Uwagi |
|---|---|---|---|---|
| PDFsharp | **6.2.4** | MIT | Core (pośrednio wszystkie moduły) | **6.5.0 = AGPL-3.0-only – ZAKAZANY**. Pakiet zawiera też PdfSharp.Cryptography, Snippets, Charting, BarCodes, Quality, WPFonts (MIT) |
| PdfPig | 0.1.16 | Apache-2.0 | Viewer, Pages, Annotations, Editing, Security | jeden pakiet (UglyToad.PdfPig.*) |
| PDFiumCore | 157.0.8100 | Apache-2.0 | Rendering | wrapper .NET |
| bblanchon.PDFium.Win32 | 157.0.8100 | Apache-2.0 (pakowanie) | Rendering | binarny PDFium: **BSD-3-Clause** (Google, Foxit) + statycznie dołączone: FreeType (FTL), libjpeg-turbo (IJG/BSD-3/zlib), OpenJPEG (BSD-2), Little CMS (MIT), zlib (zlib), libpng (libpng-2.0), Abseil (Apache-2.0), ICU (Unicode), AGG 2.3 (licencja permisywna). Pakiet NIE zawiera tekstów licencji – patrz sekcja 4 |
| bblanchon.PDFium.Linux, bblanchon.PDFium.macOS | 157.0.8100 | Apache-2.0 | przechodnie (PDFiumCore) | binaria Linux/macOS nie trafiają do publikacji win-x64 |
| Tesseract (charlesw) | 5.2.0 | Apache-2.0 | Ocr | natywne: Tesseract 5 (Apache-2.0), Leptonica 1.82 (BSD-2-Clause) – osadzone w `ZetPDF.Ocr.dll` (DECYZJE D-07) |
| tessdata_fast `pol.traineddata`, `eng.traineddata` | main (16.10.2026) | Apache-2.0 | `src\ZetPDF.Ocr\tessdata\` (osadzone) | plik `LICENSE` obok danych |
| Fluent.Ribbon | 11.0.2 | MIT | App | licencja z pliku w pakiecie |
| System.Speech | 8.0.0 | MIT | Viewer (czytanie na głos, etap 3) | SAPI 5 z Windows; głosy lokalne, bez sieci |
| ZXing.Net | 0.16.11 | Apache-2.0 | Annotations (pole kodu kreskowego, etap 3) | QR, PDF417, DataMatrix |
| Jint | 4.16.4 | BSD-2-Clause | Annotations (JavaScript formularzy, etap 3) | bez dostępu do CLR; limity czasu, pamięci, rekursji |
| Acornima | 1.7.0 | BSD-3-Clause | przechodnio przez Jint | parser JavaScript |
| ControlzEx | 7.0.3 | MIT | App (przez Fluent.Ribbon) | |
| Microsoft.Xaml.Behaviors.Wpf | 1.1.135 | MIT | App (przez ControlzEx) | |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | Core | |
| Microsoft.Extensions.DependencyInjection / .Abstractions | 8.0.1 / 8.0.2 | MIT | App / Core | |
| Microsoft.Extensions.Logging / .Abstractions | 8.0.1 / 8.0.3 | MIT | Core | |
| Microsoft.Extensions.Options, .Primitives | 8.0.2 / 8.0.0 | MIT | przechodnie | |
| System.Security.Cryptography.Pkcs | 8.0.2 | MIT | Core | podniesione z 8.0.1 (CVE, DECYZJE D-14) |
| System.Security.Cryptography.ProtectedData | 8.0.0 | MIT | Core | DPAPI dla zrzutów Cofnij |
| System.Text.Json | 8.0.5 | MIT | przechodnie | |
| System.IO.Packaging | 8.0.1 | MIT | przechodnie (OpenXml) | |
| System.Reflection.Emit | 4.7.0 | MIT | przechodnie | dopisane przez recenzenta (brakowało na liście) |
| DocumentFormat.OpenXml, DocumentFormat.OpenXml.Framework | 3.5.1 | MIT | Editing | PDF → DOCX |
| .NET 8 runtime + WPF (self-contained w exe) | 8.0.x | MIT | dist | |
| Czcionki Windows (Arial, Segoe UI) | – | licencja systemu | podzbiory osadzane w tworzonych PDF (prawo osadzania TrueType) | ikony: Segoe Fluent Icons / MDL2 z systemu – NIE dołączane |

Pakiety z wcześniejszej listy, które **nie są używane** (nie występują w `dotnet list package` żadnego projektu): DiffPlex 1.9.0 (porównanie ma własną implementację Myersa), BouncyCastle.Cryptography (usunięty po scaleniu). Nie trafiają do programu.

Partia 2 rozbudowy (2026-11-04): **bez nowych pakietów**. Składniki systemu używane w czasie działania, niedołączane do programu ani do repozytorium: profil `sRGB Color Space Profile.icm` z Windows (kopiowany do tworzonych plików PDF/A jako profil wyjściowy, czytany z `%WINDIR%\System32\spool\drivers\color`). Czcionki Windows (Arial, Times New Roman, Courier New i inne) osadzane w całości w PDF/A tylko wtedy, gdy ich `fsType` na to pozwala.

## 2. Biblioteki tylko testów (projekty `tests\`, NIE trafiają do `ZetPDF.exe`)

| Pakiet | Wersja | Licencja |
|---|---|---|
| xunit, xunit.core, xunit.assert, xunit.extensibility.core/execution | 2.9.3 | Apache-2.0 |
| xunit.abstractions | 2.0.3 | Apache-2.0 (licenseUrl do license.txt repozytorium xunit) |
| xunit.analyzers | 1.18.0 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |
| BouncyCastle.Cryptography | 2.6.2 | MIT (tylko `ZetPDF.Security.Tests`: niezależna budowa odpowiedzi OCSP w mini-CA; kod programu nie używa tego pakietu) |
| Microsoft.NET.Test.Sdk, Microsoft.TestPlatform.ObjectModel, Microsoft.TestPlatform.TestHost, Microsoft.CodeCoverage | 17.14.1 | MIT |
| Newtonsoft.Json | 13.0.3 | MIT |
| System.Collections.Immutable, System.Reflection.Metadata | 8.0.0 | MIT |

Testy używają też wszystkich bibliotek z sekcji 1 (przez referencje do projektów `src`).

## 3. Narzędzia użyte tylko do kontroli (nie są częścią programu ani repozytorium)

Do niezależnej weryfikacji plików wynikowych (szyfrowanie AES-256, redakcja, oczyszczanie) recenzent użył zainstalowanych w systemie: PyMuPDF/MuPDF (AGPL – **wyłącznie jako zewnętrzny czytnik, bez linkowania i bez dystrybucji**), pypdf (BSD-3) i Windows.Data.Pdf (składnik Windows). Nie ma ich w zależnościach ZetPDF.

## 4. Teksty licencji w `dist\LICENSES\` – stan

Dołączone pliki: `MIT.txt`, `Apache-2.0.txt`, `BSD-2-Clause.txt`, `BSD-3-Clause.txt` (z `src\ZetPDF.App\Licenses\`), `PDFium-ThirdParty.txt` (noty o prawach autorskich komponentów PDFium), `zlib.txt` (tekst licencji zlib), `Fluent.Ribbon-License.txt` (z pakietu), `tessdata-LICENSE.txt` (Apache-2.0, z `src\ZetPDF.Ocr\tessdata\LICENSE`), `Jint-LICENSE.txt` (BSD-2, posiadacz praw Sebastien Ros), `Acornima-LICENSE.txt` (BSD-3, Adam Simon, z notą o kodzie acorn – MIT i Esprima .NET – BSD-3), `THIRD-PARTY-NOTICES.txt` (lista z sekcji 1).

**Brakuje pełnych tekstów** licencji FreeType (FTL), IJG/libjpeg-turbo, libpng-2.0 i Unicode/ICU. Pakiet bblanchon.PDFium ich nie zawiera, a recenzent nie pobierał plików z sieci (brak zgody na pobieranie w tej sesji). Przed dystrybucją komercyjną należy dołączyć plik `LICENSE` z repozytorium https://github.com/bblanchon/pdfium-binaries (wersja odpowiadająca 157.0.8100). Noty wymagane w dokumentacji (FTL – „Portions of this software are copyright © The FreeType Project”, IJG – „based in part on the work of the Independent JPEG Group”, AGG) są w `PDFium-ThirdParty.txt` i w oknie O programie > Licencje bibliotek.

Zakazane: AGPL/GPL (iText 7, MuPDF, Ghostscript, **PDFsharp ≥ 6.5**), LGPL z linkowaniem statycznym. Nowy pakiet: sprawdzić `.nuspec` (`<license>`) i dopisać tutaj.

Uwaga (etap 3): pakiety Jint i Acornima nie zawierają plików LICENSE; linie praw autorskich wpisano z metadanych `.nuspec` (`<copyright>`), bez lat. Przed dystrybucją komercyjną porównać z plikami LICENSE repozytoriów GitHub (w tej kontroli nie pobierano plików z sieci). ZXing.Net (Apache-2.0) i System.Speech (MIT, .NET Foundation) – teksty `Apache-2.0.txt` i `MIT.txt`.
