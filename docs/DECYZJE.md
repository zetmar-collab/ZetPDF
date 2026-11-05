# Decyzje projektowe ZetPDF

Format: problem → decyzja → uzasadnienie. Decyzje oznaczone **[DO ZATWIERDZENIA]** wymagają akceptacji koordynatora.

## D-01. PDFsharp 6.2.4 zamiast 6.5.0 **[DO ZATWIERDZENIA]**
- Problem: koordynator wskazał PDFsharp 6.5.0. Sprawdzenie `.nuspec`: 6.5.0 (wydany 14.10.2026) ma licencję **AGPL-3.0-only**; wersje 6.0–6.2.4 – MIT (6.3 i 6.4 nie istnieją na nuget.org).
- Decyzja: PDFsharp **6.2.4 (MIT)**, przypięty właściwością `$(PdfSharpVersion)` w `Directory.Build.props`, referencja tylko w Core. Zakaz aktualizacji do 6.5+.
- Uzasadnienie: zasady zespołu zabraniają AGPL. Wszystkie potrzebne funkcje (AES-256, podpisy, ContentReader, AcroForm, zakładki) zweryfikowane na 6.2.4.

## D-02. Bez Central Package Management
- Decyzja: wersje wspólne jako właściwości MSBuild w `Directory.Build.props`; moduł dodaje własne pakiety z jawną wersją we własnym csproj.
- Uzasadnienie: CPM wymagałby edycji wspólnego `Directory.Packages.props` przez każdego agenta (konflikty przy scalaniu worktree). Właściwości dają jedno miejsce dla wersji wspólnych bez konfliktów.

## D-03. Sesja trzyma bajty BEZ szyfrowania; szyfrowanie przy zapisie
- Problem: PDFsharp modyfikuje tylko z hasłem właściciela i gubi szyfrowanie przy zapisie (zweryfikowane).
- Decyzja: przy otwarciu PDFium (`FPDF_SaveAsCopy` z `FPDF_REMOVE_SECURITY`) tworzy odszyfrowaną kopię; `DocumentSnapshot.Encryption` niesie ustawienia; `IDocumentManager` szyfruje przy zapisie. Ochrona hasłem/usunięcie = `SetEncryptionCommand` (cofalne).
- Uzasadnienie: żaden moduł nie musi obsługiwać haseł; Cofnij obejmuje też zmiany zabezpieczeń. Koszt: odszyfrowane dane w pamięci procesu (zrzuty spuszczane na dysk – chronione DPAPI).

## D-04. Cofnij/Ponów oparte na zrzutach całego pliku
- Decyzja: rekomendacja koordynatora przyjęta: polecenie = funkcja „bajty → bajty”, historia przechowuje zrzuty (limit liczby z Ustawień + budżet 256 MB/dokument, starsze do `%TEMP%\ZetPDF\<pid>\snapshots`).
- Uzasadnienie: prostota i niezawodność (Cofnij zawsze przywraca identyczne bajty – test), te same metody dla Kreatora działań i testów. Koszt: każda edycja = pełny zapis PDFsharp (dla 500 stron/50 MB rzędu 1 s) – akceptowalne; interakcje ciągłe wykonują polecenie dopiero po zakończeniu gestu.

## D-05. Deklaratywny, stały katalog wstążki w Core (generowany)
- Decyzja: `tools/catalog/catalog.py` → `CommandIds.g.cs`, `RibbonCatalog.g.cs`, `docs/WSTAZKA.md`, początkowe teksty `Cmd.*` (pl/en) w App. Moduły tylko podpinają `ICommand` do id (`ICommandRegistry.Register`); polecenie bez obsługi jest wyszarzone.
- Uzasadnienie: powłoka i moduły pracują równolegle bez edycji wspólnych plików; ui-shell od razu widzi pełną wstążkę; walidacja konfliktów skrótów w skrypcie i teście. `RegisterExtra` daje elastyczność bez zmiany kontraktu.

## D-06. Teksty poleceń wstążki w zasobach App (ui-shell), teksty okien w zasobach modułów
- Uzasadnienie: polecenie (`Cmd.*`) jest elementem powłoki (wyszukiwanie Alt+Q, QAT, podpowiedzi); okna i komunikaty – modułu. Prefiksy kluczy wykluczają kolizje (test).

