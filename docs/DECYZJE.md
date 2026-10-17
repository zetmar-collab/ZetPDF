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
1. IDocumentView.ShowAnnotations (render bez RenderFlags.Annotations, widżety zostają) – Pokaż/Ukryj komentarze nie zmienia pliku (annotations-forms + renderer-viewer).
2. Hak przed zapisem w IDocumentManager – „Spłaszcz przy zapisie” (annotations-forms).
3. AnnotationUpdate.TextStyle – zmiana czcionki FreeText.
4. IStampingService: RemovePageNumbersAsync, GetStoredOptionsAsync (pages-engine, Kreator działań).
5. Zdublowany pasek nawigacji stron (ViewerBar + pasek stanu powłoki) – zostawić jeden.
6. BouncyCastle nieużywany w ZetPDF.Security – usunąć albo uzasadnić.
7. Annotations: FillSign.Flatten po scaleniu ma używać IPageOperations.FlattenAsync z pages-engine – sprawdzić /AS widżetów.
