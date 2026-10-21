# ZetPDF – raport testów (tester)

- **Data:** 2026-10-17
- **Gałąź / punkt wyjścia:** `main`, commit 4aab648 (Integracja 10) + commity testera.
- **Polecenie:** `dotnet test ZetPDF.sln -c Release` (Windows 11, .NET SDK 8.0.423).
- **Zasada:** testy integracyjne testera są czarną skrzynką – usługi z kontenera DI zbudowanego jak w aplikacji
  (`AddZetPdfCore` + `ModuleCatalog.CreateAll()`, prawdziwe implementacje), wynik sprawdzany **w pliku** niezależnymi
  czytnikami (PdfPig, PDFium przez `PdfiumRenderer`, PDFsharp – słowniki, zdekodowane strumienie). Kod produkcyjny
  nie był zmieniany. Testy czerwone z powodu błędów w kodzie zostały czerwone.

## 0. Wynik końcowy (recenzent końcowy, 2026-10-21)

`dotnet build ZetPDF.sln -c Release --no-incremental`: **0 błędów, 0 ostrzeżeń**. `dotnet test ZetPDF.sln -c Release --no-build` uruchomiony **3 razy z rzędu** – za każdym razem identyczny wynik:

| Projekt | Łącznie | Zaliczone | Niezaliczone | Pominięte |
|---|---:|---:|---:|---:|
| ZetPDF.Core.Tests | 34 | 34 | 0 | 0 |
| ZetPDF.Viewer.Tests | 54 | 54 | 0 | 0 |
| ZetPDF.Pages.Tests | 33 | 33 | 0 | 0 |
| ZetPDF.Annotations.Tests | 50 | 50 | 0 | 0 |
| ZetPDF.Editing.Tests | 45 | 45 | 0 | 0 |
| ZetPDF.Security.Tests | 31 | 31 | 0 | 0 |
| ZetPDF.App.Tests | 82 | 82 | 0 | 0 |
| ZetPDF.IntegrationTests | 266 | 266 | 0 | 0 |
| **Razem** | **595** | **595** | **0** | **0** |

Zmiany recenzenta w testach (szczegóły: `docs\RAPORT_KONTROLI.md`):
- B-1 i B-2 (czerwone w raporcie poniżej) naprawione przed kontrolą (commit 4b4b8e3) – testy zielone.
- Testy schowka (`Copy_selected_text_with_polish_characters_to_clipboard`, `Snapshot_tool_copies_selected_area_to_clipboard_at_high_resolution`) przeniesione z `EdgeCaseTests` do nowej klasy `ClipboardTests` w `SequentialCollection` (przegląd wszystkich poleceń wstążki w `RibbonCommandTests` wykonuje m.in. „Ze schowka” równolegle). Asercje bez zmian. Przyczyna wcześniejszej „niestabilności”: przy **zablokowanej sesji Windows** `OpenClipboard` zwraca `CLIPBRD_E_CANT_OPEN` przez cały czas (sprawdzone: obecny proces `LogonUI`, także `Clipboard.Clear()` z PowerShella zawodzi). Atrybut `[ClipboardFact]` w takiej sytuacji jawnie POMIJA test z podaniem przyczyny (zamiast fałszywego zaliczenia); przy dostępnym schowku test działa normalnie i ponawia operacje przy chwilowej blokadzie schowka. W 3 przebiegach końcowych (sesja odblokowana) oba testy wykonały się i przeszły; w przebiegu przy zablokowanej sesji były 2 pominięte.
- Nowe: `OwnerOnlyProtectionTests` (7 – D-23 i D-22), `LocalizedErrorTests` (7 – O-3).

## 1. Wyniki (raport testera z 2026-10-17)

| Projekt | Łącznie | Zaliczone | Niezaliczone | Pominięte |
|---|---:|---:|---:|---:|
| ZetPDF.Core.Tests | 34 | 34 | 0 | 0 |
| ZetPDF.Viewer.Tests | 54 | 54 | 0 | 0 |
| ZetPDF.Pages.Tests | 33 | 33 | 0 | 0 |
| ZetPDF.Annotations.Tests | 50 | 50 | 0 | 0 |
| ZetPDF.Editing.Tests | 45 | 45 | 0 | 0 |
| ZetPDF.Security.Tests | 31 | 31 | 0 | 0 |
| ZetPDF.App.Tests | 82 | 82 | 0 | 0 |
| **ZetPDF.IntegrationTests** | **252** | **250** | **2** | 0 |
| **Razem** | **581** | **579** | **2** | **0** |

Przed pracą testera: 370 testów (IntegrationTests: 41). Dodano 211 testów integracyjnych. Pełny przebieg IntegrationTests
trzy razy z rzędu: wynik identyczny (2 czerwone, te same), ok. 45 s. Testy mierzące liczniki procesu (uchwyty, pamięć,
czas, blokada wątku UI) są w kolekcji xUnit bez zrównoleglenia (`SequentialCollection`), żeby inne klasy nie zakłócały
pomiaru.

### Czerwone testy

| Test | Moduł |
|---|---|
| `FeatureCoverageTests.Link_uri_with_polish_characters_is_stored_as_valid_ascii_uri` | edit-convert-ocr |
| `PrivacyAndProtectionTests.Status_of_self_signed_signature_reports_modification_after_signing` | security-signatures |

## 2. Błędy

### B-1 (edit-convert-ocr) Łącze URI z polskimi znakami zapisywane jako UTF-16 – adres w pliku jest zepsuty
- **Test:** `ZetPDF.IntegrationTests.FeatureCoverageTests.Link_uri_with_polish_characters_is_stored_as_valid_ascii_uri`.
- **Kroki:** `ILinkService.AddLinkAsync(pdf, 0, rect, new LinkTarget(Uri: "https://example.com/zażółć"))`, potem otwarcie
  wyniku w PDFium i `IRenderDocument.GetLinksAsync(0)`. To samo dla `CreateLinksFromUrlsAsync` na stronie z tekstem
  `https://example.com/zażółć` (Edytuj > Utwórz łącza z adresów URL).