## D-07. Tesseract w single-file: zasoby osadzone + rozpakowanie do %LOCALAPPDATA%
- Problem: Tesseract 5.2.0 szuka natywnych DLL obok `Tesseract.dll` (`Assembly.Location`), które w single-file jest puste.
- Decyzja: `tesseract50.dll`, `leptonica-1.82.0.dll`, `pol/eng.traineddata` osadzone w `ZetPDF.Ocr.dll`; `TesseractBootstrap` rozpakowuje je do `%LOCALAPPDATA%\ZetPDF\ocr\5.2.0\` i ustawia `TesseractEnviornment.CustomSearchPath` (zweryfikowane w publikacji single-file).
- Uzasadnienie: spełnia wymóg jednego `ZetPDF.exe`. Koszt: ~19 MB w exe, jednorazowe rozpakowanie (~1 s). To nie jest „pobieranie z sieci” – dane pochodzą z exe.

## D-08. Ponowne szyfrowanie przy zapisie dokumentu otwartego z hasłem **[DO ZATWIERDZENIA]**
- Hasło użytkownika (właściciel nieznany): zapis z tym samym hasłem użytkownika, **nowym losowym hasłem właściciela** i tymi samymi uprawnieniami (efekt dla odbiorcy taki sam: nie może zdjąć ograniczeń).
- Hasło właściciela + puste hasło użytkownika: zapis z pustym hasłem użytkownika i tym samym hasłem właściciela.
- Hasło właściciela + niepuste, nieznane hasło użytkownika (AES-256 nie pozwala go odtworzyć): zapis z hasłem użytkownika = hasło właściciela; powłoka/security powinny pokazać komunikat `Core.Encryption.ResaveOwnerOnly` i zaproponować Ochrona > Ochrona hasłem.
- Uzasadnienie: jedyne rozwiązania bez pytania użytkownika o dodatkowe hasła; alternatywa – pytać o hasło otwierania przy pierwszym zapisie (security-signatures może to dodać przez `ISecurityService.CreateResaveSettings`).

## D-09. Uprawnienia egzekwowane w ZetPDF
- Decyzja: przy haśle użytkownika sesja ma uprawnienia z `/P`; `IDocumentCommand.RequiredPermission` sprawdzany przez sesję (`DocumentPermissionException`), `ActiveDocumentCommand` wyszarza polecenia.
- Uzasadnienie: PDFium daje odszyfrowaną kopię niezależnie od uprawnień, więc szacunek dla uprawnień musi zapewnić aplikacja.

## D-10. Kontener DI: Microsoft.Extensions.DependencyInjection
- Moduły rejestrują singletony w `ConfigureServices`; `ValidateOnBuild = true`; okna z DI lub `App.Services` (wyjątkowo).

## D-11. Shell w szkielecie zbudowany w kodzie (bez XAML okna)
- Szkielet `MainWindow` (Fluent `RibbonWindow`) buduje wstążkę z katalogu w kodzie – minimalny działający przykład. ui-shell może przepisać na XAML/MVVM, zachowując zasady (wyszarzanie, `RegistryCommandProxy`, skróty przez `Shortcut`).

## D-12. Skróty bez Ctrl/Alt obsługiwane ręcznie
- WPF `KeyGesture` odrzuca `Shift+litera` i same litery (zweryfikowane). Powłoka obsługuje je w `PreviewKeyDown` tylko poza polami tekstowymi (`Shortcut.RequiresNonTextFocus`). Klawisze jednoliterowe – dodatkowo tylko przy włączonej opcji w Ustawieniach.

## D-13. Nazwy typów kolidujące z WPF
- `PdfTextBlock`, `PdfTextSelection` zamiast `TextBlock`/`TextSelection` (kolizja z `System.Windows.Controls.TextBlock` / `System.Windows.Documents.TextSelection` wykryta przy budowie).

## D-14. System.Security.Cryptography.Pkcs 8.0.2
- PDFsharp 6.2.4 zależy od 8.0.1 (CVE-2026-84008, CVE-2026-70162 – DoS); Core podnosi do 8.0.2 (pierwsza poprawiona z linii 8.x).

## D-15. Załączniki (panel i usługa) – annotations-forms
- Załączniki plikowe są adnotacjami (FileAttachment) lub drzewem EmbeddedFiles – bliżej komentarzy. Oczyszczanie (pages-engine) usuwa je niezależnie, bez użycia `IAttachmentService`.

## D-16. Silnik zakładek (zapis) – renderer-viewer
- Panel zakładek jest w widoku; `IBookmarkService` (PDFsharp) implementuje renderer-viewer w `ZetPDF.Viewer`. „Zakładki z nagłówków” (edit-convert-ocr) generuje drzewo (`ILinkService.GenerateBookmarksFromHeadingsAsync`) i zapisuje przez `IBookmarkService`.

## Zatwierdzenia koordynatora (krok 1)
- D-01 zatwierdzona: PDFsharp 6.2.4 (MIT). Sprawdzono .nuspec: 6.5.0 = AGPL-3.0-only, 6.2.4 = MIT. Aktualizacja PDFsharp powyżej 6.2.x zabroniona bez sprawdzenia licencji.
- D-08 zatwierdzona (ponowne szyfrowanie przy zapisie).
- tessdata: agent edit-convert-ocr może zamienić tessdata_fast na tessdata (standard, Apache 2.0), jeśli jakość polskich znaków będzie wyraźnie lepsza; rozmiar exe jest drugorzędny.
- Publikacja z -p:DebugType=None.


## Zatwierdzenia koordynatora (krok 3, pages-engine)
- Pages.Tests może referować ZetPDF.Rendering (tylko weryfikacja wyników testów).
- Rozszerzenie IStampingService o RemovePageNumbersAsync i GetStoredOptionsAsync odłożone do scalenia wszystkich modułów (zmiana kontraktu w Core przez koordynatora); do tego czasu polecenia używają metod klasy StampingService.


## Do zrobienia po scaleniu wszystkich modułów (koordynator, zmiany kontraktów)
1. [x] IDocumentView.ShowAnnotations (render bez RenderFlags.Annotations, widżety zostają) – Pokaż/Ukryj komentarze nie zmienia pliku (annotations-forms + renderer-viewer). → D-17.
2. [x] Hak przed zapisem w IDocumentManager – „Spłaszcz przy zapisie” (annotations-forms). → D-18.
3. [x] AnnotationUpdate.TextStyle – zmiana czcionki FreeText. → D-19.
4. [x] IStampingService: RemovePageNumbersAsync, GetStoredOptionsAsync (pages-engine, Kreator działań). Dodane do kontraktu; rzutowania na `StampingService` w poleceniach Pages usunięte, testy Pages wołają przez interfejs.
5. [x] Zdublowany pasek nawigacji stron (ViewerBar + pasek stanu powłoki) – zostawić jeden. Został pasek stanu powłoki (wymaganie: „pasek stanu z numerem strony i powiększeniem”); `ViewerBar` usunięty z Viewer (z tekstami `Viewer.Bar.*`), `TryParsePercent` przeniesione do `PageLayout` (okno Powiększenie do), FindBar bez zmian.
6. [x] BouncyCastle nieużywany w ZetPDF.Security – usunąć albo uzasadnić. Usunięty: brak `Org.BouncyCastle` w kodzie i w zależnościach przechodnich (project.assets.json); podpisy/CMS/TSA działają na System.Security.Cryptography.Pkcs. Usunięty też z `docs\LICENCJE.md` i listy licencji w oknie O programie.
7. [x] Annotations: FillSign.Flatten po scaleniu ma używać IPageOperations.FlattenAsync z pages-engine – sprawdzić /AS widżetów. Sprawdzone testem `FlattenWidgetStateTests` (IntegrationTests: FormService → FlattenAsync → render PDFium bez adnotacji): zaznaczone pole wyboru i wybrana opcja mają znacznik, odznaczone – nie. Poprawka w Pages: widżet bez `/AS` bierze stan z `/V` pola (wcześniej był usuwany bez rysowania).


## Zmiany po scaleniu (integrator)

## D-17. Pokaż/Ukryj komentarze tylko w widoku
- Problem: `Comment.ShowAll/HideAll` zmieniały flagę Hidden w pliku (polecenie dokumentu, stan „zmodyfikowany”).
- Decyzja: `IDocumentView.ShowAnnotations` (kontrakt). `false` = render widoku bez `RenderFlags.Annotations` (FPDF_ANNOT), z `RenderFlags.FormFields`. Sprawdzone renderem PDFium 157: `FPDF_RenderPageBitmap` bez FPDF_ANNOT nie rysuje adnotacji, a `FPDF_FFLDraw` rysuje tylko widżety pól – rozdzielenie jest pełne (test `ShowAnnotationsTests`). Polecenia przełączają aktywny widok; `IAnnotationService.SetAllHiddenAsync` zostaje w usłudze (bez polecenia).
- Kompromis: widżety w pliku bez `/AcroForm` (brak środowiska formularza PDFium) znikają razem z komentarzami; miniatury i druk nie zależą od przełącznika.

## D-18. Hak przed zapisem: `IDocumentManager.AddSaveTransform`
- Problem: „Spłaszcz przy zapisie” spłaszczało od razu (polecenie dokumentu), bo nie było zdarzenia „przed zapisem”.
- Decyzja: `IDisposable AddSaveTransform(SaveTransform)` (delegat `session, bajty → bajty`). Haki działają w `GetBytesForSaveAsync` przed szyfrowaniem, więc obejmują Zapisz, Zapisz jako i podpis cyfrowy; sesja (zrzut, historia, „zmodyfikowany”) się nie zmienia. Wybrano przekształcenie zamiast zdarzenia z modyfikowalnym argumentem – asynchroniczne, jawna kolejność, łatwe do testu.
- `FillSign.Flatten` w katalogu zmieniono z Button na Toggle (`tools/catalog/catalog.py`, wygenerowane pliki): włączenie (z potwierdzeniem) oznacza dokument, hak modułu Annotations spłaszcza komentarze i pola (`IPageOperations.FlattenAsync`, rezerwa: własne spłaszczanie). Stan per sesja, nie zapisywany.

## D-19. `AnnotationUpdate.TextStyle`
- Decyzja: nowe pole kontraktu; dla adnotacji `/FreeText` (pole tekstowe, maszyna do pisania, objaśnienie) zmienia rodzinę i rozmiar czcionki oraz kolor tekstu (`/DA`, `/DS`) i odtwarza `/AP`; dla innych rodzajów ignorowane. Stosowane po `Color` (który w FreeText ustawia kolor tekstu i ramki). Bold/Italic pomijane – model FreeText ich nie obsługuje także przy tworzeniu. Okno Właściwości pokazuje czcionkę i rozmiar dla FreeText (odczyt przez `AnnotationService.GetTextStyleAsync`, metoda modułu, poza kontraktem).

## D-20. Ostrzeżenia kompilacji (cel: 0)
- CS0618 PdfPig 0.1.16: `IPdfImage.Bounds` → `BoundingBox` (DocxWriter), `Letter.GlyphRectangle` → `BoundingBox` (LinkService). Przed zamianą sprawdzono tymczasowym testem na dokumencie z tekstem, obrazem i stroną z `/Rotate 90`, że stare i nowe właściwości zwracają identyczne prostokąty (stare są tylko aliasami); testy DOCX i łączy przechodzą bez zmian.
- CS8602 w `ConversionTests` (nullowalne `MainDocumentPart.Document` w OpenXml 3.5) – jawne `!`.

## D-21. `OcrPageResult.RegionOfInterest`
- Problem: wynik OCR obszaru był oznaczany obejściem `ConditionalWeakTable` w `TesseractOcrService.AsRegionResult` (znacznik ginął przy każdej kopii wyniku, np. po poprawkach w oknie przeglądu – okno musiało go przenosić ręcznie).
- Decyzja: opcjonalny parametr kontraktu `OcrPageResult.RegionOfInterest` (`PdfRect?`, domyślnie null = cała strona). `ApplyTextLayerAsync` decyduje per strona: z obszarem – dopisuje warstwę `ocr-region`, bez – zastępuje warstwę strony. Obejście i metody `AsRegionResult`/`IsRegionResult` usunięte; kopie `with` zachowują obszar (test `Region_ocr_recognizes_only_the_area`).

## Pozostałe zadania integratora (po liście 1–7)
8. [x] Ostrzeżenia kompilacji → D-20 (build: 0 ostrzeżeń, 0 błędów).
9. [x] `OcrPageResult.RegionOfInterest` → D-21.
10. [x] Test dymny Kreatora działań na prawdziwych usługach z DI (`tests\ZetPDF.IntegrationTests\ActionWizardSmokeTests.cs`): „Przygotuj do dystrybucji”, „Zoptymalizuj dla Internetu”, „Archiwizuj” na 2 plikach z TestData (metadane+załącznik+JavaScript, skan) – wyniki otwierają się w PDFium; dystrybucja: puste Title/Subject/Keywords, brak `/JavaScript` i załączników, znak wodny i nagłówek/stopka wykryte (`DetectMarksAsync`) i tekst znaku wodnego obecny; Internet: wynik nie większy od oryginału, ta sama liczba stron; archiwum: tytuł uzupełniony tylko gdy pusty, JavaScript usunięty, załączniki zostają.

## Decyzje koordynatora po raporcie testera (2026-10-17)
- B-1 naprawiony: adres /URI zapisywany jako ASCII (punycode hosta + kodowanie procentowe UTF-8); adresy ASCII bez zmian (LinkService.ToAsciiUri).
- B-2 naprawiony: kolejność statusu podpisu: integralność → ważność certyfikatu → zmiany po podpisaniu → zaufanie (SignatureValidator). Niezaufany certyfikat nie ukrywa już „zmieniono po podpisaniu” ani „certyfikat nieważny”.
- D-22 (O-1): plik RC4 (i AES-128) otwarty z hasłem i zapisany jest szyfrowany ponownie AES-256 – świadomie (RC4 jest przestarzały). Do opisania w OGRANICZENIA i w komunikacie przy zapisie.
- D-23 (O-2): plik z samym hasłem uprawnień, otwarty bez hasła właściciela, nie może zostać po cichu zapisany z nowym losowym hasłem właściciela. Przed zapisem takiego pliku program ma ostrzec i zaproponować: (a) podanie hasła właściciela (zachowanie ochrony), (b) ustawienie nowego hasła uprawnień, (c) zapis bez ochrony. Do wykonania przez recenzenta końcowego.
- O-3: komunikaty wyjątków domenowych pokazywane użytkownikowi mają być zlokalizowane (klucze .resx), bez angielskich wstawek w polskim interfejsie. Do wykonania przez recenzenta końcowego.
- Test EdgeCaseTests.Copy_selected_text_with_polish_characters_to_clipboard bywa niestabilny przy równoległym uruchomieniu (schowek współdzielony); pojedynczo przechodzi 3/3. Do przeniesienia do SequentialCollection.

## Wykonanie przez recenzenta końcowego (2026-10-21)
- D-22: zachowanie bez zmian (RC4 → AES-256 przy zapisie); opis w `OGRANICZENIA.md` (rozdz. 9), po zapisie pasek stanu: „Zapisano: … Szyfrowanie RC4 128-bitowe zastąpiono AES-256.” (`DocumentActions.SavedStatus`, test `OwnerOnlyProtectionTests.Status_after_save_tells_about_reencryption`).
- D-23: `PdfEncryptionSettings.GeneratedOwnerPassword` (nowa właściwość `init` kontraktu, ustawiana w `CreateResaveSettings` dla otwarcia bez hasła właściciela); `DocumentManager.RequiresOwnerPasswordDecision/SetSaveEncryptionOverride/GetEffectiveSaveEncryption` (poza interfejsem – nadpisanie dotyczy tylko zapisu, historia Cofnij bez zmian, bo zmiana zabezpieczeń poleceniem wymaga pełnych uprawnień); powłoka: `OwnerProtectionGuard` przed Zapisz i Zapisz jako – okno z wyborem: hasło uprawnień (sprawdzane na pliku źródłowym `PdfSecurityHelper.IsOwnerPassword`), nowe hasło uprawnień (AES-256, wpisane dwukrotnie), zapis bez ochrony, Anuluj. Wybór ważny do zmiany ustawień szyfrowania sesji (Przywróć pyta ponownie). Na poziomie `IDocumentManager.SaveAsync` (bez powłoki, np. testy) zachowanie D-08 bez zmian. Podpis cyfrowy (`GetBytesForSaveAsync`) używa wybranych ustawień, ale sam nie pyta.
- O-3: `Core.Localization.LocalizedError` – klucz `.resx` i argumenty w `Exception.Data` (`WithMessageKey`), teksty już przetłumaczone oznaczane `MarkLocalized`; `Describe` daje tekst w bieżącym języku, a dla wyjątków bez klucza – ogólny zlokalizowany opis wg rodzaju (plik zajęty, brak dostępu, schowek, zły format…); komunikat biblioteki tylko w szczegółach okna błędu i w dzienniku. Typy wyjątków bez zmian (testy i obsługa w kodzie działają jak wcześniej).
- Schowek w testach: `ClipboardTests` w `SequentialCollection`, `[ClipboardFact]` pomija test przy niedostępnym schowku systemowym (zablokowana sesja).

## Etap 3 – decyzje koordynatora (2026-10-21)
- D-24: indeks pełnotekstowy jako własny magazyn tekstu (GZip, wyszukiwanie liniowe) zamiast SQLite FTS5 – brak natywnych bibliotek w single-file.
- D-25: widok równoległy w widoku aktywnej karty (DocumentView.SetSideContent), 2 dokumenty, drugi tylko do podglądu.
- D-26: wyszukiwanie zaawansowane i sprawdzanie ułatwień dostępu jako panele boczne (Panel.AdvancedSearch 70, Panel.Accessibility 60).
- Eksporty CSV: neutralizacja formuł (apostrof przed = + - @ tab CR) i cytowanie wartości z , ; tab – wymagane we wszystkich eksportach CSV (po dwóch zgłoszeniach przeglądu bezpieczeństwa).
- Do recenzji etapu 3: test integracyjny „każde polecenie” odtwarza dźwięk przez prawdziwy syntezator (Read.CurrentPage) – w testach mowa ma iść do strumienia/atrapy; sprawdzić eskejpowanie HTML w raportach (porównanie, ułatwienia dostępu); dopisać nowe karty do --diag-smoke.

- Etap 3 scalony w całości (2026-10-21): build 0/0, 686 testów zielonych. Do zrobienia: dopisać ZXing.Net, Jint, Acornima, System.Speech do okna O programie i dist\LICENSES; recenzja etapu 3; ponowna publikacja exe.

## Wykonanie przez recenzenta – etap 3 (2026-10-21)
- Mowa w testach: SilentSpeechEngineFactory (System.Speech, SetOutputToNull) we wszystkich kontenerach testowych i w trybach --diag-* exe – bez dźwięku (RAPORT_KONTROLI R-12).
- Eskejpowanie HTML: raport ułatwień dostępu i raport porównania – poprawne; dodane testy z <script> w tytule i treści.
- --diag-smoke rozszerzony o polecenia etapu 3, --diag-screenshots o kartę Ułatwienia dostępu i panele etapu 3.
- ZXing.Net, Jint, Acornima, System.Speech dopisane do okna O programie i dist\LICENSES; exe opublikowany ponownie.
- Piaskownica JavaScript utwardzona (R-13…R-15); uszkodzony plik indeksu (R-16); szczegóły w RAPORT_KONTROLI rozdz. 9.


## Partia 1 nowych funkcji (2026-11-04)

## D-27. Autozapis i odzyskiwanie po awarii
- `AutoSaveService` (Core, zegar na wątku UI, zapis w `Task.Run`) i `RecoveryStore`: para plików `<pid>-<guid>.recovery` (PDF) i `.info` (opis JSON) w `%LOCALAPPDATA%\ZetPDF\recovery`, **oba** chronione DPAPI CurrentUser z entropią (także opis, bo ma ścieżkę i nazwę; DPAPI daje też integralność, więc podmieniony opis nie wskaże innego pliku docelowego). Plik tymczasowy + `File.Move(overwrite)`; publikacja i usuwanie pod wspólną blokadą, więc autozapis trwający podczas zapisu lub zamknięcia nie zostawi kopii.
- Porzucona = numer procesu nie działa albo ma inny czas startu (numery wracają). Pliki działających procesów nie są ruszane, śmieci po martwych procesach są sprzątane przy starcie.
- **Haseł nie ma na dysku, nawet pod DPAPI** (wymaganie: brak jawnych haseł; DPAPI chroni tylko przed innymi kontami, a hasło żyłoby na dysku dłużej niż dokument). Opis zapamiętuje tylko `IsProtected` i uprawnienia. Przy odzyskaniu ustawienia szyfrowania powstają z pliku oryginalnego tą samą ścieżką co przy otwieraniu (okno hasła, D-08, D-23). Gdy oryginału nie ma, nie jest zaszyfrowany (ochronę dodano w sesji) albo użytkownik anulował okno hasła – pytanie o nowe hasło otwarcia; anulowanie = dokument się **nie otwiera**, kopia zostaje. Nigdy nie ma otwarcia bez ochrony.
- Stan pliku z opisu (`SourceLength`, `SourceModifiedUtcTicks`, `SourceSigned`) trafia do `FileState`, więc ostrzeżenia przy zapisie (zmiana na dysku, podpisy, D-23) działają bez zmian. Odzyskany dokument ma `startModified` i oryginalną ścieżkę; gdy ten plik jest już otwarty w innej karcie – kopia bez ścieżki (Zapisz jako).
- Zamykanie Windows (`OnSessionEnding`) nie usuwa kopii. Wyłączenie autozapisu w Ustawieniach usuwa kopie bieżącego procesu. Tryby `--diag-*` autozapisu nie uruchamiają.

## D-28. Jedna instancja programu
- `SingleInstanceChannel` (App): nazwany potok `ZetPDF-<SID>-<sesja>`, `PipeOptions.CurrentUserOnly` po obu stronach, bez sieci. Rezerwacja nazwy limitem jednej instancji serwera (`FILE_FLAG_FIRST_PIPE_INSTANCE` w .NET), bez osobnego muteksu; ta sama instancja serwera jest ponownie używana przez `Disconnect`. Nasłuch rusza przed budową okna, a wiadomości czekają do końca startu (okno odzyskiwania).
- Wiadomość: 4 bajty długości + JSON, limit 256 KB, 256 plików, hasło do 1024 znaków, 5 s na klienta. Przyjmowane są tylko pełne ścieżki istniejących plików (bez `\.\`, `\?\`, strumieni NTFS; `..` normalizowane). Ścieżki sieciowe UNC zostają, bo „Otwórz za pomocą” na udziale musi działać, a to ten sam użytkownik i te same możliwości co przy `ZetPDF.exe \serwer\plik.pdf`. Treść wiadomości nie trafia do dziennika.
- Gdy kopia nie odpowie lub odrzuci wiadomość, kolejna uruchamia się osobno: pliki użytkownika mają się otworzyć. Wyjście na wierzch: klient woła `AllowSetForegroundWindow` dla procesu serwera.
- `--diag-*` i `ZETPDF_HOME` nie używają kanału. `ZETPDF_TEST_PIPE` (własna nazwa potoku) włącza go w izolowanym `ZETPDF_HOME` tylko po to, by test całego exe nie dotykał profilu i kopii użytkownika.

## D-29. Indeksy pełnotekstowe: ochrona DPAPI i okno zarządzania
- Nowy format pliku `.zidx`: znacznik `ZIDXDP2`, długość nagłówka, DPAPI(nagłówek: folder, data, liczba plików), DPAPI(GZip listy plików z tekstem). Osobny nagłówek pozwala pokazać listę bez odszyfrowywania całego tekstu. Plik uszkodzony lub z innego konta = indeks nieaktualny (`Load` zwraca null, budowa od nowa), na liście widoczny jako nieczytelny i można go tylko usunąć.
- Stary format (jawny GZip) jest czytany i od razu przepisywany na chroniony: przy `Load` i w przebiegu w tle po starcie (`ProtectLegacyFiles`), który usuwa też nieczytelne pliki starego formatu oraz pliki tymczasowe starsze niż dzień. Zaufanie do starego formatu kończy się na tym przepisaniu – nowe zapisy zawsze są chronione.
- `Index.Manage` to okno `IndexManageWindow`: folder, liczba dokumentów, rozmiar, data; Aktualizuj (przyrostowo), Usuń wybrany, Usuń wszystkie (z potwierdzeniem; pliki PDF nietknięte). Informacja o tekście na dysku w pytaniu przed budową i w oknie zarządzania.

## D-30. Zapis załącznika: nazwa niezaufana i typy wykonywalne
- `AttachmentSaveGuard` (Annotations): podpowiedź nazwy = `Path.GetFileName` (po zamianie `/` na odwrotny ukośnik), usunięcie znaków formatujących Unicode (np. odwrócenie kierunku ukrywające `.exe`), `IFileService.SanitizeFileName`, przedrostek `_` dla nazw urządzeń (`CON`, `NUL`, `COM1`…), limit 200 znaków z zachowaniem rozszerzenia. Do wyodrębnienia z PDF nadal służy nazwa oryginalna (klucz w drzewie załączników), a nie podpowiedź.
- Ostrzeżenie (`IDialogService.Confirm`) po wybraniu miejsca zapisu: sprawdzana jest nazwa z PDF, podpowiedź i nazwa wybrana w oknie, bo zawartość pliku się nie zmienia, a rozszerzenie można zmienić w oknie zapisu. Końcowe kropki i spacje są pomijane jak w Windows (`a.exe.` to `a.exe`).
- Lista obejmuje programy, skrypty i typy uruchamiane przez powłokę (też `.reg`, `.url`, `.chm`, `.msix`, `.py`). Dokumentów z makrami (`.docm`, `.xlsm`) na liście nie ma: to nie są pliki wykonywalne, a Office ma własne ostrzeżenia.
- `DialogService.SaveFile`: filtr `*.*` nie ustawia już `DefaultExt` (wcześniej `.*`), żeby nazwa bez rozszerzenia nie dostawała dopisku `*`.

## D-31. Okna niemodalne w kontrakcie dialogów, kalkulator księgowy
- `IDialogService.ShowModeless(Window)`: właścicielem jest okno główne (nie aktywne), bez paska zadań, styl motywu, powrót od razu. Moduł sam śledzi `Window.Closed`.
- Kalkulator: jedno okno na program; „Dodaj zaznaczenie” czyta zaznaczenie z aktywnego widoku w chwili kliknięcia, więc działa też po zmianie karty. Otwarcie dodaje zaznaczenie tylko, gdy jest nowe. „Wstaw na stronę” nie zamyka okna.
- Test `Every_registered_command_runs_without_error_with_cancelled_dialogs` po sprawdzeniu wymagał poprawki: `PresentationSource.CurrentSources` obejmuje wszystkie wątki, więc okno innego testu działającego równolegle dawało fałszywy błąd i wyjątek przy `Close` z obcego wątku (powtarzalne po dodaniu testu z prawdziwym oknem niemodalnym). Teraz liczy tylko okna własnego wątku; okna z atrapy `ShowModeless` są zamykane po każdym poleceniu. Test wykrywa okna otwarte z pominięciem usługi, ale tylko te, które polecenie faktycznie otworzy przy anulowanych oknach. Dlatego doszedł test na źródłach: bezpośrednie `.Show()` okien w modułach tylko z jawnej listy wyjątków (`CompareWindow`, panel czytania – patrz OGRANICZENIA 27.5).


## Rozbudowa – partia 2 (2026-11-04)

## D-32. PDF do XLSX: tabele z układu tekstu
- `TableDetector` (czysta logika): słowa PdfPig → wiersze po linii bazowej → segmenty po odstępie większym niż 0,85 rozmiaru czcionki → tabela to co najmniej dwa kolejne wiersze z dwoma segmentami. Kolumny wynikają z pionowych pasów pustego miejsca; od 4 wierszy jeden napis może biec w poprzek kolumn (nagłówek obejmujący dwie). Bez czytania linii siatki: działa tak samo dla tabel z liniami i bez nich, a PdfPig nie daje prostego dostępu do ścieżek powiązanych z tekstem.
- Liczby: `CellNumberParser` woli zostawić tekst niż zapisać wartość tysiąc razy inną. „1,234” i „1.234” rozstrzyga tylko reszta dokumentu (wskazówka: separator z zapisów jednoznacznych, przewaga co najmniej 3:1); bez niej zostają tekstem. Numery (zera na początku, 11 i więcej cyfr, telefon z plusem), daty i wartości z jednostką zostają tekstem.
- Komórki: `t="inlineStr"` albo liczba, elementu `<f>` nie ma w ogóle. Tekst zaczynający się od `=`, `+`, `-`, `@` (także znaki pełnej szerokości) ma styl tekstowy z `quotePrefix`, żeby przetrwał ręczną edycję w Excelu. To odpowiednik neutralizacji z eksportów CSV, ale bez zmiany treści komórki.
- `IConversionService` dostał `PdfToXlsxAsync` (dopisanie do kontraktu, jedyna implementacja to `ConversionService`).

## D-33. PDF do HTML
- Jeden plik UTF-8: akapity (`DocxWriter.Paragraphs`) z rozmiarem, pogrubieniem, kursywą i kolorem; obrazy jako `data:` z typem ustalonym po sygnaturze bajtów (PNG, JPEG), inne dane są pomijane. Opcja „tylko tekst” daje same `<p>` bez stylów i obrazów.
- Bezpieczeństwo: tekst z dokumentu (treść, tytuł) idzie przez `HtmlWriter.Escape`; rodzina czcionki pochodzi ze stałej listy `FontNames`, a kolor i rozmiar to liczby. Dodatkowo nagłówek CSP `default-src 'none'; img-src data:; style-src 'unsafe-inline'`: nawet gdyby coś przeszło przez escapowanie, skrypt ani zasób zewnętrzny się nie załaduje. Łącza z PDF nie są eksportowane, więc plik nie zawiera `<a>`.

## D-34. PDF/A-2b na bibliotekach permisywnych
- **Co da się zrobić:** PDFsharp 6.2.4 i PDFium nie mają ani konwersji do PDF/A, ani walidatora. Nie da się ich dodać licencyjnie (veraPDF: GPL/MPL i Java, iText: AGPL). PDFsharp ma tylko `SetPdfA()` oznaczające plik jako PDF/A-1A, bez żadnych zmian w treści, więc nie jest używane. Powstało własne sprawdzenie wybranych reguł i konwersja konserwatywna: plik z `pdfaid` zapisuje się dopiero wtedy, gdy własne sprawdzenie wyniku nie zgłasza błędów. To nie jest walidacja i interfejs mówi to wprost.
- **XMP:** PDFsharp przy każdym zapisie bezwarunkowo zastępuje `/Metadata` własnym pakietem (`PdfDocument.PrepareForSave`), nie ma opcji, żeby to wyłączyć. Dlatego pakiet z `pdfaid` jest dopisywany po zapisie jako aktualizacja przyrostowa tego samego obiektu, z wartościami z zapisanego `/Info` (także `/Producer`, który PDFsharp ustawia sam). Skutek: każdy późniejszy zapis przez PDFsharp usuwa `pdfaid`, co jest pożądane, bo edytowany plik przestaje być sprawdzony. Krok PDF/A w Kreatorze musi być ostatni.
- **Czcionki:** osadzanie tylko czcionek prostych z WinAnsi, rodziną zgodną metrycznie lub tą samą i szerokościami zgodnymi z `/Widths`. Osadzany jest cały plik czcionki (wynik rośnie o około 0,5 MB na krój), bo podzbiór wymagałby przebudowy `cmap`. Reszta to rasteryzacja strony za zgodą.
- **CMYK:** rasteryzacja strony za zgodą, bo wynik ma profil sRGB, a podmiana kolorów w operatorach zmieniłaby wygląd bez zarządzania kolorem. Istniejący OutputIntent jest zastępowany przez sRGB.
- **Pola bez wyglądu:** rasteryzacja strony, a nie usunięcie, bo pole może mieć wartość. PDFium rysuje wtedy wartość na obrazie.
- **Załączniki:** PDF/A-2 dopuszcza tylko załączniki PDF/A, a tego nie umiemy sprawdzić, więc każdy jest błędem i konwersja je usuwa za zgodą.
- **Kreator („Archiwizuj”):** działa przez kontrakt `IPdfAService` w `Core` (krok `PdfA`, dopisany na końcu). Wsadowo nie ma kogo zapytać, więc krok działa bez rasteryzacji i bez usuwania treści. Gdy to niemożliwe, plik zostaje zwykłym PDF po wcześniejszych krokach, a `ActionFileResult.Warning` trafia do raportu w oknie. Plik nie udaje PDF/A, a załączniki nie znikają po cichu.
- **Polecenie Do PDF/A:** wymaga uprawnień ModifyContent i CopyContent, bo wynik nie ma szyfrowania i nie może omijać ograniczeń pliku chronionego. Zapis tylko do innego pliku.
- **Pliki spreparowane (po przeglądzie bezpieczeństwa):** chodzenie po drzewach bez nieograniczonej rekurencji: `IsEmbedded` schodzi jeden poziom i tylko do czcionki CID, formularze i wzory mają zbiór aktywnych i limit 32 poziomów (cykl lub głębiej = strona „nieczytelna”, a nie awaria), drzewa nazw i `PdfATree` są iteracyjne. Akcja to słownik wskazany z miejsca na akcję (`/A`, `/OpenAction`, `/PA`, wpisy `/AA`, łańcuch `/Next` od takiego słownika) w dowolnym słowniku dokumentu, także zagnieżdżonym, a nie słownik o określonych kluczach. Decyzja przez listę dozwolonych (`PdfAActions`): GoTo, GoToR, GoToE, Thread, URI, SubmitForm, Named z nawigacją po stronach; brak `/S` lub nieznany typ to akcja zabroniona. Klucze `/P`, `/K`, `/Type` niczego nie zwalniają. Lista i reguły `/AA` oraz `/A` pól pochodzą z pamięci reguł ISO 19005-2 (6.4.1, 6.5.1, 6.5.2), bo normy nie ma pod ręką. Świadome rozstrzygnięcia: `/Next` jako klucz nie jest miejscem na akcję w ogólności, bo ma go każda pozycja zakładek (liczy się tylko łańcuch od akcji). `/A` bez `/S` i z `/O` to obiekt atrybutów elementu struktury: nie ma czego wykonać, a dopisanie `/O` do słownika z `/S` nic nie daje. `/OpenAction` jako tablica to miejsce docelowe. Słownik `/S /JavaScript` niewskazany z żadnego miejsca nie ma ścieżki wykonania i nie jest usuwany, jeśli pozostaje osiągalny inną drogą (np. jako element drzewa struktury). Konserwatywnie `/AA` znika wszędzie, także w adnotacjach innych niż widżety, a pole traci `/A` każdego typu.
- **Weryfikacja niezależna:** w systemie nie ma veraPDF ani pikepdf; nic nie instalowano. Sprawdzono pypdf (struktura, tryb strict) i PyMuPDF (render, osadzenie czcionek). To nie jest walidacja PDF/A: **wyniku nie potwierdził żaden zewnętrzny walidator PDF/A**.

## D-35. Skanowanie przez WIA 2.0
- Obiekty automatyzacji `WIA.DeviceManager` z `wiaaut.dll` (składnik Windows), typy dynamiczne, bez pakietów. Kod COM jest za interfejsem `IScannerBackend`; `ScanWorkflow` (przebiegi, PDF, OCR) ma testy na atrapie. Praca na wątku STA poza UI (`StaThread`), limit 15 minut na przebieg.
- Format transferu BMP, bo konwersja do innych formatów zależy od sterownika, a BMP niesie DPI. PDF powstaje przez istniejące `ImagesToPdfAsync` z pliku tymczasowego w `IAppPaths.TempDirectory`, usuwanego zaraz po konwersji (skany to dane poufne). OCR przez `IOcrService.OcrDocumentAsync`, przed utworzeniem dokumentu.
- Ustawienie, którego urządzenie nie obsługuje (np. dana rozdzielczość), zostaje domyślne i nie przerywa skanowania.

## D-36. PDF ze strony WWW przez Microsoft Edge: wyjątek od braku sieci
- **Program nie łączy się sam z siecią, tylko na to polecenie i przy TSA.** Połączenie wykonuje zainstalowana Edge po wpisaniu adresu i zatwierdzeniu okna, które mówi to wprost. Przed zatwierdzeniem nic nie jest pobierane. Program nie dołącza przeglądarki.
- Zgoda na wyjątek jest z polecenia właściciela projektu (partia 2). Plik README (pl, en), `OGRANICZENIA.md` i `ZASADY_ZESPOLU.md` to opisują.
- Tylko http, https i lokalne `.html`/`.htm`. Wartość zaczynająca się od `-` jest odrzucana, argumenty idą przez `ProcessStartInfo.ArgumentList`, a adres za znacznikiem `--`, po którym Chromium nie czyta przełączników. Odrzucane też: inne schematy (`javascript:`, `data:`, `edge:`, `file://` poza lokalnym), ścieżki sieciowe i `user:hasło@` w adresie.
- **msedge.exe to starter:** kończy się po kilkudziesięciu milisekundach z kodem 0, a przeglądarka działa jako proces potomny. Sprawdzone uruchomieniem. Dlatego `SystemProcessRunner` śledzi procesy potomne migawką systemu i przy limicie czasu lub anulowaniu kończy tylko je, po numerach, ze sprawdzeniem czasu startu. Edge użytkownika nie jest procesem potomnym, więc nie zostanie ruszona. Gdy plik wynikowy jest gotowy, procesy mają 5 s na zakończenie.
- Profil tymczasowy w `IAppPaths.TempDirectory`, usuwany po każdym zakończeniu (z ponawianiem, bo procesy potomne chwilę trzymają pliki). Wyłączone: rozszerzenia, synchronizacja, ruch w tle przeglądarki, nagłówek i stopka wydruku.

