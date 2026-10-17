# ZetPDF – architektura (wersja 0.1.0-test)

Dokument obowiązuje wszystkich agentów. Kontrakty w `src\ZetPDF.Core\Contracts\` oraz typy w `src\ZetPDF.Core\Commands\` (katalog poleceń, rejestr, polecenia dokumentu) są **zamrożone** – zmiany tylko przez koordynatora (opisz potrzebę w raporcie, sekcja „Prośby do koordynatora”). Decyzje z uzasadnieniem: `docs\DECYZJE.md`. Licencje: `docs\LICENCJE.md`. Ograniczenia: `docs\OGRANICZENIA.md`. Pełna tabela wstążki: Załącznik A (ten plik) i `docs\WSTAZKA.md`.

## 1. Stos i wersje (zweryfikowane uruchomieniem)

| Element | Wersja | Licencja | Status |
|---|---|---|---|
| .NET SDK | 8.0.423 (`global.json`, rollForward latestPatch) | MIT | build OK |
| WPF, C# 12, `net8.0-windows`, x64 | – | – | – |
| **PDFsharp** | **6.2.4** (NIE 6.5.0!) | MIT | **6.5.0 (wydany 14.10.2026) ma licencję AGPL-3.0-only – zakazany**; patrz DECYZJE D-01 |
| PDFiumCore (+ bblanchon.PDFium.Win32) | 157.0.8100 | Apache-2.0 (PDFium: BSD-3) | OK |
| PdfPig | 0.1.16 | Apache-2.0 | OK |
| Tesseract (charlesw) + tessdata_fast pol/eng | 5.2.0 | Apache-2.0 (Leptonica BSD-2) | OK, wymaga obejścia w single-file |
| Fluent.Ribbon (+ ControlzEx, Xaml.Behaviors) | 11.0.2 | MIT | OK, przełączanie motywu na żywo |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | – |
| Microsoft.Extensions.DependencyInjection / Logging | 8.0.x | MIT | – |
| System.Security.Cryptography.Pkcs | 8.0.2 (PDFsharp ciągnie 8.0.1 z CVE) | MIT | OK |
| ~~BouncyCastle.Cryptography~~ | – | MIT | usunięty po scaleniu – nieużywany (podpisy przez System.Security.Cryptography.Pkcs) |
| DocumentFormat.OpenXml | 3.5.1 | MIT | OK |
| ZXing.Net (etap 3) | 0.16.11 | Apache-2.0 | OK (nie dołączony jeszcze do żadnego projektu) |
| xUnit 2.9.3, Test SDK 17.14.1, runner 3.1.4 | – | Apache-2.0 / MIT | testy |

### 1.1 Wyniki weryfikacji bibliotek (program `scratch\LibCheck`, 29 sprawdzeń, 28 OK + 1 oczekiwany brak)

| Sprawdzenie | Wynik |
|---|---|
| PDFsharp: utworzenie PDF z polskim tekstem (Arial, Unicode/Identity-H), zakładki zagnieżdżone, adnotacja Text | OK |
| PDFsharp: zapis **AES-256** (`SecurityHandler.SetEncryptionToV5()`) | OK – w pliku `/V 5 /R 6 /CFM /AESV3` |
| PDFium: otwarcie AES-256 bez hasła / złym hasłem | OK – `FPDF_ERR_PASSWORD` (4) w obu przypadkach (rozróżnienie „brak” vs „złe” tylko po tym, czy podano hasło) |
| PDFium: AES-256 z hasłem użytkownika i właściciela, tekst, wyszukiwanie „gęślą”, render | OK |
| PDFium: wczytanie z pamięci (`FPDF_LoadMemDocument`), liczba stron, render BGRA (`FPDFBitmapCreateEx` format 4), prostokąty znaków (`FPDFTextGetCharBox`), wyszukiwanie (`FPDFTextFindStart/Next`) | OK |
| PDFium: `FPDF_SaveAsCopy(..., FPDF_REMOVE_SECURITY=3)` z hasłem UŻYTKOWNIKA → bajty bez szyfrowania, które PDFsharp otwiera w trybie Modify | OK – **podstawa modelu „sesja trzyma odszyfrowane bajty”** |
| PDFium: `FPDF_GetSignatureCount` na pliku podpisanym przez PDFsharp | OK (1) |
| PDFsharp: otwarcie AES-256 hasłem **właściciela** w trybie Modify | OK |
| PDFsharp: otwarcie hasłem **użytkownika** w trybie Modify | **NIE** – `To modify the document the owner password is required` (oczekiwane; obejście: PDFium RemoveSecurity) |
| PDFsharp: po otwarciu zaszyfrowanego i zapisie | **szyfrowanie NIE jest zachowywane** (plik zapisany bez hasła) – trzeba ponownie wywołać `SetEncryptionToV5()` + hasła (sprawdzone: działa) |
| PDFsharp: usuwanie zabezpieczeń (`SetEncryptionToNoneAndResetPasswords`) | OK |
| PDFsharp: operacje na stronach (MovePage, Rotate, RemoveAt, import z innego dokumentu, CropBox) | OK |
| PDFsharp: `ContentReader.ReadContent(page)` – parsowanie operatorów (Tj/TJ/Do…) | OK |
| PDFsharp: AcroForm – pola tworzone niskopoziomowo (słowniki), odczyt `PdfTextField`/`PdfCheckBoxField`, zapis wartości z polskimi znakami | OK (PDFsharp nie ma wysokopoziomowego API do TWORZENIA pól – trzeba budować słowniki; patrz OGRANICZENIA) |
| PDFsharp 6.2.4: **podpis cyfrowy** – `PdfSharp.Pdf.Signatures.DigitalSignatureHandler.ForDocument(doc, new PdfSharpDefaultSigner(cert, PdfMessageDigestType.SHA256, timestampUri), options)` (zestaw `PdfSharp.Cryptography.dll` jest W pakiecie PDFsharp, osobny pakiet niepotrzebny) | OK – podpis widoczny, `/ByteRange` obejmuje cały plik, `SignedCms.CheckSignature` OK, zmiana 1 bajtu wykryta |
| PDFsharp: podpis dokumentu otwartego z AES-256 | podpis OK, ale plik wynikowy **bez szyfrowania** (jak wyżej) – szyfrowanie trzeba ustawić przed podpisem w tym samym zapisie (do sprawdzenia przez security-signatures) |
| PdfPig: słowa z pozycjami, także z AES-256 + hasło; odczyt AcroForm | OK |
| System.Security.Cryptography.Pkcs: `SignedCms`, `Rfc3161TimestampRequest` (bez sieci) | OK |
| BouncyCastle, DocumentFormat.OpenXml (DOCX), ZXing.Net (QR) | OK |
| Tesseract 5.2.0 pod .NET 8 x64 (build zwykły) | OK – OCR renderu 300 DPI, pewność 0,93; tessdata_fast słabiej rozpoznaje polskie znaki („Zażółć” → „Zazoic”, „gęślą” OK) |
| Tesseract w **publikacji single-file** | **Nie działa wprost** (`Assembly.Location` puste → `ArgumentNullException`; natywne DLL nie trafiają do pakietu). Obejście **zweryfikowane**: DLL i tessdata jako zasoby osadzone, rozpakowanie do `%LOCALAPPDATA%\ZetPDF\ocr\5.2.0\` i `TesseractEnviornment.CustomSearchPath` – działa w single-file (gotowa klasa `ZetPDF.Ocr.TesseractBootstrap` + test) |
| PDFium w publikacji single-file (`IncludeNativeLibrariesForSelfExtract`) | OK |
| Cała aplikacja (szkielet) opublikowana single-file (`ZetPDF.exe` 91 MB) | uruchamia się (sprawdzone) |
| Fluent.Ribbon 11: `ThemeManager.Current.ChangeTheme(app, "Dark.Blue")` na żywo | OK (`scratch\RibbonCheck`: tło karty #F7F7F7 → #2C2C2C) |
| WPF `KeyGesture` dla skrótów bez Ctrl/Alt (np. `Shift+H`) | **NotSupportedException** – takie skróty obsługuje powłoka ręcznie (`Shortcut.RequiresNonTextFocus`) |

## 2. Projekty i referencje

```
ZetPDF.sln
├─ src\
│  ├─ ZetPDF.Core          (WPF lib)  kontrakty, model dokumentu, Cofnij/Ponów, usługi wspólne, lokalizacja, klucze motywu, PDFsharp
│  ├─ ZetPDF.Rendering     (lib)      PDFium → IPdfRenderer                              → Core
│  ├─ ZetPDF.Viewer        (WPF lib)  widok, miniatury, zakładki, wyszukiwanie, druk      → Core (+PdfPig)
│  ├─ ZetPDF.Pages         (WPF lib)  strony, oznaczenia, scalanie/dzielenie, optymalizator, oczyszczanie → Core (+PdfPig)
│  ├─ ZetPDF.Annotations   (WPF lib)  komentarze, formularze, Wypełnij i podpisz, załączniki → Core (+PdfPig)
│  ├─ ZetPDF.Ocr           (lib)      Tesseract → IOcrService                            → Core (+Tesseract)
│  ├─ ZetPDF.Editing       (WPF lib)  edycja tekstu/obiektów, łącza, konwersje, redakcja, okna OCR → Core, Ocr (+PdfPig, OpenXml)
│  ├─ ZetPDF.Security      (WPF lib)  hasła, podpisy, porównanie, Kreator działań         → Core (+PdfPig)
│  └─ ZetPDF.App           (WinExe, AssemblyName=ZetPDF)  powłoka                         → wszystkie powyższe (+Fluent.Ribbon, M.E.DI)
└─ tests\
   ├─ ZetPDF.TestData            generator przykładowych PDF (wspólny)
   ├─ ZetPDF.Core.Tests          → Core
   ├─ ZetPDF.Viewer.Tests        → Rendering, Viewer
   ├─ ZetPDF.Pages.Tests         → Pages
   ├─ ZetPDF.Annotations.Tests   → Annotations
   ├─ ZetPDF.Editing.Tests       → Editing, Ocr
   ├─ ZetPDF.Security.Tests      → Security
   ├─ ZetPDF.App.Tests           → App
   └─ ZetPDF.IntegrationTests    → wszystko