- **Oczekiwane:** `/URI` to ciąg ASCII (ISO 32000-1, 12.6.4.7); znaki spoza ASCII zakodowane procentowo z UTF-8:
  `https://example.com/za%C5%BC%C3%B3%C5%82%C4%87`.
- **Faktyczne:** `/URI` zapisany jako ciąg UTF-16BE z BOM; PDFium zwraca `þÿ\0h\0t\0t\0p\0s…`. Łącze nie działa
  w innych czytnikach, a w samym ZetPDF klik w trybie Ręka go nie otworzy (adres nie zaczyna się od http/https/mailto).
  Adresy czysto ASCII działają poprawnie (test `Edit.LinksFromUrls` w `UndoRedoAndPolishPathTests` – zielony).

### B-2 (security-signatures) Status podpisu z identyfikatora własnego ukrywa „zmieniono po podpisaniu” i „certyfikat nieważny”
- **Test:** `ZetPDF.IntegrationTests.PrivacyAndProtectionTests.Status_of_self_signed_signature_reports_modification_after_signing`.
- **Kroki:** `ISignatureService.CreateSelfSignedIdAsync(...)` (Ochrona > Identyfikatory cyfrowe), podpisanie dokumentu
  dwa razy (druga rewizja po pierwszym podpisie), `ValidateAsync`.
- **Oczekiwane:** pierwszy podpis: `IntegrityOk = true`, `CoversWholeDocument = false`, **`Status = ValidButModifiedAfter`**.
- **Faktyczne:** `Status = UnknownSigner`. W `SignatureValidator.cs` (ok. wiersza 152) kolejność warunków to
  `!integrity → Invalid`, potem `!trusted → UnknownSigner`, a dopiero dalej `!validAtTime → Invalid`
  i `!covers → ValidButModifiedAfter`. Dla niezaufanego certyfikatu (a każdy identyfikator utworzony w ZetPDF jest
  niezaufany) panel Podpisy pokazuje tylko „nieznany podpisujący”. Zmiana po podpisaniu i certyfikat nieważny w chwili
  podpisu są widoczne tylko w rozwijanych szczegółach. Dopisanie bajtów po `%%EOF` daje ten sam status.

### Obserwacje (bez czerwonych testów – do decyzji koordynatora)
- **O-1 (security-signatures + core, dokumentacja):** dokument RC4 otwarty hasłem i zapisany wychodzi jako **AES-256**.
  `SecurityService.CreateResaveSettings` i `PdfSecurityHelper.CreateResaveSettings` robią to celowo
  (`method = Aes128/Aes256 ? info.Method : Aes256`), ale nie opisują tego ani `DECYZJE.md`, ani `OGRANICZENIA.md`, a użytkownik
  nie dostaje informacji. Test `FileSafetyAndEncryptionVariantsTests.Encryption_method_after_resave_with_user_password`
  utrwala obecne zachowanie: AES-256/AES-128 zostają, RC4 → AES-256.
- **O-2 (core/security-signatures, skutek D-08):** plik z samym hasłem uprawnień (puste hasło otwarcia), otwarty bez
  hasła, np. z dodanym komentarzem i zapisany, dostaje **nowe losowe hasło właściciela**. Autor nie może już zdjąć ograniczeń
  swoim hasłem. D-08 opisuje to tylko dla przypadku „tylko hasło użytkownika”. Dla pustego hasła otwarcia skutek
  jest groźniejszy, bo plik może zmienić każdy odbiorca. Sprawdzone w
  `FileSafetyAndEncryptionVariantsTests.Owner_only_encryption_opens_without_prompt_with_restricted_rights_and_keeps_owner_password`
  (test potwierdza hasło właściciela tylko po otwarciu hasłem właściciela).
- **O-3 (annotations-forms, pages-engine, editing, security – lokalizacja komunikatów):** niektóre wyjątki
  z angielskim tekstem trafiają do użytkownika przez `IDialogService.ShowError` → `Core.Error.Generic` z `exception.Message`,
  np. `Not an FDF file.`, `Empty XFDF.` (import złego pliku), `Object not found…`, `No pages.` W polskim interfejsie
  okno błędu ma wtedy wstawkę po angielsku. Niski priorytet.

## 3. Nowe testy testera (tests\ZetPDF.IntegrationTests, tests\ZetPDF.TestData)