## D-37. DOCX, XLSX i PPTX przez zainstalowany Office
- Automatyzacja COM, typy dynamiczne, bez pakietów PIA. Kod COM za interfejsem `IOfficeConverter`, opcjonalny parametr konstruktora `ConversionService`; `CombineFilesAsync` obsługuje te formaty, więc działa w Z pliku (Ctrl+N) i Połącz pliki. Bez aplikacji: pliki są pomijane z komunikatem, reszta działa jak dotąd. Na poziomie usługi jest błąd z komunikatem. Wcześniej plik binarny wybrany przez „Wszystkie pliki” trafiał do PDF jako tekst.
- Bezpieczeństwo: `AutomationSecurity = msoAutomationSecurityForceDisable` przed otwarciem, tylko do odczytu, `UpdateLinks = 0`, aplikacja niewidoczna, bez ostrzeżeń. Hasło fikcyjne przy otwarciu: plik chroniony kończy się błędem, a nie niewidocznym oknem pytania o hasło (PowerPoint nie ma tego parametru, patrz OGRANICZENIA).
- Proces: Word i Excel przez `CreateObject` zawsze dostają nowy proces; „nasz” to ten, którego nie było przed utworzeniem obiektu. Zamknięcie w `finally` (`Quit`, zwolnienie COM, 10 s na koniec, potem `Kill` po numerze). Po limicie 3 minut wątek STA jest porzucany, a proces kończony po numerze. PowerPoint ma jedną instancję: gdy użytkownik już go używa, nie ma `Quit` ani `Kill`, zamykana jest tylko nasza prezentacja.