```

**Zasada: moduły funkcjonalne NIE referencjonują się nawzajem** (wyjątek: Editing → Ocr, ten sam właściciel). Komunikacja wyłącznie przez interfejsy z `Core\Contracts` rozwiązywane z kontenera DI. Dzięki temu np. Kreator działań (Security) woła `ISanitizeService` (Pages) i `IOcrService` (Ocr), a miniatury (Viewer) wołają `IPageOperations.MoveAsync` (Pages), zanim te moduły powstaną – do tego czasu dostaną `NotImplementedException` (polecenie wstążki powinno wtedy być wyszarzone lub pokazać komunikat `Core.Error.NotImplemented` przez `IDialogService.ShowError`).

## 3. Własność modułów (kto edytuje które pliki)

| Agent | Pliki/foldery (wyłącznie) |
|---|---|
| **architekt / koordynator** | `ZetPDF.sln`, `global.json`, `Directory.Build.props`, `Directory.Build.targets`, `tests\Directory.Build.props`, `.gitignore`, `tools\**`, `src\ZetPDF.Core\**`, `tests\ZetPDF.Core.Tests\**`, `docs\ARCHITEKTURA.md`, `docs\WSTAZKA.md`, `docs\DECYZJE.md`, `docs\LICENCJE.md`, `docs\OGRANICZENIA.md` |
| **ui-shell** | `src\ZetPDF.App\**` (w tym `App.xaml.cs` – ale NIE usuwa wpisów `ModuleCatalog`; `Resources\Strings*.resx` z tekstami wszystkich poleceń `Cmd.*`, kart `Ribbon.Tab.*`, grup `Ribbon.Group.*`; `Themes\*.xaml`), `tests\ZetPDF.App.Tests\**`, `docs\ograniczenia\ui-shell.md` |
| **renderer-viewer** | `src\ZetPDF.Rendering\**`, `src\ZetPDF.Viewer\**`, `tests\ZetPDF.Viewer.Tests\**`, `docs\ograniczenia\renderer-viewer.md` |
| **pages-engine** | `src\ZetPDF.Pages\**`, `tests\ZetPDF.Pages.Tests\**`, `docs\ograniczenia\pages-engine.md` |
| **annotations-forms** | `src\ZetPDF.Annotations\**`, `tests\ZetPDF.Annotations.Tests\**`, `docs\ograniczenia\annotations-forms.md` |
| **edit-convert-ocr** | `src\ZetPDF.Editing\**`, `src\ZetPDF.Ocr\**` (w tym `tessdata\` – nie podmieniać danych), `tests\ZetPDF.Editing.Tests\**`, `docs\ograniczenia\edit-convert-ocr.md` |
| **security-signatures** | `src\ZetPDF.Security\**`, `tests\ZetPDF.Security.Tests\**`, `docs\ograniczenia\security-signatures.md` |
| **tester** | `tests\ZetPDF.TestData\**`, `tests\ZetPDF.IntegrationTests\**`, `docs\RAPORT_TESTOW.md` |
| **recenzent-kodu** | wszystko (na końcu, na `main`) + `docs\RAPORT_KONTROLI.md`, `docs\zrzuty\` |

Uwagi:
- Pliki „wspólne” (`.sln`, `Directory.Build.*`, `ModuleCatalog.cs`, `Core\**`) są skończone – agenci modułów ich nie edytują. Gałęzie modułów scalają się bez konfliktów, bo zbiory plików są rozłączne.
- Panele boczne: Miniatury i Zakładki – renderer-viewer; Komentarze i **Załączniki** – annotations-forms; Podpisy – security-signatures. Powłoka (ui-shell) daje hosta (`IPanelRegistry`).
- Zaślepki wygenerowane w szkielecie (`*Service.cs` z `NotImplementedException`, `PlaceholderViewer.cs`) należą do właściciela modułu – zastępuje je implementacją (może zmieniać nazwy klas, rejestrując je w swoim `*Module.ConfigureServices`).
- `TestData` jest używany przez wszystkich, ale edytuje go tylko tester. Moduł, który potrzebuje specjalnej próbki, generuje ją we własnym projekcie testowym.

## 4. Pakiety NuGet – polityka

- **Bez Central Package Management.** Wspólne wersje są właściwościami MSBuild w `Directory.Build.props` (`$(PdfSharpVersion)`, `$(PdfPigVersion)`, `$(PdfiumCoreVersion)`, `$(TesseractVersion)`, `$(FluentRibbonVersion)`, `$(MvvmToolkitVersion)`, `$(MsExtensionsVersion)`, `$(PkcsVersion)`) – używaj ich, jeśli pakiet jest na tej liście.
- Moduł potrzebujący NOWEGO pakietu dopisuje `<PackageReference Include="X" Version="1.2.3" />` **we własnym csproj** (z jawną wersją) i podaje licencję w raporcie. Nie dodawaj pakietu, który już przychodzi z Core (PDFsharp, CommunityToolkit.Mvvm, M.E.DI.Abstractions, M.E.Logging, Pkcs, ProtectedData).
- **PDFsharp pozostaje na 6.2.x** (6.5.0 = AGPL). Nie aktualizuj.
- Testy: xUnit/Test SDK dodaje automatycznie `tests\Directory.Build.props` (dla projektów z `<IsTestProject>true</IsTestProject>`).
- `NU1605` (obniżenie wersji) jest błędem – przy konflikcie podnieś wersję we własnym csproj zgodnie z komunikatem.

## 5. Integracja modułów z powłoką

### 5.1 Moduł
Każdy projekt ma jedną klasę `*Module : IFeatureModule` (`RenderingModule`, `ViewerModule`, `PagesModule`, `AnnotationsModule`, `OcrModule`, `EditingModule`, `SecurityModule`). `ZetPDF.App\ModuleCatalog.CreateAll()` zawiera je wszystkie. Start (`App.OnStartup`):
1. `services.AddZetPdfCore(paths)` – usługi rdzenia.
2. Usługi powłoki (`IThemeService`, `IDialogService`, `IShellService`, `MainWindow`).
3. `module.ConfigureServices(services)` dla każdego modułu → `BuildServiceProvider(ValidateOnBuild = true)`.
4. Rejestracja zasobów App, ustawienie języka i motywu.
5. `module.Initialize(provider)` (wątek UI): moduł rejestruje swoje `Resources\Strings` (`ILocalizationService.RegisterResources(Strings)` – już wpisane), podpina obsługę poleceń (`ICommandRegistry.Register`), panele (`IPanelRegistry.Register`), menu dynamiczne (`SetMenuItems`).
6. Okno główne; pliki z linii poleceń (`-pwd<hasło>`).

### 5.2 Polecenia i wstążka
- Układ wstążki jest **deklaratywny i stały**: `Core\Commands\RibbonCatalog.g.cs` + `CommandIds.g.cs`, generowane z `tools\catalog\catalog.py` (jedyne źródło; Załącznik A). Powłoka buduje wstążkę z katalogu. Polecenie bez obsługi jest **wyszarzone** (nie ukryte).
- Moduł podpina obsługę: `registry.Register(CommandIds.Pages.Delete, new ActiveDocumentCommand(documents, dialogs, d => …, DocumentPermissions.AssembleDocument, canExecute: d => …))`. `ActiveDocumentCommand` (Core) = CanExecute tylko przy aktywnym dokumencie z uprawnieniem; wyjątki → `IDialogService.ShowError`.
- Przełączniki (narzędzia, tryby widoku) implementują `IToggleCommand` (np. `ToggleCommand` z Core: `IsChecked = () => tools.IsActive(CommandIds.Tool.Hand)`; wołaj `RaiseIsCheckedChanged()` po `IToolService.ActiveToolChanged`).
- Polecenia typu `Menu`: dzieci stałe z `RibbonCatalog.Menus` (np. `Stamp.Watermark` → Add/Update/RemoveAll) albo dynamiczne `registry.SetMenuItems(CommandIds.Comment.Stamp, …)` (lista stempli, kształtów `Edit.AddShape`, podpisów `FillSign.Signature`).
- Polecenia spoza katalogu: `registry.RegisterExtra(descriptor, tabId, groupId, handler)`; tekst pod `descriptor.CustomTextKey` w zasobach modułu (prefiks modułu).
- Karty kontekstowe `FillSign` i `CompareResults`: moduł woła `IShellService.SetContextualTabVisible(ContextualTabIds.FillSign, true)`.
- Teksty: etykieta `Cmd.<Id>`, podpowiedź `Cmd.<Id>.Tip` (opcjonalna), karta `Ribbon.Tab.<Id>`, grupa `Ribbon.Group.<Id>`, karta Plik `Ribbon.Tab.File` – wszystkie w `ZetPDF.App\Resources\Strings*.resx` (ui-shell; wstępnie wypełnione pl/en dla wszystkich 180 poleceń).
- Ikony: `CommandDescriptor.IconGlyph` = znak z „Segoe Fluent Icons” (Windows 11) z rezerwą „Segoe MDL2 Assets” (Windows 10) przez klucz czcionki `Font.Family.Icons`. ui-shell może nadpisać ikony własną mapą w App (np. geometrie XAML) – nie kopiujemy ikon Foxita.
- Skróty: `CommandDescriptor.Shortcut` (notacja nazw `System.Windows.Input.Key`, kilka rozdzielonych `;`), parsowane przez `Core.Commands.Shortcut`. Skróty z Ctrl/Alt/F-klawisze → WPF `KeyBinding`. Skróty bez Ctrl/Alt (`Shift+H`, `Shift+T`…) i klawisze jednoliterowe (`SingleKey`, tylko gdy `AppSettings.SingleKeyShortcuts`) → ręcznie w `PreviewKeyDown`, **wyłącznie gdy fokus nie jest w polu tekstowym**. Konflikty skrótów sprawdza test `CatalogTests`.

### 5.3 Panele boczne
`IPanelRegistry.Register(new PanelDescriptor(PanelIds.Thumbnails, "Viewer.Panel.Thumbnails", "", 10, sp => new ThumbnailsPanel(...)))`. Panel sam śledzi `IDocumentManager.ActiveDocumentChanged` / `IViewerService.ActiveViewChanged`. Kolejność: Miniatury 10, Zakładki 20, Komentarze 30, Załączniki 40, Podpisy 50.

## 6. Model dokumentu, polecenia, Cofnij/Ponów

### 6.1 Sesja i zrzuty
- `IPdfDocumentSession` (impl. `Core.Documents.PdfDocumentSession`) trzyma **niezmienny `DocumentSnapshot`**: bajty PDF **bez szyfrowania** + `PdfEncryptionSettings? Encryption` (stosowane dopiero przy zapisie). `GetBytes()` zwraca kopię.
- Każda zmiana = `IDocumentCommand` → nowy zrzut. Standardowo: **usługa modułu ma metodę „bajty → bajty”**, a polecenie to `DelegateDocumentCommand(descriptionKey, (pdf, progress, ct) => service.XxxAsync(pdf, …, ct), DocumentChangeKind.…, DocumentPermissions.…)`. Te same metody wywołuje Kreator działań (wsadowo, bez sesji) i testy.
- `session.ExecuteAsync(cmd)`: sprawdza uprawnienie, serializuje edycje (jedna naraz), wykonuje w `Task.Run`, zapisuje (przed, po) w historii, podnosi `Changed` (z `DocumentChangeKind` i stronami) na wątku UI. Wyjątek polecenia → stan bez zmian. Wynik identyczny bajtowo = brak wpisu w historii.
- Cofnij/Ponów przywraca zrzuty (bez ponownego wykonania). Limit poziomów: `AppSettings.UndoLevels` (domyślnie 30). Budżet pamięci 256 MB/dokument – starsze zrzuty przenoszone do `%TEMP%\ZetPDF\<pid>\snapshots` (dla dokumentów zaszyfrowanych chronione **DPAPI**, CurrentUser). `IsModified` = bieżąca wersja ≠ wersja zapisana (Cofnij do zapisanej zeruje flagę).
- Po `Changed` widok przeładowuje dokument PDFium (`IPdfRenderer.OpenAsync(session.Current.GetBytes())`) i unieważnia cache stron wg `Kind`/`AffectedPages`; panele odświeżają się wg `Kind`.
- Operacje lekkie (przesunięcie adnotacji, zmiana wartości pola) – ten sam mechanizm (koszt: zapis PDFsharp całego pliku; dla dokumentów do ~50 MB akceptowalny). Ciągłe interakcje (przeciąganie) rysuj na nakładce i wykonuj polecenie dopiero po puszczeniu myszy.
- Klucz opisu Cofnij (`DescriptionKey`) należy do modułu, który TWORZY polecenie, konwencja `<Prefiks>.Undo.<Operacja>` (np. `Viewer.Undo.MovePages`, `Pages.Undo.Watermark`). Wspólne w Core: `Core.Undo.SetEncryption`, `Core.Undo.RemoveEncryption`, `Core.Undo.Metadata`.

### 6.2 Otwieranie i zapis (`Core.Documents.DocumentManager`)
1. Odczyt bajtów pliku (bez trzymania blokady pliku).
2. `IPdfRenderer.ProbeAsync` (rezerwa: PDFsharp) → `PasswordRequired`/`InvalidPassword` → `IDialogService.AskPassword` (pętla, anulowanie = null) → `Corrupt` → `PdfCorruptException`.
3. Plik zaszyfrowany: `ISecurityService.AnalyzeAsync` (rezerwa: `PdfSecurityHelper.Analyze`) → metoda, rewizja, uprawnienia, czy hasło właściciela; odszyfrowanie `IPdfRenderer.RemoveSecurityAsync` (PDFium; rezerwa PDFsharp – tylko z hasłem właściciela); ustawienia ponownego szyfrowania `ISecurityService.CreateResaveSettings` (rezerwa: `PdfSecurityHelper.CreateResaveSettings`, DECYZJE D-08).
4. Uprawnienia efektywne: hasło właściciela lub brak szyfrowania → `All`; hasło użytkownika → uprawnienia z `/P`. Polecenia deklarują `RequiredPermission`, sesja egzekwuje.
5. Zapis: `GetBytesForSaveAsync` (zrzut → haki `AddSaveTransform` w kolejności rejestracji, np. „Spłaszcz przy zapisie” – sesja się nie zmienia → szyfrowanie: `ISecurityService.EncryptAsync`, rezerwa `PdfSecurityHelper.Encrypt`) → `IFileService.WriteAllBytesSafeAsync`: plik tymczasowy w tym samym folderze, potem `File.Replace` (kopia `.bak` usuwana po sukcesie) lub `File.Move`. Nigdy bezpośrednie nadpisanie.
6. Podpis cyfrowy: `GetBytesForSaveAsync` → `ISignatureService.SignAsync` → zapis bajtów → ponowne otwarcie (`RevertAsync`/`OpenAsync`), bo podpisany plik nie może już przejść przez PDFsharp-owy zapis.
7. Ścieżki z polskimi znakami i spacjami – test `Open_modify_save_with_polish_path`.

## 7. Wątki

- **Wątek UI**: tylko WPF. Żadnych `.Result`/`.Wait()`. Ciężkie rzeczy `await`.
- **PDFium**: wyłącznie przez `IPdfRenderer`/`IRenderDocument`, który wykonuje wszystkie wywołania na **jednym dedykowanym wątku** (kolejka zadań, np. `BlockingCollection<Action>` + `TaskCompletionSource`). Każdy uchwyt (`FPDF_LoadMemDocument`, `FPDF_LoadPage`, `FPDFText_LoadPage`, `FPDFBitmap_Create*`, wyszukiwanie) zwalniany w `finally`/`Dispose` na tym samym wątku. Bajty dokumentu przypięte (`GCHandle`) na czas życia uchwytu dokumentu. Anulowanie: zadania w kolejce sprawdzają token przed startem; render dużych stron może być progresywny.
- **PDFsharp/PdfPig**: w `Task.Run` (bezpieczne równolegle dla RÓŻNYCH obiektów dokumentu). `PdfSharpEdit.EnsureInitialized()` ustawia czcionki Windows (polskie znaki) – wołane automatycznie.
- **Tesseract**: `TesseractEngine` nie jest wątkowo bezpieczny – jeden silnik na zadanie/wątek.
- Zdarzenia sesji i `IDocumentManager` – na wątku UI. Postęp przez `IProgress<OperationProgress>` (tworzony na UI → raporty wracają na UI).

## 8. Widok, narzędzia, nakładki, współrzędne

- `IDocumentViewFactory.CreateView(session)` → `IDocumentView` (kontrolka do karty). `IViewerService.ActiveView`.
- Współrzędne: przestrzeń strony = punkty, Y w górę, początek lewy-dolny (przed `/Rotate`). Widok = DIP WPF. **`PageTransform`** (Core, przetestowane) – jedyna obowiązująca matematyka (obrót łączny /Rotate + obrót widoku, skala = zoom·96/72). renderer-viewer zwraca ją przez `IDocumentView.GetPageTransform(page)` i testuje zgodność z PDFium (`FPDF_PageToDevice`).
- Narzędzia: `IViewerTool` (Id = id polecenia wstążki), aktywacja przez `IToolService.Activate`. Domyślne narzędzie: Ręka. Zdarzenia myszy/pióra jako `PagePointerEventArgs` (strona, punkt PDF, nacisk).
- Nakładki: `IPageOverlay.Render(DrawingContext, page, PageTransform)` z `ZIndex`: 100 komentarze, 200 formularze, 300 edycja, 400 redakcja, 900 aktywne narzędzie. Kolory z motywu (`ThemeKeys.Viewer*`), kolory treści (wybór użytkownika) – wolno.
- Zaznaczenie tekstu: `IDocumentView.TextSelection` (`PdfTextSelection` – spany ze stronami i czworokątami `PdfQuad`) → adiustacja (QuadPoints), redakcja, kopiowanie.
- Ukrywanie komentarzy (`Comment.HideAll`/`ShowAll`, annotations-forms) ustawia `IDocumentView.ShowAnnotations`: widok renderuje wtedy bez `RenderFlags.Annotations` (FPDF_ANNOT), z `RenderFlags.FormFields` (FPDF_FFLDraw rysuje tylko widżety) – plik się nie zmienia.

## 9. Kontrakty – mapa (implementacja → użytkownicy)

| Plik | Interfejs | Implementuje | Używają m.in. |
|---|---|---|---|
| Documents.cs | `IDocumentManager`, `IPdfDocumentSession`, `IUndoRedoService`, `IDocumentCommand`, `IMetadataService` | Core | wszyscy |
| Rendering.cs | `IPdfRenderer`, `IRenderDocument` | Rendering | Viewer, Ocr, Editing (redakcja, konwersje do obrazów), Security (porównanie graficzne), Pages (białe marginesy), Core (otwieranie) |
| Viewer.cs | `IDocumentViewFactory`, `IViewerService`, `IDocumentView`, `IToolService`, `IViewerTool`, `IPageOverlay`, `IBookmarkService`, `IPrintService` | Viewer | App, Annotations, Editing, Security |
| Shell.cs | `IFeatureModule`, `ICommandRegistry`, `IPanelRegistry`, `IShellService`, `IDialogService`, `ILocalizationService`, `IThemeService`, `ISettingsService`, `IRecentFilesService`, `IAppPaths`, `IFileService` | Core (rejestry, lokalizacja, ustawienia, ścieżki, pliki), App (powłoka, dialogi, motywy) | wszyscy |
| Pages.cs | `IPageOperations`, `IStampingService`, `IOptimizerService`, `ISanitizeService` | Pages | Viewer (miniatury: Move/Rotate/Delete/Duplicate/Insert/Extract), Security (Kreator), Annotations (Spłaszcz przy zapisie → `FlattenAsync`) |
| Annotations.cs | `IAnnotationService`, `IFormService`, `IAttachmentService` | Annotations | Security (pole podpisu: `IFormService.GetFieldsAsync`, `CreateFieldAsync(Signature)`), Editing (redakcja – adnotacje Redact przez własny kod) |
| Editing.cs | `ITextEditService`, `IObjectEditService`, `ILinkService`, `IConversionService`, `IRedactionService` | Editing | App (Utwórz), Security (Kreator) |
| Ocr.cs | `IOcrService` | Ocr | Editing (UI OCR), Security (Kreator) |
| Security.cs | `ISecurityService`, `ISignatureService`, `ICompareService`, `IActionWizard` | Security | Core (Analyze/Encrypt przy otwieraniu/zapisie), Annotations |

Konwencje kontraktów: indeksy stron 0-based; tekstowe zakresy stron („1-3,5”) przez `Core.Utilities.PageRange`; opcje = rekordy z `init` i wartościami domyślnymi (serializowalne `System.Text.Json` – Kreator zapisuje je w JSON); kolory treści `PdfColor`; wynik zmian = `byte[]`.

## 10. Lokalizacja

- `ILocalizationService` (Core): agreguje `ResourceManager`-y wszystkich modułów; `this[key]`, `Format(key, args)`, `SetCulture` → `PropertyChanged("Item[]")` (wszystkie wiązania odświeżają się na żywo), `CultureChanged` (dla tekstów ustawianych w kodzie – odśwież je w obsłudze zdarzenia).
- XAML: `xmlns:loc="clr-namespace:ZetPDF.Core.Localization;assembly=ZetPDF.Core"` → `Content="{loc:Loc Pages.Dialog.Title}"` (opcjonalnie `Format`). Kod: `loc["…"]`, `loc.Format("…", x)`.
- Pliki: `src\<Projekt>\Resources\Strings.resx` (angielski, domyślny) + `Strings.pl.resx` (polski). Każdy klucz w obu. Język domyślny: ustawienie lub systemowy (pl dla `pl-*`, inaczej en).
- **Prefiksy kluczy (unikalne między modułami – test integracyjny to sprawdza):** `Core.*`, `Shell.*` + `Cmd.*` + `Ribbon.*` (App), `Render.*`, `Viewer.*`, `Pages.*`, `Annot.*`, `Edit.*`, `Ocr.*`, `Security.*`.
- Konwencja: `<Prefiks>.<Obszar>.<Element>[.<Wariant>]`, np. `Pages.Watermark.Dialog.Title`, `Pages.Watermark.Opacity`, `Annot.Stamp.Approved`, `Edit.Redact.Warning.Irreversible`, `Security.Sign.Error.NoCertificate`. Opisy Cofnij: `<Prefiks>.Undo.<Operacja>`.
- Zakaz tekstów na sztywno w XAML i kodzie (także `MessageBox` z literałem). Teksty „dane”, np. stemple w PDF, też z zasobów (stempel PL/EN wg języka UI lub wyboru).

## 11. Motywy

- `IThemeService` (App): `System` / `Light` / `Dark`, przełączanie na żywo (słownik `Themes\Light|Dark.xaml` + motyw Fluent.Ribbon `Light.Blue`/`Dark.Blue`), nasłuch zmiany motywu Windows (`AppsUseLightTheme`).
- Kolory interfejsu wyłącznie `{DynamicResource <klucz>}` / `SetResourceReference` z kluczy `Core.Theming.ThemeKeys`:
  `Brush.Window.Background`, `Brush.Surface`, `Brush.Surface.Alt`, `Brush.Surface.Raised`, `Brush.Text.Primary`, `Brush.Text.Secondary`, `Brush.Text.Disabled`, `Brush.Text.OnAccent`, `Brush.Text.Link`, `Brush.Border`, `Brush.Border.Strong`, `Brush.Focus`, `Brush.Accent`, `Brush.Accent.Hover`, `Brush.Accent.Pressed`, `Brush.Control.Background`, `Brush.Control.Background.Hover`, `Brush.Control.Background.Pressed`, `Brush.Control.Border`, `Brush.Input.Background`, `Brush.Input.Border`, `Brush.Selection.Background`, `Brush.Selection.Text`, `Brush.Scrollbar.Thumb`, `Brush.Scrollbar.Track`, `Brush.Tooltip.Background`, `Brush.Tooltip.Text`, `Brush.StatusBar.Background`, `Brush.StatusBar.Text`, `Brush.Viewer.Background`, `Brush.Viewer.PageBorder`, `Brush.Viewer.PageShadow`, `Brush.Viewer.SearchHit`, `Brush.Viewer.SearchHit.Current`, `Brush.Viewer.TextSelection`, `Brush.Viewer.Overlay.Outline`, `Brush.Viewer.Overlay.Handle`, `Brush.Viewer.FormField.Highlight`, `Brush.Viewer.RedactionMark`, `Brush.Status.Error`, `Brush.Status.Warning`, `Brush.Status.Success`, `Brush.Status.Info`, `Brush.Diff.Inserted`, `Brush.Diff.Deleted`, `Brush.Diff.Changed`; czcionki `Font.Family.Ui`, `Font.Family.Icons`, `Font.Family.Mono`.
- ui-shell dostarcza style domyślne kontrolek (Window, Button, TextBox, ComboBox, CheckBox, RadioButton, ListBox, TreeView, ScrollBar, ToolTip, ContextMenu, TabControl, DataGrid, ProgressBar, Slider) w `Themes\Styles.xaml`, więc okna modułów nie ustawiają kolorów. Kontrast tekstu ≥ 4.5:1 (test).
- Wyjątki od zakazu kolorów: kolory treści PDF wybrane przez użytkownika (`PdfColor`) i czerń redakcji.

## 12. Ustawienia, ścieżki, dziennik

- `ISettingsService` (Core): `%APPDATA%\ZetPDF\settings.json` – `{"General": AppSettings, "Modules": {...}}`; zapis atomowy. Pola ogólne w `AppSettings` (język, motyw, tożsamość autora, powiększenie/układ, klawisze jednoliterowe, TSA, folder tymczasowy, poziomy Cofnij, ostatnie pliki, okno, QAT, próg OCR). Moduły: `GetModuleSettings<T>("Annotations")`.
- `IAppPaths`: AppData (`signatures`, `stamps`, `actions`, `digital-ids`), LocalAppData (`logs`, `ocr`), Temp (`%TEMP%\ZetPDF\<pid>`, czyszczony przy wyjściu). `ZETPDF_HOME` przekierowuje wszystko (testy).
- Dziennik: `%LOCALAPPDATA%\ZetPDF\logs\zetpdf-yyyyMMdd.log` (`ILogger<T>` z DI; retencja 14 dni). Żadnej telemetrii, żadnej sieci (wyjątek: TSA na żądanie użytkownika).

## 13. Konwencje kodu i nazw

- Przestrzenie nazw = nazwa projektu + folder (`ZetPDF.Pages.Watermark`). Pliki: jedna klasa publiczna na plik, nazwa pliku = nazwa klasy; okna `*Window.xaml`/`*Dialog.xaml` + `*ViewModel.cs` (CommunityToolkit.Mvvm: `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`).
- Usługi: `I<Nazwa>Service` w Core, implementacja `<Nazwa>Service` w module, rejestracja singleton w `*Module.ConfigureServices`.
- Okna modułów pokazuj przez `IDialogService.ShowDialog(window)` (ustawia Owner). Wszystkie operacje dłuższe niż ~200 ms – asynchronicznie, z postępem (`IDialogService.RunWithProgressAsync`) i anulowaniem.
- PDFsharp przez `Core.Documents.PdfSharpEdit` (Modify/Read/Save). Zabezpieczenia: `PdfSecurityHelper` (referencja).
- Nazwy `TextBlock`, `TextSelection` w kontraktach są zastrzeżone dla WPF – kontrakty używają przedrostka `Pdf` (`PdfTextBlock`, `PdfTextSelection`).
- `System.IO` jest globalnym using (Directory.Build.targets); w kodzie WPF z `System.Windows.Shapes` pisz `System.IO.Path` jawnie.

## 14. Testy

- `dotnet test ZetPDF.sln -c Release`. Projekty testowe per moduł (tabela w p. 3) – agent pisze testy swojego silnika we własnym projekcie, bez konfliktów.
- `tests\ZetPDF.TestData\SamplePdfFactory`: `Text(n)`, `Large(500)`, `EncryptedAes256()` (hasła `SamplePdfFactory.UserPassword`/`OwnerPassword`, bez modyfikacji/kopiowania), `WithBookmarks()`, `WithAnnotations()`, `WithForm()` (tekst `imie`, pole wyboru `zgoda`, opcje `plec` k/m, lista `miasto`), `WithSensitiveData()` (PESEL/NIP/e-mail/telefon/karta/IBAN – fikcyjne, poprawne sumy), `WithMetadataAttachmentAndJavaScript()`, `Scanned()` (obraz tekstu bez warstwy tekstu, render WPF na wątku STA), `Corrupted()`, `NotAPdf()`; `TestPaths.WriteSample()` – folder `Próbny folder`, plik `zażółć gęślą.pdf`.
- Testy szkieletu (77, wszystkie zielone): katalog i skróty, PageRange, PageTransform, sesja (Cofnij/Ponów, limit, budżet → dysk, uprawnienia, szyfrowanie), DocumentManager (polska ścieżka, AES-256 z hasłem użytkownika/właściciela, uszkodzone pliki), ustawienia, lokalizacja (na żywo, pl=en), integracja (każdy kontrakt ma implementację w DI, klucze pl/en we wszystkich `.resx`, brak duplikatów kluczy między modułami, etykiety wszystkich poleceń/kart/grup, słowniki motywów mają wszystkie klucze), OCR (natywne DLL + tessdata z zasobów).

## 15. Publikacja

```
dotnet publish src\ZetPDF.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```
Sprawdzone na szkielecie: powstaje jeden `ZetPDF.exe` (~91 MB, + pliki `.pdb`; można je pominąć `-p:DebugType=None`), uruchamia się. PDFium – w pakiecie (rozpakowywane automatycznie). Tesseract + tessdata – zasoby `ZetPDF.Ocr.dll`, rozpakowywane przy pierwszym OCR do `%LOCALAPPDATA%\ZetPDF\ocr\5.2.0\` (bez tego Tesseract nie działa w single-file). Jeśli kiedyś trzeba będzie zrezygnować z tego obejścia, `dist\` musi zawierać `x64\tesseract50.dll`, `x64\leptonica-1.82.0.dll` i `tessdata\` obok exe.

## Załącznik A. Wstążka – karty, grupy, polecenia, moduły, skróty

> Kopia `docs/WSTAZKA.md` z chwili tworzenia szkieletu (źródło: `tools/catalog/catalog.py`). Przy rozbieżności obowiązuje `src/ZetPDF.Core/Commands/RibbonCatalog.g.cs`.


> Wygenerowano przez `tools/catalog/catalog.py` (jedyne źródło prawdy: ten skrypt → `src/ZetPDF.Core/Commands/*.g.cs`). Teksty: klucz `Cmd.<Id>` (etykieta), `Cmd.<Id>.Tip` (podpowiedź, opcjonalnie), `Ribbon.Tab.<Id>`, `Ribbon.Group.<Id>` w `src/ZetPDF.App/Resources/Strings*.resx` (ui-shell).
> Skróty w notacji WPF `KeyGesture` (`D0` = klawisz 0, `OemPlus` = `+`). Kilka skrótów rozdzielonych `;`. Klawisze jednoliterowe działają tylko po włączeniu w Ustawieniach i nigdy, gdy fokus jest w polu tekstowym.

### Pasek szybkiego dostępu

`File.Open`, `File.Save`, `Edit.Undo`, `Edit.Redo`, `File.Print`

### Plik (backstage)

| Id | PL | EN | Moduł (agent) | Skrót |
|---|---|---|---|---|
| `File.Open` | Otwórz | Open | Shell (ui-shell) | Ctrl+O |
| `File.Save` | Zapisz | Save | Shell (ui-shell) | Ctrl+S |
| `File.SaveAs` | Zapisz jako | Save As | Shell (ui-shell) | Ctrl+Shift+S |
| `File.Revert` | Przywróć | Revert | Shell (ui-shell) |  |
| `File.Close` | Zamknij | Close | Shell (ui-shell) | Ctrl+W, Ctrl+F4 |
| `File.CloseAll` | Zamknij wszystko | Close All | Shell (ui-shell) | Ctrl+Shift+W |
| `File.Properties` | Właściwości dokumentu | Document Properties | Shell (ui-shell) | Ctrl+D |
| `File.Print` | Drukuj | Print | Viewer (renderer-viewer) | Ctrl+P |
| `File.CreateFromFile` | Z pliku | From File | Editing (edit-convert-ocr) | Ctrl+N |
| `File.CombineFiles` | Połącz pliki | Combine Files | Editing (edit-convert-ocr) |  |
| `File.CreateBlank` | Pusty dokument | Blank Document | Pages (pages-engine) |  |
| `File.CreateFromClipboard` | Ze schowka | From Clipboard | Editing (edit-convert-ocr) |  |
| `File.OptimizeReduceSize` | Zmniejsz rozmiar pliku | Reduce File Size | Pages (pages-engine) |  |
| `File.OptimizeAdvanced` | Optymalizacja zaawansowana | Advanced Optimization | Pages (pages-engine) |  |
| `File.ActionWizard` | Kreator działań | Action Wizard | Security (security-signatures) |  |
| `File.Settings` | Ustawienia | Settings | Shell (ui-shell) | Ctrl+K |
| `File.Exit` | Zakończ | Exit | Shell (ui-shell) | Ctrl+Q |

### Karta `Home` – Początek / Home

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `Home.Tools` Narzędzia | `Tool.Hand` | Ręka | Hand | Viewer (renderer-viewer) | Toggle |  | H |
| `Home.Tools` Narzędzia | `Tool.SelectText` | Zaznacz tekst | Select Text | Viewer (renderer-viewer) | Toggle |  | V |
| `Home.Tools` Narzędzia | `Tool.SelectAnnotation` | Wybierz adnotację | Select Annotation | Annotations (annotations-forms) | Toggle |  | R |
| `Home.Tools` Narzędzia | `Tool.Snapshot` | Migawka | Snapshot | Viewer (renderer-viewer) | Toggle |  |  |
| `Home.Zoom` Powiększenie | `View.ZoomIn` | Powiększ | Zoom In | Viewer (renderer-viewer) | Button | Ctrl+OemPlus, Ctrl+Add |  |
| `Home.Zoom` Powiększenie | `View.ZoomOut` | Pomniejsz | Zoom Out | Viewer (renderer-viewer) | Button | Ctrl+OemMinus, Ctrl+Subtract |  |
| `Home.Zoom` Powiększenie | `View.ZoomTo` | Powiększ do… | Zoom To… | Viewer (renderer-viewer) | Button | Ctrl+M |  |
| `Home.Zoom` Powiększenie | `View.FitPage` | Dopasuj stronę | Fit Page | Viewer (renderer-viewer) | Button | Ctrl+D0 |  |
| `Home.Zoom` Powiększenie | `View.FitWidth` | Dopasuj szerokość | Fit Width | Viewer (renderer-viewer) | Button | Ctrl+D2 |  |
| `Home.Zoom` Powiększenie | `View.FitVisible` | Dopasuj widoczne | Fit Visible | Viewer (renderer-viewer) | Button | Ctrl+D3 |  |
| `Home.Zoom` Powiększenie | `View.ActualSize` | Rzeczywisty rozmiar | Actual Size | Viewer (renderer-viewer) | Button | Ctrl+D1 |  |
| `Home.View` Widok | `View.RotateLeft` | Obróć widok w lewo | Rotate View Left | Viewer (renderer-viewer) | Button | Ctrl+Shift+OemMinus |  |
| `Home.View` Widok | `View.RotateRight` | Obróć widok w prawo | Rotate View Right | Viewer (renderer-viewer) | Button | Ctrl+Shift+OemPlus |  |
| `Home.Edit` Edycja | `Edit.EditText` | Edytuj tekst | Edit Text | Editing (edit-convert-ocr) | Toggle |  | T |
| `Home.Edit` Edycja | `Edit.EditObject` | Edytuj obiekt | Edit Object | Editing (edit-convert-ocr) | Toggle |  | O |
| `Home.Edit` Edycja | `Comment.Typewriter` | Maszyna do pisania | Typewriter | Annotations (annotations-forms) | Toggle | Shift+T |  |
| `Home.Edit` Edycja | `Comment.Highlight` | Wyróżnij | Highlight | Annotations (annotations-forms) | Toggle | Shift+H |  |
| `Home.Pages` Strony | `Pages.InsertFromFile` | Wstaw z pliku | Insert from File | Pages (pages-engine) | Button | Ctrl+Shift+I |  |
| `Home.Pages` Strony | `Pages.InsertBlank` | Pusta strona | Blank Page | Pages (pages-engine) | Button | Ctrl+Shift+T |  |
| `Home.Pages` Strony | `Nav.AddBookmark` | Dodaj zakładkę | Add Bookmark | Viewer (renderer-viewer) | Button | Ctrl+B |  |
| `Home.Recognize` Rozpoznawanie | `Ocr.QuickRecognize` | Szybkie rozpoznawanie | Quick Recognition | Editing (edit-convert-ocr) | Button |  |  |
| `Home.Sign` Podpis | `FillSign.Open` | Wypełnij i podpisz | Fill & Sign | Annotations (annotations-forms) | Button |  |  |

### Karta `Convert` – Konwertuj / Convert

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `Convert.Create` Utwórz | `File.CreateFromFile` | Z pliku | From File | Editing (edit-convert-ocr) | Button | Ctrl+N |  |
| `Convert.Create` Utwórz | `File.CombineFiles` | Połącz pliki | Combine Files | Editing (edit-convert-ocr) | Button |  |  |
| `Convert.Create` Utwórz | `File.CreateBlank` | Pusty dokument | Blank Document | Pages (pages-engine) | Button |  |  |
| `Convert.Create` Utwórz | `File.CreateFromClipboard` | Ze schowka | From Clipboard | Editing (edit-convert-ocr) | Button |  |  |
| `Convert.Export` Eksport | `Convert.ToImages` | Do obrazów | To Images | Editing (edit-convert-ocr) | Button |  |  |
| `Convert.Export` Eksport | `Convert.ToText` | Do tekstu (TXT) | To Text (TXT) | Editing (edit-convert-ocr) | Button |  |  |
| `Convert.Export` Eksport | `Convert.ToDocx` | Do Word (DOCX) | To Word (DOCX) | Editing (edit-convert-ocr) | Button |  |  |
| `Convert.Ocr` OCR | `Ocr.RecognizeDocument` | Rozpoznaj tekst (OCR) | Recognize Text (OCR) | Editing (edit-convert-ocr) | Button |  |  |
| `Convert.Ocr` OCR | `Ocr.RecognizeRegion` | OCR zaznaczonego obszaru | OCR Selected Area | Editing (edit-convert-ocr) | Button |  |  |
| `Convert.Ocr` OCR | `Ocr.QuickRecognize` | Szybkie rozpoznawanie | Quick Recognition | Editing (edit-convert-ocr) | Button |  |  |
| `Convert.Ocr` OCR | `Ocr.SuspectResults` | Podejrzane wyniki | Suspect Results | Editing (edit-convert-ocr) | Button |  |  |
| `Convert.Color` Kolor | `Convert.Grayscale` | Skala szarości | Grayscale | Pages (pages-engine) | Button |  |  |

### Karta `Edit` – Edytuj / Edit

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `Edit.Content` Edycja treści | `Edit.EditText` | Edytuj tekst | Edit Text | Editing (edit-convert-ocr) | Toggle |  | T |
| `Edit.Content` Edycja treści | `Edit.EditObject` | Edytuj obiekt | Edit Object | Editing (edit-convert-ocr) | Toggle |  | O |
| `Edit.Content` Edycja treści | `Edit.AddText` | Dodaj tekst | Add Text | Editing (edit-convert-ocr) | Toggle |  |  |
| `Edit.Content` Edycja treści | `Edit.AddImage` | Dodaj obraz | Add Image | Editing (edit-convert-ocr) | Button |  |  |
| `Edit.Content` Edycja treści | `Edit.AddShape` | Dodaj kształt | Add Shape | Editing (edit-convert-ocr) | Menu |  |  |
| `Edit.Content` Edycja treści | `Edit.FindReplace` | Wyszukaj i zamień | Find and Replace | Editing (edit-convert-ocr) | Button | Ctrl+T |  |
| `Edit.Links` Łącza i nawigacja | `Edit.Link` | Łącze | Link | Editing (edit-convert-ocr) | Toggle |  | L |
| `Edit.Links` Łącza i nawigacja | `Edit.CreateLinksFromUrls` | Utwórz łącza z adresów URL | Create Links from URLs | Editing (edit-convert-ocr) | Button |  |  |
| `Edit.Links` Łącza i nawigacja | `Edit.RemoveAllLinks` | Usuń wszystkie łącza | Remove All Links | Editing (edit-convert-ocr) | Button |  |  |
| `Edit.Links` Łącza i nawigacja | `Nav.AddBookmark` | Dodaj zakładkę | Add Bookmark | Viewer (renderer-viewer) | Button | Ctrl+B |  |
| `Edit.Links` Łącza i nawigacja | `Edit.BookmarksFromHeadings` | Zakładki z nagłówków | Bookmarks from Headings | Editing (edit-convert-ocr) | Button |  |  |
| `Edit.Attachments` Załączniki | `Attach.AddFile` | Załącz plik | Attach File | Annotations (annotations-forms) | Button |  |  |

### Karta `Organize` – Organizuj / Organize

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `Organize.Pages` Strony | `Pages.InsertFromFile` | Wstaw z pliku | Insert from File | Pages (pages-engine) | Button | Ctrl+Shift+I |  |
| `Organize.Pages` Strony | `Pages.InsertBlank` | Pusta strona | Blank Page | Pages (pages-engine) | Button | Ctrl+Shift+T |  |
| `Organize.Pages` Strony | `Pages.Delete` | Usuń strony | Delete Pages | Pages (pages-engine) | Button | Ctrl+Shift+D |  |
| `Organize.Pages` Strony | `Pages.Extract` | Wyodrębnij | Extract | Pages (pages-engine) | Button |  |  |
| `Organize.Pages` Strony | `Pages.Replace` | Zastąp | Replace | Pages (pages-engine) | Button |  |  |
| `Organize.Pages` Strony | `Pages.Duplicate` | Duplikuj | Duplicate | Pages (pages-engine) | Button | Ctrl+Alt+D |  |
| `Organize.Pages` Strony | `Pages.Move` | Przenieś | Move | Pages (pages-engine) | Button |  |  |
| `Organize.Pages` Strony | `Pages.Swap` | Zamień | Swap | Pages (pages-engine) | Button |  |  |
| `Organize.Pages` Strony | `Pages.Reverse` | Odwróć kolejność | Reverse Order | Pages (pages-engine) | Button |  |  |
| `Organize.Pages` Strony | `Pages.Interleave` | Przeplatanie | Interleave | Pages (pages-engine) | Button |  |  |
| `Organize.Transform` Podział i przekształcenia | `Pages.Split` | Podziel dokument | Split Document | Pages (pages-engine) | Button |  |  |
| `Organize.Transform` Podział i przekształcenia | `Pages.Merge` | Scal pliki PDF | Merge PDFs | Pages (pages-engine) | Button |  |  |
| `Organize.Transform` Podział i przekształcenia | `Pages.RotateLeft` | Obróć w lewo | Rotate Left | Pages (pages-engine) | Button |  |  |
| `Organize.Transform` Podział i przekształcenia | `Pages.RotateRight` | Obróć w prawo | Rotate Right | Pages (pages-engine) | Button |  |  |
| `Organize.Transform` Podział i przekształcenia | `Pages.Rotate` | Obracanie stron… | Rotate Pages… | Pages (pages-engine) | Button |  |  |
| `Organize.Transform` Podział i przekształcenia | `Pages.Crop` | Kadruj strony | Crop Pages | Pages (pages-engine) | Button |  | C |
| `Organize.Transform` Podział i przekształcenia | `Pages.Resize` | Zmień rozmiar stron | Resize Pages | Pages (pages-engine) | Button |  |  |
| `Organize.Transform` Podział i przekształcenia | `Pages.Flatten` | Spłaszcz | Flatten | Pages (pages-engine) | Button |  |  |
| `Organize.Marks` Oznaczenia stron | `Stamp.Watermark` | Znak wodny | Watermark | Pages (pages-engine) | Menu |  |  |
| ↳ menu | `Stamp.Watermark.Add` | Dodaj znak wodny | Add Watermark | Pages (pages-engine) | Button |  |  |
| ↳ menu | `Stamp.Watermark.Update` | Aktualizuj znak wodny | Update Watermark | Pages (pages-engine) | Button |  |  |
| ↳ menu | `Stamp.Watermark.RemoveAll` | Usuń wszystkie znaki wodne | Remove All Watermarks | Pages (pages-engine) | Button |  |  |
| `Organize.Marks` Oznaczenia stron | `Stamp.Background` | Tło | Background | Pages (pages-engine) | Menu |  |  |
| ↳ menu | `Stamp.Background.Add` | Dodaj tło | Add Background | Pages (pages-engine) | Button |  |  |
| ↳ menu | `Stamp.Background.Update` | Aktualizuj tło | Update Background | Pages (pages-engine) | Button |  |  |
| ↳ menu | `Stamp.Background.RemoveAll` | Usuń wszystkie tła | Remove All Backgrounds | Pages (pages-engine) | Button |  |  |
| `Organize.Marks` Oznaczenia stron | `Stamp.HeaderFooter` | Nagłówek i stopka | Header & Footer | Pages (pages-engine) | Menu |  |  |
| ↳ menu | `Stamp.HeaderFooter.Add` | Dodaj nagłówek i stopkę | Add Header & Footer | Pages (pages-engine) | Button |  |  |
| ↳ menu | `Stamp.HeaderFooter.Update` | Aktualizuj nagłówek i stopkę | Update Header & Footer | Pages (pages-engine) | Button |  |  |
| ↳ menu | `Stamp.HeaderFooter.RemoveAll` | Usuń nagłówki i stopki | Remove Headers & Footers | Pages (pages-engine) | Button |  |  |
| `Organize.Marks` Oznaczenia stron | `Stamp.PageNumbers` | Numeracja stron | Page Numbers | Pages (pages-engine) | Button |  |  |
| `Organize.Marks` Oznaczenia stron | `Stamp.Bates` | Numeracja Bates | Bates Numbering | Pages (pages-engine) | Menu |  |  |
| ↳ menu | `Stamp.Bates.Add` | Dodaj numerację Bates | Add Bates Numbering | Pages (pages-engine) | Button |  |  |
| ↳ menu | `Stamp.Bates.RemoveAll` | Usuń numerację Bates | Remove Bates Numbering | Pages (pages-engine) | Button |  |  |
| `Organize.Marks` Oznaczenia stron | `Pages.PageLabels` | Format numerów stron | Page Number Format | Pages (pages-engine) | Button |  |  |

### Karta `Comment` – Komentarz / Comment

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `Comment.Markup` Adiustacja tekstu | `Comment.Highlight` | Wyróżnij | Highlight | Annotations (annotations-forms) | Toggle | Shift+H |  |
| `Comment.Markup` Adiustacja tekstu | `Comment.AreaHighlight` | Wyróżnienie obszaru | Area Highlight | Annotations (annotations-forms) | Toggle |  |  |
| `Comment.Markup` Adiustacja tekstu | `Comment.Underline` | Podkreśl | Underline | Annotations (annotations-forms) | Toggle | Ctrl+U |  |
| `Comment.Markup` Adiustacja tekstu | `Comment.Squiggly` | Podkreślenie falowane | Squiggly Underline | Annotations (annotations-forms) | Toggle | Shift+U |  |
| `Comment.Markup` Adiustacja tekstu | `Comment.StrikeOut` | Przekreśl | Strikethrough | Annotations (annotations-forms) | Toggle | Shift+S |  |
| `Comment.Text` Notatki i tekst | `Comment.Note` | Notatka | Note | Annotations (annotations-forms) | Toggle | Ctrl+Shift+N | S |
| `Comment.Text` Notatki i tekst | `Comment.Typewriter` | Maszyna do pisania | Typewriter | Annotations (annotations-forms) | Toggle | Shift+T |  |
| `Comment.Text` Notatki i tekst | `Comment.TextBox` | Pole tekstowe | Text Box | Annotations (annotations-forms) | Toggle | Shift+E | X |
| `Comment.Text` Notatki i tekst | `Comment.Callout` | Objaśnienie | Callout | Annotations (annotations-forms) | Toggle | Shift+C |  |
| `Comment.Drawing` Rysowanie | `Comment.Rectangle` | Prostokąt | Rectangle | Annotations (annotations-forms) | Toggle |  |  |
| `Comment.Drawing` Rysowanie | `Comment.Oval` | Owal | Oval | Annotations (annotations-forms) | Toggle |  |  |
| `Comment.Drawing` Rysowanie | `Comment.Line` | Linia | Line | Annotations (annotations-forms) | Toggle |  |  |
| `Comment.Drawing` Rysowanie | `Comment.Arrow` | Strzałka | Arrow | Annotations (annotations-forms) | Toggle |  |  |
| `Comment.Drawing` Rysowanie | `Comment.Polygon` | Wielokąt | Polygon | Annotations (annotations-forms) | Toggle |  |  |
| `Comment.Drawing` Rysowanie | `Comment.Polyline` | Linia łamana | Polyline | Annotations (annotations-forms) | Toggle |  |  |
| `Comment.Drawing` Rysowanie | `Comment.Pencil` | Ołówek | Pencil | Annotations (annotations-forms) | Toggle | Shift+P |  |
| `Comment.Stamps` Stemple | `Comment.Stamp` | Stempel | Stamp | Annotations (annotations-forms) | Menu |  | K |
| `Comment.Stamps` Stemple | `Comment.CustomStamp` | Stempel z obrazu | Stamp from Image | Annotations (annotations-forms) | Button |  |  |
| `Comment.Manage` Zarządzanie | `Comment.Import` | Importuj komentarze | Import Comments | Annotations (annotations-forms) | Button |  |  |
| `Comment.Manage` Zarządzanie | `Comment.ExportAll` | Eksportuj komentarze | Export Comments | Annotations (annotations-forms) | Button | Ctrl+L |  |
| `Comment.Manage` Zarządzanie | `Comment.ShowAll` | Pokaż wszystkie komentarze | Show All Comments | Annotations (annotations-forms) | Button | Ctrl+D8 |  |
| `Comment.Manage` Zarządzanie | `Comment.HideAll` | Ukryj wszystkie komentarze | Hide All Comments | Annotations (annotations-forms) | Button | Ctrl+Shift+D8 |  |
| `Comment.Manage` Zarządzanie | `Comment.DeleteAll` | Usuń wszystkie komentarze | Delete All Comments | Annotations (annotations-forms) | Button |  |  |
| `Comment.Manage` Zarządzanie | `View.PanelComments` | Komentarze | Comments | Shell (ui-shell) | Button |  |  |

### Karta `View` – Widok / View

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `View.Layout` Układ stron | `View.SinglePage` | Jedna strona | Single Page | Viewer (renderer-viewer) | Toggle |  |  |
| `View.Layout` Układ stron | `View.Continuous` | Ciągły | Continuous | Viewer (renderer-viewer) | Toggle |  |  |
| `View.Layout` Układ stron | `View.TwoPages` | Dwie strony | Two Pages | Viewer (renderer-viewer) | Toggle |  |  |
| `View.Layout` Układ stron | `View.TwoPagesContinuous` | Dwie strony ciągłe | Two Pages Continuous | Viewer (renderer-viewer) | Toggle |  |  |
| `View.Mode` Tryb | `View.ReadingMode` | Tryb odczytu | Reading Mode | Viewer (renderer-viewer) | Toggle | Ctrl+H |  |
| `View.Mode` Tryb | `View.FullScreen` | Pełny ekran | Full Screen | Viewer (renderer-viewer) | Toggle | F11 |  |
| `View.Mode` Tryb | `View.RotateLeft` | Obróć widok w lewo | Rotate View Left | Viewer (renderer-viewer) | Button | Ctrl+Shift+OemMinus |  |
| `View.Mode` Tryb | `View.RotateRight` | Obróć widok w prawo | Rotate View Right | Viewer (renderer-viewer) | Button | Ctrl+Shift+OemPlus |  |
| `View.Navigation` Nawigacja | `Nav.GoToPage` | Przejdź do strony | Go to Page | Viewer (renderer-viewer) | Button | Ctrl+G |  |
| `View.Navigation` Nawigacja | `Nav.FirstPage` | Pierwsza strona | First Page | Viewer (renderer-viewer) | Button | Ctrl+Home |  |
| `View.Navigation` Nawigacja | `Nav.PreviousPage` | Poprzednia strona | Previous Page | Viewer (renderer-viewer) | Button | Ctrl+PageUp |  |
| `View.Navigation` Nawigacja | `Nav.NextPage` | Następna strona | Next Page | Viewer (renderer-viewer) | Button | Ctrl+PageDown |  |
| `View.Navigation` Nawigacja | `Nav.LastPage` | Ostatnia strona | Last Page | Viewer (renderer-viewer) | Button | Ctrl+End |  |
| `View.Find` Wyszukiwanie | `Nav.Find` | Znajdź | Find | Viewer (renderer-viewer) | Button | Ctrl+F |  |
| `View.Find` Wyszukiwanie | `Nav.FindNext` | Znajdź następny | Find Next | Viewer (renderer-viewer) | Button | F3 |  |
| `View.Find` Wyszukiwanie | `Nav.FindPrevious` | Znajdź poprzedni | Find Previous | Viewer (renderer-viewer) | Button | Shift+F3 |  |
| `View.Compare` Porównanie | `Compare.Start` | Porównaj dokumenty | Compare Documents | Security (security-signatures) | Button |  |  |
| `View.Panels` Panele | `View.TogglePanel` | Panel boczny | Side Panel | Shell (ui-shell) | Toggle | F4 |  |
| `View.Panels` Panele | `View.PanelThumbnails` | Miniatury | Thumbnails | Shell (ui-shell) | Button |  |  |
| `View.Panels` Panele | `View.PanelBookmarks` | Zakładki | Bookmarks | Shell (ui-shell) | Button |  |  |
| `View.Panels` Panele | `View.PanelComments` | Komentarze | Comments | Shell (ui-shell) | Button |  |  |
| `View.Panels` Panele | `View.PanelAttachments` | Załączniki | Attachments | Shell (ui-shell) | Button |  |  |
| `View.Panels` Panele | `View.PanelSignatures` | Podpisy | Signatures | Shell (ui-shell) | Button |  |  |
| `View.Panels` Panele | `View.ToggleRibbon` | Zwiń wstążkę | Collapse Ribbon | Shell (ui-shell) | Toggle | Ctrl+F11 |  |

### Karta `Form` – Formularz / Form

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `Form.Fields` Pola formularza | `Form.TextField` | Pole tekstowe | Text Field | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Fields` Pola formularza | `Form.CheckBox` | Pole wyboru | Check Box | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Fields` Pola formularza | `Form.RadioButton` | Przycisk opcji | Radio Button | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Fields` Pola formularza | `Form.ComboBox` | Pole kombi | Combo Box | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Fields` Pola formularza | `Form.ListBox` | Pole listy | List Box | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Fields` Pola formularza | `Form.Button` | Przycisk | Button | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Fields` Pola formularza | `Form.SignatureField` | Pole podpisu | Signature Field | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Fields` Pola formularza | `Form.DateField` | Pole daty | Date Field | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Design` Projektowanie | `Tool.SelectAnnotation` | Wybierz adnotację | Select Annotation | Annotations (annotations-forms) | Toggle |  | R |
| `Form.Design` Projektowanie | `Form.TabOrder` | Kolejność tabulacji | Tab Order | Annotations (annotations-forms) | Button |  |  |
| `Form.Design` Projektowanie | `Form.HighlightFields` | Wyróżnij pola | Highlight Fields | Annotations (annotations-forms) | Toggle |  |  |
| `Form.Data` Dane | `Form.ImportData` | Importuj dane | Import Data | Annotations (annotations-forms) | Button |  |  |
| `Form.Data` Dane | `Form.ExportData` | Eksportuj dane | Export Data | Annotations (annotations-forms) | Button |  |  |
| `Form.Data` Dane | `Form.Reset` | Resetuj formularz | Reset Form | Annotations (annotations-forms) | Button |  |  |

### Karta `Protect` – Ochrona / Protect

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `Protect.Security` Zabezpieczenia | `Security.PasswordProtect` | Ochrona hasłem | Password Protection | Security (security-signatures) | Button |  |  |
| `Protect.Security` Zabezpieczenia | `Security.RemoveSecurity` | Usuń zabezpieczenia | Remove Security | Security (security-signatures) | Button |  |  |
| `Protect.Security` Zabezpieczenia | `Security.Properties` | Właściwości zabezpieczeń | Security Properties | Security (security-signatures) | Button |  |  |
| `Protect.Redaction` Redakcja | `Redact.MarkText` | Zaznacz tekst do redakcji | Mark Text for Redaction | Editing (edit-convert-ocr) | Toggle |  |  |
| `Protect.Redaction` Redakcja | `Redact.MarkArea` | Zaznacz obszar do redakcji | Mark Area for Redaction | Editing (edit-convert-ocr) | Toggle |  |  |
| `Protect.Redaction` Redakcja | `Redact.SearchAndRedact` | Wyszukaj i redaguj | Search & Redact | Editing (edit-convert-ocr) | Button |  |  |
| `Protect.Redaction` Redakcja | `Redact.Apply` | Zastosuj redakcje | Apply Redactions | Editing (edit-convert-ocr) | Button |  |  |
| `Protect.HiddenData` Ukryte dane | `Sanitize.Document` | Oczyszczanie dokumentu | Sanitize Document | Pages (pages-engine) | Button |  |  |
| `Protect.Signatures` Podpisy | `FillSign.Open` | Wypełnij i podpisz | Fill & Sign | Annotations (annotations-forms) | Button |  |  |
| `Protect.Signatures` Podpisy | `Sign.PlaceSignature` | Podpisz cyfrowo | Sign Digitally | Security (security-signatures) | Button |  |  |
| `Protect.Signatures` Podpisy | `Sign.Validate` | Weryfikuj podpisy | Validate Signatures | Security (security-signatures) | Button |  |  |
| `Protect.Signatures` Podpisy | `Sign.Timestamp` | Znacznik czasu dokumentu | Document Timestamp | Security (security-signatures) | Button |  |  |
| `Protect.Signatures` Podpisy | `Sign.DigitalIds` | Identyfikatory cyfrowe | Digital IDs | Security (security-signatures) | Button |  |  |

### Karta `Help` – Pomoc / Help

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `Help.Main` Pomoc | `Help.Shortcuts` | Skróty klawiszowe | Keyboard Shortcuts | Shell (ui-shell) | Button | F1 |  |
| `Help.Main` Pomoc | `Help.Licenses` | Licencje bibliotek | Third-party Licenses | Shell (ui-shell) | Button |  |  |
| `Help.Main` Pomoc | `Help.OpenLogFolder` | Folder dziennika | Log Folder | Shell (ui-shell) | Button |  |  |
| `Help.Main` Pomoc | `Help.About` | O programie | About | Shell (ui-shell) | Button |  |  |

### Karta `FillSign` – Wypełnij i podpisz / Fill & Sign (kontekstowa)

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `FillSign.Tools` Narzędzia | `FillSign.Text` | Tekst | Text | Annotations (annotations-forms) | Toggle |  |  |
| `FillSign.Tools` Narzędzia | `FillSign.Check` | Znacznik ✓ | Check Mark ✓ | Annotations (annotations-forms) | Toggle |  |  |
| `FillSign.Tools` Narzędzia | `FillSign.Cross` | Krzyżyk ✗ | Cross ✗ | Annotations (annotations-forms) | Toggle |  |  |
| `FillSign.Tools` Narzędzia | `FillSign.Dot` | Kropka | Dot | Annotations (annotations-forms) | Toggle |  |  |
| `FillSign.Tools` Narzędzia | `FillSign.Line` | Linia | Line | Annotations (annotations-forms) | Toggle |  |  |
| `FillSign.Signatures` Podpisy | `FillSign.Signature` | Podpis odręczny | Handwritten Signature | Annotations (annotations-forms) | Menu |  |  |
| `FillSign.Signatures` Podpisy | `FillSign.ManageSignatures` | Zarządzaj podpisami | Manage Signatures | Annotations (annotations-forms) | Button |  |  |
| `FillSign.Signatures` Podpisy | `FillSign.Flatten` | Spłaszcz przy zapisie | Flatten on Save | Annotations (annotations-forms) | Toggle |  |  |
| `FillSign.CloseGroup` Zamknij | `FillSign.Close` | Zamknij Wypełnij i podpisz | Close Fill & Sign | Annotations (annotations-forms) | Button |  |  |

### Karta `CompareResults` – Wyniki porównania / Compare Results (kontekstowa)

| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |
|---|---|---|---|---|---|---|---|
| `CompareResults.Navigate` Zmiany | `Compare.Previous` | Poprzednia zmiana | Previous Change | Security (security-signatures) | Button | Ctrl+OemComma |  |
| `CompareResults.Navigate` Zmiany | `Compare.Next` | Następna zmiana | Next Change | Security (security-signatures) | Button | Ctrl+OemPeriod |  |
| `CompareResults.Report` Raport | `Compare.Report` | Raport porównania | Comparison Report | Security (security-signatures) | Button |  |  |
| `CompareResults.Report` Raport | `Compare.Close` | Zamknij porównanie | Close Comparison | Security (security-signatures) | Button |  |  |

### Polecenia bez miejsca na wstążce (tylko skrót / menu kontekstowe)

| Id | PL | EN | Moduł | Skrót |
|---|---|---|---|---|
| `Edit.Copy` | Kopiuj | Copy | Viewer (renderer-viewer) | Ctrl+C |
| `Edit.SelectAll` | Zaznacz wszystko | Select All | Viewer (renderer-viewer) | Ctrl+A |
| `Shell.CommandSearch` | Wyszukaj polecenie | Search Commands | Shell (ui-shell) | Alt+Q |
| `Shell.NextDocument` | Następny dokument | Next Document | Shell (ui-shell) | Ctrl+Tab |
| `Shell.PreviousDocument` | Poprzedni dokument | Previous Document | Shell (ui-shell) | Ctrl+Shift+Tab |

### Liczba poleceń według modułu

| Moduł | Agent | Liczba |
|---|---|---|
| Shell | ui-shell | 25 |
| Viewer | renderer-viewer | 30 |
| Pages | pages-engine | 40 |
| Annotations | annotations-forms | 48 |
| Editing | edit-convert-ocr | 24 |
| Security | security-signatures | 13 |

Razem: 180 poleceń, 11 kart, 41 grup.