| Plik / klasa | Testy | Co sprawdza (wynik w pliku) |
|---|---:|---|
| `Infrastructure\Host.cs`, `Infrastructure\Inspect.cs` | – | host DI jak w aplikacji, atrapy `IDialogService`/`IShellService`, STA z Dispatcherem; PdfPig, PDFium, „Deep” (słowniki + zdekodowane strumienie + ciągi UTF-16 + /Info), render (kolor, jasność) |
| `Operations.cs` | – | katalog 32 operacji wszystkich modułów (bajty → bajty) z próbką i weryfikacją w pliku |
| `UndoRedoAndPolishPathTests` | 34 | dla każdej z 32 operacji: plik w `Próbny folder\…\zażółć gęślą jaźń.pdf` → sesja → wynik w pliku → Cofnij = identyczne bajty → Ponów = identyczne bajty → Zapisz jako do `Wynik ąę ść\zapisany … źdźbło.pdf` (oryginał nietknięty, brak .tmp/.bak) → ponowne otwarcie; łańcuch 7 operacji różnych modułów z cofaniem każdego stanu; nadpisanie pliku |
| `EncryptedInputTests` | 67 | każda operacja na pliku AES-256 otwartym hasłem właściciela i hasłem użytkownika: wynik w pliku, zapis nadal AES-256 (`/AESV3`), PDFium/PdfPig otwierają tym hasłem, bez hasła – odmowa; ograniczone uprawnienia blokują modyfikację (DocumentPermissionException), komentarz dozwolony, ograniczenia zachowane po zapisie; scalanie/wstawianie z zaszyfrowanego źródła; porównanie i weryfikacja na zaszyfrowanych |
| `CorruptedInputTests` | 18 | 7 rodzajów uszkodzeń: otwarcie → `PdfCorruptException` albo działająca sesja (operacje modułów bez wyjątków programistycznych); `ProbeAsync` nie rzuca; 17 usług z uszkodzonymi bajtami – bez NullReference/IndexOutOfRange i bez zawieszenia; śmieciowy XFDF/FDF/CSV; Kreator z mieszanką plików (raport per plik, oryginały nietknięte); scalanie/łączenie/obrazy z uszkodzonym źródłem |
| `PrivacyAndProtectionTests` | 12 | redakcja: fraza znika z PdfPig, PDFium, **zdekodowanych strumieni** (próbka z tekstem dosłownym `(…) Tj`), tytułu, zakładek; obszar czarny; reszta tekstu wyszukiwalna; redakcja obszaru; wszystkie wzorce (PESEL, NIP, e-mail, telefon, karta, IBAN) + walidacja sum; Cofnij redakcji. AES-256 zapisany przez DocumentManager: `/V 5 /R 6 /AESV3`, PDFium (brak/złe/użytkownik/właściciel), PdfPig z hasłem, usuwanie zabezpieczeń i Cofnij; ponowny zapis hasłem użytkownika. Oczyszczanie: /Info, XMP, JavaScript (OpenAction, Names, /AA strony, łącze), EmbeddedFiles, FileAttachment, komentarze – brak w całym pliku; opcje respektowane. Podpis: identyfikator własny → podpis → ważny; zmiana bajtu → Invalid; dopisanie → nie obejmuje całości; edycja w ZetPDF unieważnia; podpis AES-256 z sesji; widoczny podpis w renderze |
| `CommentsFormsOcrTests` | 7 | XFDF 12 rodzajów komentarzy + odpowiedź + stan: zapis do `komentarze zażółć.xfdf`, poprawny XML z polskimi znakami, import do czystego dokumentu (rodzaj, strona, treść, autor, IRT, stan, prostokąt, kolor), podtypy w /Annots, widoczne w renderze, ponowny import bez duplikatów; scalanie z komentarzami innego producenta; dane formularza XFDF/FDF/CSV w obie strony (polskie wartości, średnik i cudzysłów w CSV), PdfPig widzi wartość; OCR skanu (słowa „Faktura”, „12345”, „gęślą”, „16.10.2026” w PdfPig i PDFium, warstwa niewidoczna, wyszukiwanie), pomijanie stron z tekstem |
| `RibbonCommandTests` | 11 | prawdziwe polecenia wstążki po `Initialize` modułów: Obróć w prawo, Duplikuj, Usuń znaki wodne, Usuń komentarze, Resetuj formularz, Utwórz/Usuń łącza, Zakładki z nagłówków, Szybkie OCR, Zastosuj redakcje (z ostrzeżeniem), Usuń zabezpieczenia – wynik w pliku, Cofnij/Ponów = identyczne bajty; polecenia wyszarzone bez uprawnień; **przegląd wszystkich zarejestrowanych poleceń** (okna = Anuluj): bez błędu, bez zawieszenia, bez okien z pominięciem IDialogService |
| `LocalizationThemeShortcutTests` | 9 | wszystkie `Strings.resx` z dysku: bliźniak pl, te same klucze, brak pustych, zgodne `{n}`, brak duplikatów między modułami; brak nieprzetłumaczonych tekstów pl; klucze użyte w C#/XAML istnieją; po Initialize wszystkie klucze rozwiązują się w pl i en (także polecenia `RegisterExtra` i menu dynamiczne); XAML bez tekstów na sztywno (Content/Text/Header/ToolTip/Title/…, treść TextBlock) i bez kolorów na sztywno poza Light/Dark; C# bez kolorów na sztywno (lista przejrzanych wyjątków – kolory treści PDF, papier) i bez literałów w Content/Text/…/ShowMessage/Confirm/SetStatus; skróty wszystkich zarejestrowanych poleceń bez konfliktów, bez martwych skrótów, standardowe (Ctrl+O/S/P/F/Z/Y/W/G/C/A/B, F11) |
| `PrintAndPerformanceTests` | 8 | druk do XPS tą samą ścieżką co do drukarki: zakres „1-3,5” = dokładnie te strony w kolejności (kolor arkuszy), N-up 4 i 2 (z zakresem), skala szarości; odmowa bez uprawnienia druku; 500 stron: otwarcie, render pierwszej strony, obrót, zapis, Cofnij – czas i maksymalna przerwa wątku UI < 1 s; widok 500 stron, przejście na ostatnią, wyszukiwanie bez blokady; 60 cykli otwórz/edytuj/zamknij (także AES-256) – uchwyty procesu, pamięć zarządzana, plik niezablokowany; 25 cykli widoku |
| `FeatureCoverageTests` | 11 | wiele kart, ostatnie pliki, Przywróć, nowy dokument → Zapisz jako; autor komentarza z Ustawień (narzędzie Prostokąt) i domyślny widok; język na żywo; obiekty: przesuń/skaluj/obróć/zastąp/usuń obraz (PdfPig); łącze z polskimi znakami (**B-1**); obrazy PNG/JPG/TIFF/BMP z polskich nazw → PDF; PDF → PNG/TIFF wielostronicowy/TXT/DOCX do polskich ścieżek (OpenXml); łączenie PDF+PNG+TXT, PDF z tekstu i obrazu schowka; wszystkie 8 rodzajów pól formularza, wypełnienie, spłaszczenie, kolejność tabulacji (/Annots); raport porównania HTML/TXT; własne działanie Kreatora (OCR + znak wodny + metadane + szyfrowanie) |
| `EdgeCaseTests` | 6 | redakcja usuwa komentarz i pole w obszarze oraz frazę z XMP; podział wg zakładek / co N stron i wyodrębnianie do polskich nazw; scalanie z zakładką na plik; flaga „zmieniony” względem wersji zapisanej przez Cofnij/Ponów; migawka do schowka (300 DPI); kopiowanie tekstu z polskimi znakami |
| `ToolsAndViewTests` | 5 | Wypełnij i podpisz ✓/✗ narzędziem (plik, render, Cofnij); polecenia widoku nie zmieniają pliku; stempel z obrazu (render, spłaszczenie); optymalizator (rozmiar, DPI obrazu, wygląd); nieosiągalny TSA – szybki czytelny błąd, bez TSA brak znacznika |
| `RealWorldStructureTests` | 9 | ręcznie zbudowane PDF 1.5 ze strumieniami obiektów i xref jako strumień oraz plik z aktualizacją przyrostową: operacje modułów + Cofnij, redakcja, metadane ostatniej rewizji, podpis przyrostowy, szyfrowanie, scalanie |
| `FileSafetyAndEncryptionVariantsTests` | 7 | zapis do pliku zablokowanego i tylko do odczytu – czytelny wyjątek, oryginał i folder bez zmian (bez .tmp/.bak), później zapis się udaje; AES-256/AES-128/RC4 po ponownym zapisie (**O-1**); plik z samym hasłem uprawnień (**O-2**); znak wodny: zapamiętane opcje, zakres stron, usunięcie przywraca tekst |
| `OrganizePagesTests` | 7 | przenoszenie (semantyka „upuść przed”), zamiana, przeplatanie z odwróceniem, wstawianie podzbioru i zastępowanie z polskiej ścieżki, zmiana rozmiaru A5, obrót łączny, kadrowanie białych marginesów, etykiety stron |
| `ExtraSamples` (TestData) | – | tekst dosłowny w strumieniu (Helvetica/WinAnsi), „wszystko ukryte” (Info, XMP, JS w 4 miejscach, 2 załączniki, komentarz), kolorowe strony (druk), adresy URL, PNG, 7 rodzajów uszkodzeń, PDF 1.5 z /ObjStm + /XRef, aktualizacja przyrostowa |