## Rozbudowa – partia 3 (2026-11-04)

## D-38. Limit czasu wyrażeń regularnych w piaskownicy a niestabilny test
- **Przyczyna:** `Hardened_builtins_match_native_jint` działał na produkcyjnym limicie 200 ms (`Constraints.RegexTimeout` w `ScriptRunner`), a to jest czas zegarowy, razem z kompilacją JIT przy pierwszym użyciu wyrażenia. Pod obciążeniem przekraczał go poprawny skrypt i w raporcie pojawiał się `RegexMatchTimeoutException`, a `Assert.Empty(report.Errors)` padał. Powtórzone: 48 pętli obciążających procesor, 3 z 6 przebiegów czerwonych. Silnik natywny w teście nie był źródłem błędu (domyślny limit Jint jest dłuższy).
- **Naprawa w testach:** testy wyniku funkcji (`FunctionalRunner`, też `Hardened_builtins_keep_normal_behaviour`, który miał tę samą wadę) dostają długie limity czasu przez nowy `ScriptTimeLimits`. Asercje bez zmian. Po zmianie: 10 z 10 zielonych przy tym samym obciążeniu.
- **Kod produkcyjny:** wyjątek już wcześniej kończył się błędem skryptu (`catch (Exception)` w `RunEvent`), nie awarią. Brakowało zliczenia go jako limitu: `IsLimit` obejmuje teraz `RegexMatchTimeoutException`, więc złośliwe wyrażenie liczy się do automatycznego wyłączenia skryptów dokumentu po 3 przebiegach. Test: katastrofalne wyrażenie na domyślnym limicie daje `null`, błąd w raporcie, `HitLimits`, pole bez zmian. Limit programu zostaje 200 ms (test `Default_regex_limit_stays_200_ms`).

## D-39. Rozpoznawanie pól formularza (Form.RecognizeFields)
- **Dane:** render strony przez PDFium bez adnotacji i pól (150 DPI, mniej dla stron ogromnych: bok najwyżej 3000 px, 6 Mpx, bo rozmiar strony w pliku jest niezaufany) oraz słowa z `IRenderDocument.GetPageTextAsync`. Struktura pliku nie jest używana, więc metoda działa tak samo dla płaskiego PDF, skanu po OCR i skanu bez tekstu.
- **Reguły (`FieldRecognizer`, czysta funkcja na bitmapie i tekście):** obszary zamknięte to białe składniki spójne niedotykające brzegu bitmapy, liczone po dylatacji 1 px (zamyka szczeliny w liniach skanu). To lepsze od szukania czterech krawędzi, bo w siatce krawędzie są wspólne. Obszary stykające się tworzą grupę: grupa o co najmniej 2 wierszach i 2 kolumnach to tabela i wypada w całości; jedna kolumna z tekstem w komórce też. Rząd co najmniej 3 pustych kratek to jedno pole tekstowe (PESEL). Pusty kwadrat 6–22 pt to pole wyboru. Pusty prostokąt co najmniej 30x10 pt to pole tekstowe, ale na stronie z tekstem tylko z etykietą. Linia to pole tekstowe, jeśli nad nią jest 9 pt wolnego miejsca (odpada tekst podkreślony), nie styka się z pionową kreską ani z obszarem zamkniętym (odpadają tabele) i ma etykietę z lewej albo jeden ciągły podpis pod sobą (wiele kolumn pod linią to nagłówek tabeli). Ciąg co najmniej 5 znaków `_` lub `.` z etykietą to pole, chyba że za nim stoi liczba (spis treści).
- **Nazwa:** etykieta po `FieldNames.Sanitize` (litery, cyfry, `_`, `-`; bez kropki, bo to separator hierarchii), unikalna względem pól dokumentu i ich prefiksów, bez rozróżniania wielkości liter. Bez etykiety: nazwa ogólna z zasobów z numerem.
- **Duplikaty:** propozycja pokrywająca widżet w ponad 30% mniejszego z pól jest pomijana.
- **Tworzenie:** `FormService.CreateFieldsAsync` to jeden zapis i jeden wpis Cofnij przez `DelegateDocumentCommand`, istniejący `FormEngine.Create`. Etykieta trafia do `/TU` (opis pola).
- **Ochrona przed plikiem spreparowanym:** liczba przebiegów pikseli najwyżej 400 tys. (ponad limit strona bez propozycji), liczba obszarów do porównania parami 5000. Test na stronie z gęstą kratką punktów.
- **Limity i anulowanie (po przeglądzie bezpieczeństwa):** szukanie etykiet nie jest już kwadratowe: `WordIndex` (kubełki po Y, tablice posortowane po krawędziach, najwyżej 1024 przeglądane słowa na zapytanie, także gdy wszystkie słowa leżą w jednym miejscu), a pytanie „czy w prostokącie jest słowo” odpowiada w O(1) z maski `WordMask` o rozmiarze zależnym od bitmapy. Limity na stronę: 200 tys. znaków, 20 tys. słów, 3000 linii, 5000 obszarów zamkniętych, 400 tys. przebiegów pikseli, 2000 surowych propozycji, 5000 widżetów. Strona ponad limit nie daje propozycji i trafia do `FieldRecognitionResult.SkippedPages`; okno i komunikat ją wymieniają. Wszystkie pętle zależne od danych reagują na anulowanie.