## 4. Macierz pokrycia funkcji etapów 1 i 2

Legenda kolumny „Wynik w pliku”: **Tak** – test sprawdza wynik w pliku/renderze niezależnym czytnikiem; **Częśc.** –
tylko część podpunktów lub tylko przez usługę modułu; **Nie** – test sprawdza tylko brak wyjątku lub konstrukcję obiektu.
Skróty projektów: Core = ZetPDF.Core.Tests, View = ZetPDF.Viewer.Tests, Pag = ZetPDF.Pages.Tests, Ann = ZetPDF.Annotations.Tests,
Edi = ZetPDF.Editing.Tests, Sec = ZetPDF.Security.Tests, App = ZetPDF.App.Tests, IT = ZetPDF.IntegrationTests (testy testera, jeśli nie zaznaczono inaczej).

### Etap 1

| # | Funkcja / podpunkt | Testy autorów modułów | Wynik w pliku | Testy testera (IT) | Wynik w pliku |
|---|---|---|---|---|---|
| 1 | Otwieranie, zapis, zapis jako | Core `DocumentManagerTests.Open_modify_save_with_polish_path`, `CreateNew_is_modified_and_needs_path` | Tak | `UndoRedoAndPolishPathTests.*` (32 operacje, Zapisz jako, ponowne otwarcie), `Save_over_existing_file…`, `FileSafetyAndEncryptionVariantsTests.Save_to_locked_file…`, `Save_to_read_only_file…` | Tak |
| 1 | Przeciągnij i upuść, linia poleceń | App `ShellSmokeTests.Command_line_parses_files_and_password` | Nie (parsowanie) | – (wymaga okna głównego) | – |
| 1 | PDF chroniony hasłem | Core `Encrypted_document_opened_with_user_password…` (atrapa renderera), `…owner_password_has_full_rights`; View `Encrypted_file_requires_password_and_rejects_wrong_one`, `ViewerTests.Encrypted_file_with_user_password_opens…` | Tak | `EncryptedInputTests.*` (67), `PrivacyAndProtectionTests.Aes256_saved_by_document_manager…` | Tak |
| 1 | Ostatnie pliki, wiele kart, Przywróć | Core `Open_modify_save_with_polish_path` (ostatnie) | Częśc. | `FeatureCoverageTests.Several_documents_open_in_tabs_recent_files_and_revert` | Tak |
| 1 | Uszkodzone pliki | Core `Corrupted_and_non_pdf_files_raise_domain_exception`; View `Corrupted_and_non_pdf_raise_domain_exceptions` | Tak | `CorruptedInputTests.*` (18) | Tak |
| 2 | Powiększanie, dopasuj stronę/szerokość | View `LogicTests.Continuous_layout…fit_width…`, `Fit_page_shows_whole_page…`, `Zoom_steps…` | Nie (logika układu) | `ToolsAndViewTests.View_commands_change_only_the_view_not_the_file` | Częśc. (stan widoku, plik bez zmian) |
| 2 | Układy (jedna, ciągły, dwie), obrót widoku | View `Single_and_two_page_modes…`, `View_rotation_swaps_bitmap_dimensions`, `ViewerTests.Layout_modes_and_navigation_commands_work`, `Page_transform_round_trips…` | Częśc. (render obrotu) | `ToolsAndViewTests.View_commands…` | Częśc. |
| 2 | Pełny ekran, tryb odczytu, przejdź do strony | View `Layout_modes_and_navigation_commands_work` | Nie | `ToolsAndViewTests.View_commands…` (IShellService, Nav.First/Last) | Częśc. |
| 3 | Wyszukiwanie (Ctrl+F) z podświetlaniem | View `Find_highlights_polish_hits_and_moves_between_them`, `Search_can_be_cancelled` | Tak (PDFium) | `PrintAndPerformanceTests.Large_500_page_document_view…` (1000 trafień), `CommentsFormsOcrTests.Ocr_on_scan…` (szukanie w warstwie OCR) | Tak |
| 3 | Zaznaczanie i kopiowanie tekstu | View `Text_selection_across_pages…`, `Select_all_and_copy_respect_text_and_permissions` | Tak | `EdgeCaseTests.Copy_selected_text_with_polish_characters_to_clipboard` | Tak (schowek) |
| 3 | Migawka obszaru do schowka | – | – | `EdgeCaseTests.Snapshot_tool_copies_selected_area_to_clipboard_at_high_resolution` | Tak (wymiary 300 DPI, treść) |
| 4 | Miniatury, zmiana kolejności przeciąganiem | View `Thumbnails_panel_lists_pages_renders_visible_and_moves_pages_with_undo` | Tak | `OrganizePagesTests.Move_pages_follows_drop_before_target_semantics` (silnik `MoveAsync`) | Tak |
| 4 | Panel zakładek (dodaj, edytuj, usuń, zagnieżdżaj) | View `Bookmark_tree_insert_rename_remove`, `Bookmark_tree_move…`, `Bookmark_service_round_trips_nested_tree…`, `Bookmarks_panel_adds_renames_and_moves_with_undo`, `Bookmarks_are_read_with_nesting…` | Tak | `Operations: Viewer.Bookmarks` (×4 testy), `RealWorldStructureTests` | Tak |
| 5 | Wstaw z pliku, pusta strona | Pag `Insert_blank_and_create_blank_document`, `Insert_from_file_with_polish_path_and_page_subset`, `Insert_from_encrypted_file_with_user_password` | Tak | `Operations: Pages.InsertBlank`, `OrganizePagesTests.Swap_interleave_and_insert_subset…`, `EncryptedInputTests.Merge_and_insert…` | Tak |
| 5 | Usuń, wyodrębnij, zastąp, duplikuj, przenieś, odwróć | Pag `Delete_pages_and_fix_bookmarks`, `Delete_page_with_form_fields_removes_fields`, `Extract_to_document_and_to_separate_files`, `Replace_pages_from_file`, `Duplicate_move_swap_reverse` | Tak | `Operations: Pages.Delete/Duplicate/Reverse`, `OrganizePagesTests.*`, `EdgeCaseTests.Split_by_bookmarks_and_extract…`, `RibbonCommandTests.Pages_rotate_right_and_duplicate` | Tak |
| 5 | Obróć, kadruj, zmień rozmiar | Pag `Rotate_accumulates_and_normalizes`, `Crop_margins_and_white_margins`, `Crop_on_rotated_page…`, `Resize_scales_content_into_target_size` | Tak | `Operations: Pages.Rotate/Crop`, `OrganizePagesTests.Resize_rotate_and_crop_white_margins…` | Tak |
| 5 | Podziel (co N / wg zakładek), scal | Pag `Split_every_n_pages_by_bookmarks_and_by_size`, `Merge_with_ranges_and_bookmarks_from_file_names` | Tak | `EdgeCaseTests.Split_by_bookmarks…`, `Merge_files_from_polish_paths…`, `RealWorldStructureTests.Encrypting_and_merging…` | Tak |
| 6 | Znak wodny (tekst/obraz), tło | Pag `Text_watermark_with_polish_characters…`, `Watermark_renders_with_pdfium_and_behind_content`, `Image_watermark_and_background_color_image` | Tak | `Operations: Pages.Watermark`, `RibbonCommandTests.Remove_all_watermarks`, `FileSafetyAndEncryptionVariantsTests.Watermark_options…` | Tak |
| 6 | Nagłówek i stopka, numeracja stron, Bates | Pag `Header_footer_macros_and_page_numbers`, `Header_footer_position_on_rotated_page…`, `Bates_numbering_across_files` | Tak | `Operations: Pages.HeaderFooter/PageNumbers/Bates` | Tak |
| 7 | Wyróżnij, podkreśl, przekreśl, notatka, pole tekstowe, maszyna | Ann `Update_moves_resizes_and_recolors`, `Xfdf_export_import_roundtrip_with_dedup`, `Markup_tool_builds_line_quads…`, `Existing_annotations_from_other_producer_are_read` | Tak | `Operations: Annot.Highlight/Note`, `CommentsFormsOcrTests.Xfdf_export_to_polish_path…` (12 rodzajów) | Tak |
| 7 | Rysunki (prostokąt, owal, linia, strzałka, ołówek) | Ann `ViewerIntegrationTests.Rectangle_tool_creates_annotation_and_undo_removes_it`, `Ink_simplification_keeps_endpoints` | Tak (prostokąt) | `Operations: Annot.Ink`, `CommentsFormsOcrTests.Xfdf…` (Square, Circle, Arrow, Line, Ink + render), `FeatureCoverageTests.Author_identity…` (narzędzie Prostokąt) | Tak |
| 7 | Stemple standardowe i z obrazu | Ann `Image_stamp_and_flatten_keep_render` | Tak | `ToolsAndViewTests.Custom_image_stamp_is_stored_with_image_appearance`, `CommentsFormsOcrTests.Xfdf…` (Approved) | Tak |
| 7 | Panel komentarzy, odpowiedzi, stan | Ann `Reply_and_state_are_stored_with_irt`, `Panels_and_dialogs_can_be_created` (Nie) | Częśc. | `CommentsFormsOcrTests.Xfdf…` (IRT i stan po imporcie) | Tak (bez panelu) |
| 7 | Eksport/import XFDF (FDF) | Ann `Xfdf_export_import_roundtrip_with_dedup`, `Fdf_export_import_roundtrip` | Tak | `CommentsFormsOcrTests.Xfdf_export_to_polish_path…`, `Xfdf_import_merges…`, `CorruptedInputTests.Broken_comment_and_form_data_files…` | Tak |
| 7 | Usuń/pokaż/ukryj komentarze | Ann `DeleteAll_keeps_links_and_widgets`, `ShowAnnotationsTests.*`, `Hidden_flag_hides_annotation_in_render` | Tak | `Operations: Annot.DeleteAll`, `RibbonCommandTests.Delete_all_comments` | Tak |
| 8 | Wypełnianie AcroForm (tekst, wybór, opcje, lista) | Ann `Reads_sample_form`, `Filled_values_are_saved_with_appearance_and_visible_in_pdfium`, `Reset_restores_defaults`; IT (integrator) `FlattenWidgetStateTests.*` | Tak | `Operations: Annot.FormFill`, `FeatureCoverageTests.All_form_field_kinds…`, `RibbonCommandTests.Reset_form` | Tak |
| 8 | Wypełnij i podpisz: tekst, ✓, ✗, podpis odręczny | Ann `Drawn_and_typed_signatures_render_to_transparent_png_and_can_be_stamped`, `FlattenOnSaveTests.*` | Tak (podpis, spłaszczanie) | `ToolsAndViewTests.Fill_and_sign_check_and_cross_marks…` | Tak (✓/✗); tekst – brak testu narzędzia |
| 9 | Hasło otwierania i uprawnień (AES-256) | Sec `Password_protect_and_remove_security_through_session_and_save`, `Resave_settings_follow_decision_D08`; Core `Encryption_change_is_undoable` | Tak | `PrivacyAndProtectionTests.Aes256_saved_by_document_manager…`, `Reopened_aes256…`, `EncryptedInputTests.*`, `FileSafetyAndEncryptionVariantsTests.*` | Tak (PDFium + PdfPig) |
| 9 | Usuwanie zabezpieczeń (znane hasło) | Sec `Password_protect_and_remove_security…`; View `RemoveSecurity_with_user_password_gives_plain_pdf…` | Tak | `RibbonCommandTests.Remove_security_from_aes256_document`, `PrivacyAndProtectionTests.Aes256_saved…` | Tak |
| 10 | Drukowanie: zakres, skalowanie, N-up | View `Print_composes_n_up_sheets_to_xps` (liczba arkuszy), `Print_layout_sheets_grid_and_fit` | Częśc. | `PrintAndPerformanceTests.Print_range_1_3_5…` (które strony), `Print_n_up…`, `Print_grayscale…`, `Print_service_refuses…` | Tak (treść arkuszy XPS) |
| 11 | Ustawienia: język, motyw | Core `Settings_persist_including_module_sections`, `Localization_switches_live…`; App `Theme_resolution_follows_system`, `Theme_dictionaries_define_all_keys_and_are_opaque_for_text` | Tak | `FeatureCoverageTests.Language_switches_live…`, `LocalizationThemeShortcutTests.*` | Tak |
| 11 | Tożsamość (autor), domyślne powiększenie | – | – | `FeatureCoverageTests.Author_identity_from_settings…` | Tak (/T adnotacji) |
| 11 | Skróty klawiszowe | Core `All_shortcuts_parse_without_conflicts…`; App `Catalog_shortcuts_have_no_conflicts_and_parse`, `Shortcut_conflicts_are_detected` | Nie (katalog) | `LocalizationThemeShortcutTests.Shortcuts_of_all_registered_commands…` (po Initialize, martwe skróty, standardowe) | Nie (mapa skrótów) |
| 12 | Cofnij/Ponów dla wszystkich operacji | Core `Execute_undo_redo_restores_identical_state`, `Snapshots_over_budget_spill_to_disk…`, `Undo_limit_drops_oldest`; View/Ann (pojedyncze operacje z Cofnij) | Tak (mechanizm) | `UndoRedoAndPolishPathTests.*` (32 operacje 7 modułów, łańcuch), `RibbonCommandTests.*` (10 poleceń wstążki), `EdgeCaseTests.Modified_flag…` | Tak (identyczne bajty) |