## D-40. Automatyczne oznaczanie struktury (Access.AutoTag)
- **Wstawianie do oryginalnych bajtów, nie zapis przez parser.** `ContentTokenizer` znajduje granice operatorów w bajtach i tam trafiają `/P <</MCID n>> BDC` oraz `EMC`. Reszta treści się nie zmienia, więc nie ma ryzyka, że serializacja `CSequence` zmieni napisy czy liczby. `ContentWalker` daje semantykę (glify, obiekty, stan), a zgodność jego listy operatorów z tokenami jest sprawdzana; rozbieżność oznacza stronę nieczytelną. Strumienie zostają osobne, ze skopiowanymi kluczami słownika, bo oznaczenia ZetPDF (znak wodny, numeracja) są rozpoznawane po kluczu strumienia. Zmieniony strumień to nowy obiekt, bo bywa współdzielony przez strony.
- **Parser PDFsharp 6.2.4:** słownik w treści daje pseudooperator `Dictionary`, a następne `BDC` nie ma operandów (sprawdzone uruchomieniem). Obrazy w treści (BI…EI) nie dają się zmapować, więc taka strona jest pomijana i wymieniana.
- **Ochrona przed stosem:** parser PDFsharp jest rekurencyjny, a przepełnienia stosu nie da się złapać. Iteracyjny `ContentTokenizer` działa przed nim i odrzuca zagnieżdżenie ponad 64 poziomy (test: 2 mln nawiasów).
- **Klasyfikacja:** tekst główny to rozmiar najczęstszy ważony liczbą glifów. Co najmniej 1,15 rozmiaru głównego to nagłówek; poziomy to kolejne rozmiary malejąco, H1–H6. Pogrubiony pojedynczy wiersz do 80 glifów w rozmiarze tekstu to nagłówek najniższego poziomu. Listy: co najmniej dwa kolejne bloki zaczynające się punktorem lub numerem (L, LI, LBody), bez osobnego Lbl. Figura: operator `Do` (obraz lub formularz), `/A` z `/BBox`, bez `/Alt`, więc sprawdzanie uczciwie zgłasza brak tekstu alternatywnego. Obraz na ponad 80% strony z tekstem to tło skanu i artefakt.
- **Artefakty:** tekst w marginesie 8% wysokości powtarzający się na co najmniej połowie stron (z cyframi sprowadzonymi do `#`) albo sam numer strony; ścieżki, `sh`; istniejące `/Artifact` zostają nietknięte, co obejmuje oznaczenia ZetPDF.
- **Zastępowanie:** `BDC` z MCID (słownik w treści lub `/Properties`) i ich `EMC` są usuwane, `/StructParents` i `/StructParent` czyszczone (formularze z ochroną przed cyklem, limit 16 poziomów), reszta znaczników (`/OC`, `/Span`) zostaje. Zakresy wstawień nie przekraczają granicy BT/ET, znacznika ani strumienia.
- **Kontrola wyniku:** przy dostępnym rendererze każda strona jest porównywana bajt w bajt przed i po (do 1 piksela na punkt); różnica oznacza stronę bez tagów w raporcie (powód `RenderChanged`). Gdy nie zostanie żadna strona, dokument się nie zmienia.
- **Limity na stronę:** 32 MB treści, 500 tys. operatorów, 1 mln glifów, 20 tys. wierszy, 500 tys. liter PdfPig, 2000 liter przeglądanych na wiersz. Ponad limit strona jest pomijana i wymieniana. Wszystkie pętle reagują na anulowanie.