### Etap 2

| # | Funkcja / podpunkt | Testy autorów modułów | Wynik w pliku | Testy testera (IT) | Wynik w pliku |
|---|---|---|---|---|---|
| 1 | Edytuj tekst (zastąpienie bloku, usunięcie starego ze strumienia) | Edi `Blocks_are_lines_with_font_info`, `Replace_block_removes_old_text_from_stream_and_adds_polish_text`, `Partial_glyph_removal_keeps_neighbours_in_place` | Tak | `Operations: Edit.ReplaceTextBlock` (stary tekst znika w PdfPig i PDFium) | Tak |
| 1 | Dodaj tekst (czcionka, rozmiar, kolor) | Edi `Add_text_with_wrapping_is_searchable` | Tak | `Operations: Edit.AddText` | Tak |
| 1 | Wyszukaj i zamień | Edi `Find_replace_replaces_on_all_pages`, `Find_replace_respects_whole_word_and_case` | Tak | `Operations: Edit.FindReplace` | Tak |
| 2 | Przesuwanie, skalowanie, obracanie, usuwanie obrazów | Edi `Detects_images_and_shapes_and_moves_image`, `Delete_and_replace_image` | Tak | `FeatureCoverageTests.Image_object_can_be_moved_scaled_rotated_replaced_and_deleted` | Tak (PdfPig) |
| 2 | Dodawanie obrazów i kształtów | Edi `Shapes_render_with_colors` | Tak | `Operations: Edit.AddImage/AddShape` | Tak |
| 3 | Łącza (URL, strona) | Edi `Add_update_delete_links` | Tak | `Operations: Edit.AddLink`, `FeatureCoverageTests.Link_uri_with_polish_characters…` (**czerwony – B-1**) | Tak |
| 3 | Automatyczne wykrywanie URL | Edi `Creates_links_from_urls_and_emails` | Tak | `Operations: Edit.LinksFromUrls`, `RibbonCommandTests.Create_and_remove_links_from_urls` | Tak |
| 3 | Zakładki z nagłówków | Edi `Bookmarks_from_headings_build_tree` | Częśc. (drzewo) | `Operations: Edit.BookmarksFromHeadings` (zapis + PDFium), `RibbonCommandTests.Bookmarks_from_headings` | Tak |
| 4 | Obrazy (JPG/PNG/TIFF/BMP) → PDF | Edi `Images_to_pdf_supports_formats_multipage_tiff_and_page_sizes` | Tak | `FeatureCoverageTests.Images_in_polish_folder_convert…` | Tak |
| 4 | PDF → obrazy, TXT, DOCX | Edi `Pdf_to_images_in_all_formats`, `Pdf_to_text_plain_and_layout`, `Pdf_to_docx_has_paragraphs_formatting_images_and_page_breaks` | Tak | `FeatureCoverageTests.Images_in_polish_folder_convert…` (polskie ścieżki, TIFF wielostronicowy, OpenXml) | Tak |
| 4 | Scalanie plików różnych typów, PDF ze schowka | Edi `Combine_pdf_images_and_text`, `Text_to_pdf_wraps_and_paginates`, `Image_data_to_pdf` | Tak | `FeatureCoverageTests.Combine_files_of_different_types…` | Tak |
| 5 | OCR pliku i obszaru, pol/eng, ukryta warstwa | Edi `Scan_gets_hidden_text_layer_searchable…`, `Region_ocr_recognizes_only_the_area`, `Repeated_ocr_replaces_previous_layer…`, `Rotated_scan_layer_matches_display_orientation`, `OcrBootstrapTests.*` | Tak | `CommentsFormsOcrTests.Ocr_on_scan…`, `Ocr_skips_text_pages…`, `Operations: Ocr.Document`, `RibbonCommandTests.Quick_ocr_on_scan`, Kreator z krokiem OCR | Tak |
| 5 | Podgląd i poprawianie niepewnych wyników | Edi `Low_confidence_words_are_suspect_with_threshold` | Częśc. (próg) | – (okno przeglądu – UI) | – |
| 6 | Redakcja obszaru/tekstu, trwałe usunięcie | Edi `Apply_removes_pesel_and_email_from_pdfpig_and_pdfium_text`, `Apply_removes_phrase_in_text_and_metadata`, `Area_redaction_covers_pixels_black…`, `Apply_on_rotated_page…` | Tak | `PrivacyAndProtectionTests.Redacted_phrase_disappears_from_pdfpig_pdfium_raw_streams…`, `Area_redaction…`, `EdgeCaseTests.Redaction_removes_comment_and_form_field…`, `RealWorldStructureTests.Redaction…`, `RibbonCommandTests.Apply_redactions` | Tak (także zdekodowane strumienie) |
| 6 | Wzorce PESEL, NIP, e-mail, telefon, karta | Edi `Sample_values_are_valid`, `Finds_all_patterns_in_sample` | Tak | `PrivacyAndProtectionTests.Search_and_redact_all_sensitive_patterns…` | Tak |
| 6 | Ostrzeżenie o nieodwracalności | – | – | `RibbonCommandTests.Apply_redactions` (potwierdzenie przed zastosowaniem) | Częśc. |
| 7 | Oczyszczanie: metadane, komentarze, załączniki, skrypty, ukryte warstwy | Pag `Sanitize_removes_metadata_attachments_javascript_and_comments`, `Sanitize_removes_hidden_layer_content_and_clears_form_data` | Tak | `PrivacyAndProtectionTests.Sanitize_removes_info_xmp_javascript_attachments…`, `Sanitize_respects_options…`, `Operations: Pages.Sanitize` | Tak (cały plik po dekompresji) |
| 8 | Optymalizator (kompresja obrazów, nieużywane obiekty) | Pag `Optimize_reduces_size_by_recompressing_and_downsampling_images`, `Optimize_removes_metadata_and_thumbnails_when_requested` | Tak | `ToolsAndViewTests.Optimizer_reduces_scan_size_and_keeps_appearance`, `Operations: Pages.Optimize`; IT (integrator) `ActionWizardSmokeTests.Optimize_for_web…` | Tak |
| 8 | Skala szarości | Pag `Grayscale_converts_content_colors_and_images`, `Grayscale_by_rasterization_produces_image_pages` | Tak | `Operations: Pages.Grayscale` (render bez koloru) | Tak |
| 9 | Podpis PFX / magazyn Windows, widoczne pole | Sec `Sign_then_validate_ok_and_one_byte_change_is_detected`, `Signing_is_incremental…`, `Sign_existing_empty_signature_field…`, `Signature_with_image_on_rotated_page…`, `Created_digital_id_can_sign`, `Sign_aes256_document_keeps_encryption` | Tak | `PrivacyAndProtectionTests.Sign_saved_document_validate_and_detect_modifications`, `Sign_aes256_document_from_session…`, `Visible_signature_is_drawn_on_page`, `RealWorldStructureTests.Signing_incrementally…` | Tak |
| 9 | Weryfikacja podpisów | Sec `Validates_signature_created_by_pdfsharp`, `Unsigned_document_has_no_results` | Tak | `PrivacyAndProtectionTests.Status_of_self_signed_signature_reports_modification_after_signing` (**czerwony – B-2**), `EncryptedInputTests.Signature_validation_and_compare…` | Tak |
| 9 | Znacznik czasu TSA | Sec `Timestamp_from_fake_tsa_is_embedded…`, `No_tsa_address_means_no_network_call` (atrapa TSA) | Tak | `ToolsAndViewTests.Unreachable_tsa_fails_fast…` | Częśc. (prawdziwy TSA nie testowany – brak sieci) |
| 10 | Tworzenie pól (tekst, wybór, opcje, lista, przycisk, podpis, data) | Ann `Created_fields_are_visible_in_pdfpig_and_render`, `Update_delete_and_tab_order` | Tak | `FeatureCoverageTests.All_form_field_kinds_can_be_created_filled_and_reordered`, `Operations: Annot.CreateField` | Tak |
| 10 | Kolejność tabulacji | Ann `Update_delete_and_tab_order` | Tak | `FeatureCoverageTests.All_form_field_kinds…` (/Annots) | Tak |
| 10 | Import/eksport danych FDF/XFDF/CSV | Ann `Csv_parser_handles_quotes_and_semicolons` | Częśc. (parser) | `CommentsFormsOcrTests.Form_data_export_and_import_roundtrip_with_polish_values` (×3 formaty) | Tak |
| 11 | Porównanie dwóch PDF (zmiany tekstu, raport) | Sec `Detects_changed_word_inserted_and_deleted_text`, `Identical_documents…`, `Graphic_difference_is_found…`, `Report_text_contains_kinds_in_polish` | Tak | `FeatureCoverageTests.Compare_report_is_written_to_polish_path…` (HTML, TXT), `EncryptedInputTests.Signature_validation_and_compare…` | Tak |
| 11 | Widok obok siebie | Sec `ModuleSmokeTests.Dialogs_and_editors_can_be_built` | Nie | – (okno – UI) | – |
| 12 | Kreator: zapisane sekwencje, wsadowo | Sec `Prepare_for_distribution_runs_on_many_files…`, `Output_folder_with_suffix_errors_are_reported_per_file`, `Missing_module_gives_readable_error…`, `Save_load_delete_action_json_without_passwords` | Tak | `FeatureCoverageTests.Custom_action_with_watermark_metadata_ocr_and_encryption…`, `CorruptedInputTests.Action_wizard_reports_broken_files…` | Tak |
| 12 | Gotowe: dystrybucja, Internet, archiwizacja | IT (integrator) `ActionWizardSmokeTests.*` | Tak | – (pokryte przez integratora na prawdziwych usługach) | Tak |

### Przekrojowo (wymagania ogólne)

| Wymaganie | Testy | Wynik |
|---|---|---|
| Polskie znaki i ścieżki ze spacjami w każdym zapisie/eksporcie | `UndoRedoAndPolishPathTests` (32 operacje), `EncryptedInputTests` (64), `FeatureCoverageTests` (konwersje, DOCX, TIFF, raport porównania, Kreator), `CommentsFormsOcrTests` (XFDF, FDF, CSV), `EdgeCaseTests` (podział, wyodrębnianie, scalanie) | zielone |
| Plik zaszyfrowany jako wejście operacji każdego modułu | `EncryptedInputTests` (32 operacje × hasło właściciela/użytkownika) | zielone |
| Uszkodzony plik – brak nieobsłużonego wyjątku | `CorruptedInputTests` (18) | zielone |
| 500 stron – czas i brak blokady | `PrintAndPerformanceTests.Large_500_page_*` (przerwa wątku UI < 1 s przy otwieraniu, renderze, edycji, zapisie, Cofnij, wyszukiwaniu) | zielone |
| Uchwyty i pamięć | `PrintAndPerformanceTests.Many_*_cycles_*`; View `Handles_are_released_after_many_open_close_cycles`, `Closing_document_disposes_view_and_releases_pdfium_handles` | zielone |
| Lokalizacja pl/en, brak literałów, brak kolorów na sztywno | `LocalizationThemeShortcutTests` (9), IT (architekt) `SkeletonIntegrationTests.All_resource_files…`, App `No_hard_coded_colors_outside_theme_dictionaries` (tylko App) | zielone |
| Skróty bez konfliktów | `LocalizationThemeShortcutTests.Shortcuts_of_all_registered_commands…` | zielone |
| Struktury plików z innych programów (ObjStm, xref-stream, aktualizacje przyrostowe) | `RealWorldStructureTests` (9) | zielone |
| Bezpieczny zapis (plik tymczasowy, brak pozostałości przy błędzie) | `FileSafetyAndEncryptionVariantsTests.Save_to_locked_file…`, `Save_to_read_only_file…`, `UndoRedoAndPolishPathTests` (brak .tmp/.bak) | zielone |