## D-41. Druk wsadowy i broszura
- **Broszura:** `BookletImposition` to czysta funkcja: dopełnienie do wielokrotności 4 pustymi stronami na końcu i kolejność składki, np. dla 8: (8,1), (2,7), (6,3), (4,5). Testy dla n = 1..9 i kontrola, że każda strona występuje raz. `PrintOptions.Booklet` to parametr dopisany na końcu rekordu (zgodność wsteczna), nadrzędny wobec stron na arkuszu. Arkusz poziomy, dupleks po krótszej krawędzi ustawiany w bilecie tylko, gdy sterownik go zgłasza. Okno mówi o druku dwustronnym, bo programowo tego nie sprawdzimy.
- **Wspólne ustawienia:** `PrintSettingsPanel` wydzielony z okna druku; używają go oba okna.
- **Druk wsadowy w trzech etapach:** sprawdzenie plików w tle, pytania o hasło na wątku okna, poza oknem postępu (`IDialogService.AskPassword` nie jest wołane z okna modalnego postępu), druk w tle. Każdy plik to osobne zadanie drukarki, wczytywany dopiero przed drukiem i zwalniany zaraz po nim. Hasło jest tylko w pamięci, zerowane po wydruku pliku. Błąd pliku nie przerywa reszty. Wynik per plik w oknie raportu.
- **Uprawnienie do druku:** z `IPdfRenderer.ProbeAsync` (`FPDF_GetDocPermissions`). Test: AES-256 z pustym hasłem użytkownika i `PermitPrint = false` jest pomijany. Hasło właściciela daje pełne uprawnienia.

## D-42. Pliki spreparowane: głębokość w PDFsharp i bomby kompresji (przegląd bezpieczeństwa)
- **Pomiar w osobnym procesie (`scratch\DepthProbe`, kod wyjścia, zapis w `scratch\logs\depthprobe*.txt`, Task.Run jak w programie).** Tablice i słowniki głębokie na 3000 poziomów, także w strumieniu obiektów, wywracały **cały proces** już w `PdfReader.Open` (1000 poziomów przechodzi, 3000 nie; plik może mieć kilkanaście kB). PDFium i PdfPig przechodzą bez błędu do 1 mln poziomów. `ContentReader`: tablice 10 tys. poziomów wywracają proces, słowniki w treści są płaskie. Wątek ze stosem 256 MB wytrzymuje ponad 580 tys. poziomów tablic (`PdfReader`), a 1 mln pada.
- **Otwieranie pliku:** wszystkie `PdfReader.Open` idą przez `PdfSharpEdit.OpenGuarded`: najpierw `PdfStructureGuard`, potem wątek z dużym stosem. `DocumentManager.LoadAsync` sprawdza plik na starcie, więc zbyt głęboki plik to komunikat o uszkodzeniu, a nie awaria przy pierwszej edycji. Skan iteracyjny, limit 256 poziomów. Skan nie ufa granicom strumieni: każde wystąpienie `n g obj` i `trailer` to punkt startu, bo parser wchodzi do obiektu tylko tak. W strumieniach obiektów start z każdego przesunięcia nagłówka. Niepasujące zamknięcie nie zmniejsza głębokości. Budżet przeglądanych bajtów 16 razy rozmiar danych (odpowiedź na kwadratowe `1 0 obj 1 0 obj`). Rozpakowanie strumieni obiektów strumieniowo, łącznie do 256 MB. Po tym wszystkim program działa na zwykłych stosach, bo głębokość wyniku jest niewielka; zapis PDFsharp też jest rekurencyjny.
- **Strumień obiektów, którego nie da się rozpakować:** bez szyfrowania (np. podwójny filtr) to odrzucenie, bo byłaby to prosta droga ukrycia zagnieżdżenia. W pliku zaszyfrowanym kontrolę robi `CosDocument.AllObjectsWithinDepthLimit` (po odszyfrowaniu, ustawiane przez SecurityModule jako `EncryptedVerifier`), a otwarcie idzie na stosie 1 GB. Główna ścieżka otwierania nie używa PDFsharp na zaszyfrowanych bajtach: PDFium odszyfrowuje, a analiza idzie przez `CosDocument`.
- **Parser COS** też był rekurencyjny bez limitu, a `1 [1 [1 [` czytał każdy poziom dwa razy (czas wykładniczy): limit 256 i spekulacyjne czytanie tylko liczb. `Inflate` z limitem.
- **Treść:** jedyne wejście do `ContentReader` to `PdfContentParser.Parse`, a przepięte są wszystkie miejsca (grep po `src`). Ochrona nie zależy od zgodności parserów: głębokość nie przekroczy liczby bajtów `[`, więc limit 500 tys. i stos 256 MB wykluczają przepełnienie. Limit czasu 60 s (wątek porzucany). Ścisły skaner (osobne śledzenie tablic i słowników, niepasujące zamknięcie to odrzucenie) i iteracyjna kontrola głębokości wyniku przed kodem rekurencyjnym są dodatkowe.
- **Dekompresja:** `PdfStreamLimits.Decode` przerywa w trakcie rozpakowywania. `PageContent` (OCR, redakcja, PDF/A, Edytuj) i `PdfIo` (Strony) mają wspólny limit 256 MB; metadane i wartości pól 32 MB. `PdfIo.StreamData` nie zamienia przekroczenia limitu w pustą treść, bo zapis oznaczałby utratę danych. Pozostałe filtry mają ograniczenie przez stosunek rozmiarów (LZW 4096:1, RL 128:1).
- **AutoTag:** modele stron nie trzymają bajtów ani tokenów, tylko przesunięcia. Budżety dokumentu: 1 GB odczytanej treści (znany rozmiar współdzielonego strumienia liczy się przed dekodowaniem), 512 MB przepisanej, 2 mln elementów modeli. Ponad budżet strona jest pomijana i wymieniana.