### Słabe testy autorów (sprawdzają tylko konstrukcję lub brak wyjątku)
- Pag `DialogSmokeTests.All_dialogs_build_and_have_localized_texts`, `ModuleSmokeTests.*`; Ann `ModuleSmokeTests`, `ModuleUiTests.Panels_and_dialogs_can_be_created`;
  Edi `ModuleSmokeTests`; Sec `ModuleSmokeTests.*`; View `ModuleSmokeTests.*`, `RendererTests.Form_fields_render_without_errors`, `Search_can_be_cancelled`;
  Edi `OcrTests.Recognition_can_be_cancelled`. To testy rejestracji/konstrukcji. Działanie funkcji pokrywają testy z tabel powyżej.
- View `Print_composes_n_up_sheets_to_xps` liczy tylko arkusze. Treść arkuszy (które strony, szarość) sprawdzają testy `PrintAndPerformanceTests.Print_*`.
- Core `Encrypted_document_opened_with_user_password…` używa atrapy renderera. Prawdziwy PDFium sprawdza `EncryptedInputTests`.

## 5. Czego nie sprawdzono
- Okien dialogowych modułów z parametrami (znak wodny, druk, podpis, OCR, Kreator…) nie wypełniano w interfejsie. W testach
  poleceń wstążki atrapa `IDialogService.ShowDialog` zwraca Anuluj, a silniki tych okien sprawdzają usługi.
  Okna głównego (MainWindow, wstążka Fluent, przeciągnij i upuść, pełny ekran w oknie) nie uruchamiano.
- Druk na fizycznej drukarce. Sprawdzono ścieżkę XPS, wspólną z drukiem do kolejki.
- Prawdziwy serwer TSA (brak sieci) – tylko atrapa (testy autora) i nieosiągalny adres lokalny.
- Kontrast i ucięcia tekstów w obu językach (wizualnie) – poza zakresem testów automatycznych testera.
- Edycja tekstu w czcionkach nieobsługiwanych (Type3, standardowe 14 bez /Widths) – opisane w `docs\ograniczenia\edit-convert-ocr.md`, nie zgłaszane jako błąd.