## D-43. Panel Warstwy
- **PDFium:** wśród 458 eksportów `pdfium.dll` 157 nie ma funkcji przełączających warstwy, więc widok renderuje kopię bajtów ze zmienionym `/OCProperties /D` (`/ON`, `/OFF`), tworzoną w tle przez PDFsharp. Kopia trafia tylko do `IRenderDocument` widoku. Sesja, historia i `IsModified` zostają bez zmian (test). Stan trzymany jest w widoku po indeksie w `/OCGs`, bo PDFsharp przy zapisie zmienia numery obiektów.
- **Zachowanie PDFium sprawdzone testem:** `/Usage /View /ViewState` warstwy ma pierwszeństwo przed `/ON` i `/OFF`, także bez `/AS`. Panel czyta stan tak samo, a zapis ustawia istniejące `/ViewState` na wybrany stan i usuwa `/AS`.
- **Zapis:** „Zapisz widoczność w dokumencie” to `DelegateDocumentCommand`, z Cofnij/Ponów. „Przywróć stan z dokumentu” czyści wybór widoku.
- **Miniatury:** te z panelu idą przez render widoku, więc pokazują stan widoku. **Druk:** otwiera `session.Current`, czyli stan zapisany w dokumencie, i panel o tym informuje; to świadome, żeby tymczasowy stan widoku nie wpływał cicho na wydruk.
- **Plik niezaufany:** `/Order` czytany iteracyjnie, każda tablica odwiedzana raz (cykl i wielokrotne współdzielenie), głębokość 32, 10 tys. węzłów z informacją o obcięciu, anulowanie w pętli.


## D-44. Sonda pliku w osobnym procesie (zastępuje skan jako obronę z D-42)
- **Dlaczego:** każdy skan wstępny zgaduje, co zobaczy parser (przegląd bezpieczeństwa: filtr po podciągu, szyfrowanie po podciągu i przepuszczanie przy niepewności). Skan z D-42 został uproszczony do `PdfStructureGuard.RejectObviousDepth`: tylko odrzuca oczywiste przypadki, nic nie stwierdza. Usunięte: `EncryptedVerifier`, „niesprawdzalne strumienie”, rozpoznawanie filtrów i `/Encrypt` z bajtów, stos 1 GB.
- **Sonda:** `ZetPDF.exe --probe-pdf` (nowy `Program.Main`, wejście przed WPF). Hasło i bajty idą przez standardowe wejście, hasła nie ma w linii poleceń. Na wątku ze stosem 512 KB, mniejszym niż najmniejszy stos programu (wątek okna 1 MB, pula 1,5 MB, parser 256 MB), robi to co program: `PdfReader.Open` surowych bajtów (Import i Modify) z zapisem, odszyfrowanie przez PDFium i to samo na wyniku, PdfPig ze stronami (budżet 20 s na strony). Zwykły wyjątek parsera to kod 3 („bez awarii”): plik uszkodzony dalej obsługuje PDFium i komunikaty programu. Każdy inny wynik (awaria, StackOverflow, limit czasu 60 s plus 1 s na 5 MB, limit pamięci, błąd uruchomienia) to odrzucenie z komunikatem „plik uszkodzony lub zbyt złożony”.
- **Job Object:** limit pamięci procesu (1,5 GB plus 8 razy rozmiar pliku, najwyżej 8 GB), jeden proces (bez potomnych), koniec razem z obiektem, bez okna błędu. Bajty idą do sondy dopiero po przypisaniu do obiektu, a sonda wcześniej tylko czeka na wejściu.
- **Bez fail-open:** `PdfFileProbes.Current` domyślnie odrzuca wszystko (`UnconfiguredPdfProbe`), a program ustawia prawdziwą sondę na starcie. Testy dostają atrapę przez `tests\TestProbeSetup.cs`; prawdziwą sondę sprawdzają `PdfProbeProcessTests` na zbudowanym exe.
- **Gdzie działa:** `DocumentManager.OpenAsync`, przywracanie z dysku, oryginał przy odzyskiwaniu; `SourceLoader` (Scal, Wstaw z pliku, inne operacje stron), `PagesCommands.SourceAsync`, `SecurityCommands` (porównanie i hasła właściciela), Kreator działań, `CombineFilesAsync` (Połącz pliki, Z pliku), indeks pełnotekstowy, `OwnerProtectionGuard`. Po wpisaniu hasła plik jest sprawdzany ponownie z hasłem. **Bez sondy:** wyniki własnych operacji (`CreateNewAsync`), kopie odzyskiwania pod DPAPI, wynik Office i Edge (powstaje lokalnie), druk wsadowy (tylko PDFium), weryfikacja podpisu aktywnego pliku (parser COS z limitem 256 poziomów).
- **Pomiary na tym komputerze:** start i sprawdzenie małego pliku w exe single-file około 380 ms (kolejne uruchomienia), w buildzie zwykłym około 265 ms; awaria na pliku głębokim około 300–780 ms. Indeks pełnotekstowy zwalnia więc o około 0,4 s na plik.
- **Sprawdzone uruchomieniem exe single-file (`scratch\publish`):** plik głęboki w strumieniu obiektów (3 tys., 100 tys., 1 mln poziomów, tablice i słowniki) odrzucony; prawdziwy AES-256 ze strumieniami obiektów (PyMuPDF) przepuszczony; `--diag-smoke` na zwykłym pliku: WYNIK OK; `--diag-smoke` na pliku głębokim: okno błędu, program działa, kod wyjścia 0.
- **Dekodowanie strumieni:** `PdfStreamLimits.Decode` dekoduje łańcuch sam (Flate, LZW, ASCII85, ASCIIHex, RunLength), z limitem narastającym w trakcie każdego filtra; nazwy ze sparsowanego słownika, po rozwinięciu referencji i `#xx`. Łańcuch ponad 4 filtry, nieobsługiwany filtr i dane nieczytelne to `PdfStreamUnreadableException`, bez powrotu do `UnfilteredValue`. Predyktor przez `Filtering.Decode` dopiero po przejściu limitu. AutoTag zgłasza takie strony w raporcie, a `PdfIo` nie zamienia ich w pustą treść.
- **PdfPig w programie** nie działa na dużym stosie, bo strony czyta leniwie w wielu miejscach. Odchylenie od prośby koordynatora: chroni go sonda ze stosem 512 KB, mniejszym niż wszystkie stosy programu.
