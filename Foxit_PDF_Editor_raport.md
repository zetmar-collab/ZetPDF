# Foxit PDF Editor 14 — analiza programu, funkcji i poleceń menu

**Lokalizacja:** `C:\Program Files (x86)\Foxit Software\Foxit PDF Editor`
**Wersja:** 14.0.7.33751 (plik `FoxitPDFEditor.exe`, ok. 148 MB)
**Data analizy:** 16.10.2026
**Rozmiar instalacji:** ok. 3,36 GB, 5491 plików

> **Skąd pochodzą dane.** Program nie był uruchamiany. Raport powstał na podstawie plików instalacji:
> - podręcznika użytkownika w języku angielskim (`Manual\en-US\…Quick Guide.pdf`, 514 stron: pełny spis treści, tabele skrótów klawiszowych, ścieżki poleceń),
> - polskiej skróconej instrukcji (`Manual\pl-PL\…Quick Guide.pdf`, 51 stron), z której pochodzą polskie nazwy kart i poleceń,
> - listy wtyczek i ich miejsc na wstążce (`plugins\DefaultPluginsList.xml`),
> - gotowych sekwencji Kreatora działań (`Sequences\pl-PL\*.sequ`), ustawień wstępnych i szablonów.
>
> Główny plik tłumaczeń interfejsu (`Lang\pl-PL\lang_pl-PL.xml`) i definicje wstążki (`auiresources\*.fx`) są zaszyfrowane, więc nie dało się z nich odczytać polskich nazw wszystkich przycisków. Tam, gdzie polska nazwa nie występuje w polskiej instrukcji, podaję własne tłumaczenie z angielskim oryginałem w nawiasie. W programie napis może brzmieć nieco inaczej.
>
> Oznaczenia: **[Pro]** działa tylko w Foxit PDF Editor Pro, **[Sub]** tylko w wydaniu subskrypcyjnym (czasem też tylko w niektórych regionach lub wersjach językowych).

---

## Spis treści

1. [Czym jest program i jak działa](#1-czym-jest-program-i-jak-działa)
2. [Architektura instalacji](#2-architektura-instalacji)
3. [Interfejs użytkownika](#3-interfejs-użytkownika)
4. [Karta Plik](#4-karta-plik)
5. [Karta Początek (Home)](#5-karta-początek-home)
6. [Karta Konwertuj (Convert)](#6-karta-konwertuj-convert)
7. [Karta Edytuj (Edit)](#7-karta-edytuj-edit)
8. [Karta Organizuj (Organize)](#8-karta-organizuj-organize)
9. [Karta Komentarz (Comment)](#9-karta-komentarz-comment)
10. [Karta Widok (View)](#10-karta-widok-view)
11. [Karta Formularz (Form)](#11-karta-formularz-form)
12. [Karta Ochrona (Protect)](#12-karta-ochrona-protect)
13. [Karta Udostępnianie (Share)](#13-karta-udostępnianie-share)
14. [Karta Ułatwienia dostępu (Accessibility)](#14-karta-ułatwienia-dostępu-accessibility)
15. [Karta Pomoc (Help)](#15-karta-pomoc-help)
16. [Karty dodatkowe i kontekstowe](#16-karty-dodatkowe-i-kontekstowe)
17. [Panele nawigacyjne](#17-panele-nawigacyjne)
18. [Preferencje (Plik > Preferencje)](#18-preferencje-plik--preferencje)
19. [Wtyczki (pliki .fpi)](#19-wtyczki-pliki-fpi)
20. [Skróty klawiszowe](#20-skróty-klawiszowe)
21. [Wiersz poleceń](#21-wiersz-poleceń)
22. [Podsumowanie](#22-podsumowanie)

---

## 1. Czym jest program i jak działa

Foxit PDF Editor to pełny edytor PDF dla Windows, konkurent Adobe Acrobat Pro. Obsługuje cały cykl życia dokumentu:

- **tworzenie** PDF z plików, skanera, schowka, stron WWW i szablonów oraz przez wirtualną drukarkę,
- **odczyt i nawigację**: wiele kart, widok równoległy, tryb odczytu, czytanie na głos, modele 3D,
- **edycję** tekstu, obrazów, kształtów, łączy, zakładek, warstw i multimediów,
- **organizację stron**: wstawianie, usuwanie, dzielenie, łączenie, kadrowanie, znaki wodne, numeracja Bates,
- **komentowanie i recenzję**: adiustacja, rysunki, pomiary, stemple, recenzje współdzielone,
- **formularze**: wypełnianie, projektowanie pól, rozpoznawanie pól, JavaScript,
- **OCR**: silnik ABBYY w `plugins\OCR\FX_ABBYY_OCR.dll`,
- **bezpieczeństwo**: hasła, certyfikaty, Microsoft Purview/MIP, redakcja, usuwanie ukrytych danych,
- **podpisy**: odręczne, cyfrowe, certyfikacja, znaczniki czasu, Foxit eSign, DocuSign,
- **dostępność**: tagowanie, kontrola WCAG/PDF-UA, kolejność odczytu,
- **AI**: Asystent AI z czatem, tłumaczeniem, streszczaniem, generowaniem zakładek i łącznikami MCP **[Sub]**.

**Model działania.** Plik `FoxitPDFEditor.exe` to rdzeń z silnikiem renderowania PDF (Foxit PDF SDK). Większość funkcji to osobne **wtyczki `.fpi`** w katalogu `plugins`, ładowane przy starcie lub na żądanie. Każda wtyczka dodaje własne przyciski do kart wstążki (zob. rozdz. 19). Interfejs to wstążka w stylu Microsoft Office. Komponenty systemowe (drukarka PDF, rozszerzenia Eksploratora, dodatki Office i przeglądarek) działają jako osobne moduły.

---

## 2. Architektura instalacji

### 2.1 Pliki wykonywalne

| Plik | Rola |
|---|---|
| `FoxitPDFEditor.exe` | Główna aplikacja (rdzeń, renderer, wstążka) |
| `Activation.exe` | Kreator aktywacji licencji (klucz lub logowanie do konta Foxit) |
| `FoxitUpdater.exe` | Aktualizator programu |
| `ProfileMgr.exe` | Menedżer profili i ustawień użytkownika |
| `TrackReview.exe` | Narzędzie śledzenia recenzji (Tracker) |
| `ScannerHelperApp.exe` | Pośrednik obsługi skanerów (TWAIN/WIA) |
| `TaggedPdfWorker.exe` | Proces roboczy do tagowania PDF (dostępność) |
| `64BitMailAgent.exe` | Agent poczty dla 64-bitowego Outlooka (wysyłanie PDF e-mailem) |
| `SendCrashReport.exe` | Wysyłanie raportów o awariach |
| `iwSessionCache.exe`, `iwKillSessionCache.exe` | Integracja z iManage Work |
| `plugins\Creator\AcqWeb.exe` | Konwersja stron WWW do PDF |
| `plugins\Creator\NativeMessagingEXE.exe` | Komunikacja z dodatkami Chrome i Firefox |
| `plugins\ATLEcmTransfer.exe` | Przesyłanie plików do systemów ECM |

### 2.2 Najważniejsze biblioteki

| Biblioteka | Rola |
|---|---|
| `FPCSDK.dll` / `FPCSDK64.dll` | Foxit PDF Creator SDK, czyli konwersja do PDF |
| `FoxitPhantomPDF(64).dll` | Warstwa zgodności (API dla innych aplikacji, np. Office) |
| `Interop.Acrobat.dll` | Zgodność z API Adobe Acrobat (automatyzacja, VBA) |
| `agnosticuilib.dll` | Biblioteka interfejsu (wstążka, okna) |
| `lucene++.dll`, `fxLuceneLib.dll` | Silnik indeksowania pełnotekstowego (Indeks, wyszukiwanie zaawansowane) |
| `mip_*.dll`, `msipc_aip.dll` | Microsoft Information Protection (etykiety poufności, szyfrowanie RMS) |
| `MAPIEmail.dll` | Wysyłanie poczty przez MAPI |
| `DocumentFormat.OpenXml.dll` | Obsługa formatów Office (DOCX/XLSX/PPTX) |
| `Sensor.dll`, `Analytics.dll` | Telemetria i statystyki użycia |
| `libxml2`, `libgsf`, `glib`, `zlib`, `libcrypto-3` | Biblioteki pomocnicze (XML, archiwa, kryptografia) |
| `WebView2Loader.dll`, `Microsoft.Web.WebView2.*` | Wbudowana przeglądarka Edge WebView2 (strona startowa, panele HTML) |
| `iw*.dll`, `iManage*.dll` | Integracja z iManage Work |
| `MSAL\`, `FMsalCom.dll`, `ADALComBridge.dll` | Logowanie Microsoft (Entra ID / Azure AD) |

### 2.3 Katalogi

| Katalog | Zawartość |
|---|---|
| `plugins\` | Ok. 70 wtyczek `.fpi` i podkatalogi: `OCR` (ABBYY), `Creator` (drukarka PDF, dodatki przeglądarek), `PDF3D`, `PDFA` (walidacja PDF/A), `JAWSScripts` (czytnik ekranu JAWS) |
| `Lang\` | 17 pakietów językowych, w tym `pl-PL` (pliki zaszyfrowane `.fx` i jawne `*lang_pl-PL.xml` wtyczek) |
| `Manual\` | Instrukcje PDF (EN: pełny podręcznik, PL: skrócona) |
| `auiresources\` | Ikony i zasoby wstążki (zaszyfrowane `.fx` i PNG, także w trybie ciemnym) |
| `Sequences\` | Gotowe działania Kreatora działań (6 sekwencji, po polsku) |
| `stamps\` | Stemple: standardowe, dynamiczne, „Podpisz tutaj” i szablony kształtów |
| `TickMark\` | Zestawy znaczników księgowych: Litery, Liczby, Symbole |
| `Redaction\` | Profile redakcji: U.S. FOIA, U.S. Privacy Act |
| `Setting\` | Ustawienia konwersji: Standard, Wysoka jakość druku, Najmniejszy rozmiar, PDF/A-1b, PDF/UA-1, USPTO |
| `PDF Optimizer\` | Profil optymalizacji `Mobile.optimize` |
| `CompareTamplateFile\` | Szablony raportu porównania dokumentów |
| `coversheet\` | Strony tytułowe portfolio i recenzji |
| `icc\` | Profile kolorów ICC (preflight, konwersja kolorów) |
| `Javascripts\` | Miejsce na skrypty JS ładowane przy starcie (puste) |
| `Shell Extensions\` | Podgląd i miniatury PDF w Eksploratorze Windows |
| `Start\`, `commonHtml\`, `WebView\` | Strona startowa (HTML) |
| `skins\` | Skórki (jasna, ciemna, kolorowa) |
| `V8\` | Silnik JavaScript V8 (JavaScript w PDF) |
| `Tracker\`, `ActivePage\`, `lex\` | Śledzenie recenzji, strony aktywacji, słowniki do sprawdzania pisowni |

---

## 3. Interfejs użytkownika

- **Wstążka (Ribbon):** polecenia pogrupowane na kartach (rozdz. 4–16). `Ctrl+F11` zwija wstążkę.
- **Pasek szybkiego dostępu:** konfigurowalne ikony nad wstążką. Prawy klik na wstążce otwiera *Dostosuj pasek szybkiego dostępu*, a tam zakładka *Klawiatura* pozwala zmieniać skróty.
- **Pole Wyszukaj (`Alt+Q`)** na pasku tytułu: wyszukuje polecenia programu po nazwie, otwiera *Wyszukaj i zastąp* w dokumencie (`Ctrl+F`), *Wyszukiwanie zaawansowane* (`Ctrl+Shift+F`) oraz *Wyszukaj i wyróżnij*.
- **Panel dokumentu:** wiele kart (każdy PDF w osobnej karcie). Kartę można przeciągnąć poza okno, żeby otworzyć ją w osobnym oknie.
- **Panel nawigacyjny (lewy, `F4`):** Zakładki, Strony, Komentarze, Załączniki, Warstwy, Podpisy, Znaczniki i inne (rozdz. 17).
- **Prawy panel / karta Format:** właściwości zaznaczonego obiektu, komentarza lub pola (czcionka, kolor, krycie, wyrównanie). Pojawia się kontekstowo.
- **Pasek stanu:** nawigacja po stronach (pierwsza, poprzednia, następna, ostatnia, numer strony), układ stron, powiększenie.
- **Strona startowa:** ostatnie dokumenty, Kreator narzędzi, samouczki, Foxit DMS / Dokumenty w chmurze, Współpraca nad PDF.
- **Skórki:** Plik > Skórki: Jasny, Ciemny, Użyj ustawienia systemowego, a w trybie jasnym wariant Kolorowy.
- **Tryb myszy / dotyku:** układ przycisków dostosowany do ekranów dotykowych.
- **Pływający pasek narzędzi:** po zaznaczeniu tekstu podsuwa szybkie akcje (wyróżnij, skopiuj, Asystent AI).

---

## 4. Karta Plik

Menu „backstage” z podstawowymi operacjami na pliku i ustawieniami programu.

| Polecenie | Co robi |
|---|---|
| **Otwórz** (`Ctrl+O`) | Otwiera PDF z komputera, Foxit DMS, SharePoint, OneDrive, Dysku Google, Dropbox, Box, Egnyte i systemów ECM. *Dodaj miejsce* rejestruje nową usługę chmurową lub ECM. |
| **Utwórz** | Nowy PDF: *Z pliku*, *Z wielu plików*, *Połącz pliki*, *Pusty*, *Ze schowka*, *Ze strony WWW*, *Ze skanera*, *Z szablonu*, *Formularz z pliku*, *Portfolio PDF*, *Segregator* **[Pro]** |
| **Zapisz** (`Ctrl+S`) | Zapisuje bieżący dokument |
| **Zapisz jako** (`Ctrl+Shift+S`) | Zapis pod nową nazwą, w nowym miejscu (komputer, Foxit DMS, chmura, ECM) lub formacie: PDF/A, PDF/E, PDF/X, Word, Excel, PowerPoint, HTML, RTF, TXT, XPS, obrazy |
| **Przywróć** (Revert) | Odrzuca niezapisane zmiany i wczytuje ostatnio zapisaną wersję |
| **Zamknij** (`Ctrl+W` / `Ctrl+F4`), **Zamknij wszystko** (`Ctrl+Shift+W`) | Zamyka bieżący lub wszystkie dokumenty |
| **Eksportuj** | Eksport do Office, obrazów, HTML, XML, XPS, tekstu dostępnego |
| **Drukuj** (`Ctrl+P`) | Okno drukowania: zakres, skalowanie, wiele stron na arkuszu, broszura, drukowanie komentarzy i formularzy, fragmentu strony. Także **Drukowanie wsadowe** (Batch Print) |
| **Udostępnij** | *E-mail*, *Wyślij do recenzji e-mail*, *Wyślij do recenzji udostępnionej*, *Narzędzie śledzenia* (Tracker) |
| **Właściwości** (`Ctrl+D`) | Właściwości dokumentu: *Opis* (tytuł, autor, temat, słowa kluczowe, metadane XMP), *Widok początkowy* (układ, powiększenie, opcje okna i interfejsu przy otwarciu), *Zabezpieczenia*, *Czcionki* (osadzone i użyte), *Zaawansowane* (język, opcje druku, linki bazowe) |
| **Kreator działań** (Action Wizard) **[Pro]** | Tworzenie, uruchamianie i zarządzanie działaniami, czyli sekwencjami operacji wykonywanymi na wielu plikach. Także *Polecenia niestandardowe* (Custom Commands). Szczegóły niżej. |
| **Optymalizator PDF** | *Zmniejsz rozmiar pliku* (szybka kompresja i podniesienie wersji PDF), *Optymalizacja zaawansowana* (kompresja obrazów, wyłączenie osadzania czcionek, przezroczystość, usuwanie obiektów i danych użytkownika, czyszczenie struktury), *Optymalizuj zeskanowany PDF* |
| **Indeks** (`Ctrl+Shift+M`) | *Zarządzaj indeksem osadzonym* (indeks wbudowany w PDF przyspiesza wyszukiwanie) oraz *Indeks pełnotekstowy z katalogiem* (indeks wielu PDF w folderze, aktualizacja według harmonogramu) |
| **Preferencje** (`Ctrl+K`) | Wszystkie ustawienia programu (rozdz. 18) |
| **Skórki** | Wybór motywu interfejsu |
| **Opcje > Dostosuj wstążkę** | Dodawanie, usuwanie i przestawianie kart, grup i przycisków. Import i eksport układu |
| **Zakończ** (`Ctrl+Q` / `Alt+F4`) | Zamyka program |

### Gotowe działania Kreatora działań (`Sequences\pl-PL`)

| Działanie | Co wykonuje |
|---|---|
| **Przygotuj do dystrybucji** | Usuwa ukryte informacje (komentarze, załączniki, metadane), dodaje znak wodny, nagłówek i stopkę |
| **Udostępnij** | Prowadzi krok po kroku przez ułatwienia dostępu: tytuł, OCR, tagowanie, język, tekst alternatywny, sprawdzenie |
| **Udostępnij (PDF/UA-1)** | Sprawdza i poprawia zgodność z PDF/UA-1 |
| **Publikuj informacje poufne** | Redakcja (zaznacz i zastosuj), usunięcie ukrytych danych, ochrona hasłem |
| **Zoptymalizuj dla Internetu i urządzeń mobilnych** | Konwersja kolorów do sRGB (preflight), kompresja obrazów, osadzenie czcionek, szybki podgląd w sieci |
| **Optymalizuj skanowane dokumenty** | OCR, prostowanie i obracanie stron, zmniejszenie rozmiaru |
| **Archiwizuj dokumenty** | Uzupełnienie metadanych i konwersja do PDF/A-1b |

---

## 5. Karta Początek (Home)

Najczęściej używane polecenia z różnych kart zebrane w jednym miejscu.

| Polecenie | Co robi |
|---|---|
| **Ręka** (Hand, `Alt+3`, klawisz `H`) | Przewijanie stron, klikanie łączy, wypełnianie formularzy, odtwarzanie multimediów |
| **Zaznacz tekst i obraz** (`Alt+6`, `V`) | Zaznaczanie tekstu lub obrazu do skopiowania, wyróżnienia albo utworzenia zakładki |
| **Wybierz adnotację** (`R`) | Zaznaczanie, przesuwanie, zmiana rozmiaru i wyrównywanie komentarzy oraz pól |
| **Migawka** (Snapshot, `Alt+7`) | Kopiuje zaznaczony obszar strony do schowka jako obraz |
| **Powiększ** (Zoom) | *Powiększ/Pomniejsz* (`Ctrl++` / `Ctrl+-`), wartości procentowe, *Powiększ do…* (`Ctrl+M`) |
| **Opcja dopasowania do strony** | *Dopasuj stronę* (`Ctrl+0`), *Dopasuj szerokość* (`Ctrl+2`), *Dopasuj widoczne* (`Ctrl+3`), *Rzeczywisty rozmiar* (`Ctrl+1`) |
| **Zmiana układu** (Reflow, `Ctrl+4`) | Pokazuje tekst jako jedną kolumnę dopasowaną do szerokości okna, bez przewijania w poziomie |
| **Obróć widok** | Obraca widok w prawo lub w lewo o 90° (`Ctrl+Shift++` / `Ctrl+Shift+-`). Sam plik się nie zmienia |
| **Edytuj tekst** / **Edytuj obiekt** | Skróty do edycji treści (rozdz. 7) |
| **Maszyna do pisania**, **Wyróżnij** i inne | Skróty do komentarzy (rozdz. 9) |
| **Wstaw** (Insert) | Wstawianie stron: *Z pliku*, *Z otwartych plików*, *Pusta strona* i inne |
| **Zakładka** | Dodaje zakładkę (`Ctrl+B`) |
| **Ze skanera / Skanuj** | Skanowanie do PDF z własnymi ustawieniami lub według ustawienia wstępnego. *Zarządzaj ustawieniami wstępnymi* |
| **Szybkie rozpoznawanie** (Quick Recognition) | OCR wszystkich stron jednym kliknięciem z ustawieniami domyślnymi |
| **Konwertuj** | Skróty do eksportu (Word, Excel, obrazy) |
| **Wypełnij i podpisz** | Otwiera kartę kontekstową *Wypełnij i podpisz* (rozdz. 16) |
| **Asystent AI** **[Sub]** | Otwiera kartę i panel Asystenta AI |
| **Asystent tłumaczenia** (TranslateAssistant) **[Sub]** | Tłumaczenie dokumentu |
| **Kup teraz** | Zakup licencji (w wersji próbnej) |

---

## 6. Karta Konwertuj (Convert)

Tworzenie PDF, eksport do innych formatów, OCR i zgodność ze standardami.

### Tworzenie

| Polecenie | Co robi |
|---|---|
| **Z plików > Z pliku** (`Ctrl+N`) | Konwertuje pojedynczy plik (Office, obraz, TXT, HTML i inne) do PDF |
| **Z plików > Z wielu plików** | Konwersja wsadowa wielu plików do osobnych PDF |
| **Połącz pliki** | Łączy wiele plików różnych typów w jeden PDF (kolejność, zakresy stron, zakładki) |
| **Pusty** | Tworzy nowy pusty PDF (rozmiar i orientacja strony) |
| **Ze schowka** | Tworzy PDF z zawartości schowka (tekst lub obraz) |
| **Ze strony WWW** | Konwertuje stronę WWW lub całą witrynę (poziomy, limity czasu, hiperłącza) |
| **Ze skanera** | *Skanuj*, ustawienia wstępne, *Zarządzaj ustawieniami wstępnymi* |
| **Z szablonu** | Tworzy PDF z gotowego szablonu (tylko niektóre wersje językowe) |
| **Formularz > Z pliku** | Tworzy formularz z istniejącego pliku z automatycznym rozpoznaniem pól |
| **Portfolio PDF** | *Utwórz nowe portfolio PDF* (z plików, folderów, skanera, WWW, schowka) lub *Utwórz puste portfolio PDF* |
| **Segregator** (Binder) **[Pro]** | Zbiór dokumentów w jednym PDF z automatycznym spisem treści. Zapis jako projekt lub szablon |

### Eksport

| Polecenie | Co robi |
|---|---|
| **Do pakietu MS Office** | Word (DOCX, DOC), Excel (XLSX, XLS), PowerPoint (PPTX) |
| **Do obrazu** | JPEG, JPEG2000, PNG, TIFF, BMP (każda strona osobno) |
| **Do HTML** | Strona HTML |
| **Do innego** | RTF, TXT, tekst dostępny, XML 1.0, XPS |
| **Eksportuj wszystkie obrazy** | Zapisuje wszystkie obrazy z PDF jako osobne pliki |
| **Eksport zaznaczonego obszaru** | Zaznaczony fragment do Word, Excel, PowerPoint lub obrazu |

### OCR

| Polecenie | Co robi |
|---|---|
| **Rozpoznaj tekst** | OCR: *Bieżący plik*, *Wiele plików*, *Zaznaczony obszar* **[Pro]**. Wybór języka i trybu wyjścia (edytowalny tekst lub obraz z ukrytą warstwą tekstu) |
| **Szybkie rozpoznawanie** | OCR jednym kliknięciem |
| **Podejrzane obiekty** (Suspect Results) | *Pierwszy podejrzany obiekt* (przegląd niepewnych fragmentów OCR jeden po drugim), *Wszystkie podejrzane obiekty* (poprawianie wielu naraz). Przyciski: *Nie tekst*, *Zaakceptuj i znajdź*, *Znajdź następne* |

### Standardy

| Polecenie | Co robi |
|---|---|
| **Wstępne sprawdzanie** (Preflight) | Sprawdza zgodność z PDF/A, PDF/E, PDF/X, PDF/UA, PDF/VT i przydatność do druku. Karty *Profile* (Analizuj / Analizuj i napraw), *Wyniki* (raport PDF/XML/TXT/HTML), *Standardy* (konwersja do PDF/A, E, X **[Pro]**) |
| **Konwersja do skali szarości** **[Pro] [Sub]** | Zamienia kolorowy PDF na odcienie szarości |

---

## 7. Karta Edytuj (Edit)

Edycja treści stron i elementów interaktywnych.

### Edycja treści

| Polecenie | Co robi |
|---|---|
| **Edytuj tekst** (`T`) | Edycja akapitów jak w edytorze tekstu: automatyczne zawijanie, styl tekstu i akapitu na karcie Format |
| **Edytuj obiekt** (`O`) | *Wszystko / Tekst / Obraz / Kształt i cieniowanie*: przesuwanie, zmiana rozmiaru, obracanie, przerzucanie, ścinanie, przycinanie, rozmieszczanie, usuwanie. Obraz można otworzyć w zewnętrznym programie (*Edytuj za pomocą*) |
| **Edycja zmiany układu** (Reflow Editing) **[Pro]** | Edycja dokumentu w trybie ciągłym, z przepływem tekstu między stronami |
| **Dodaj tekst** | Nowy obiekt tekstowy |
| **Dodaj obrazy** | *Obraz z pliku* lub *Narysuj nowy obraz* **[Pro]** |
| **Dodaj kształty** **[Pro]** | Ścieżki (linie, krzywe, prostokąty) i obiekty cieniowania (gradienty) |
| **Połącz i scal tekst** (Link & Join Text) | Łączenie bloków tekstu, żeby przepływał między nimi, oraz scalanie i dzielenie obiektów tekstowych |
| **Edytor obrazów** **[Pro]** | Narzędzia zaznaczania i malowania, filtry, przerzucanie i zastępowanie obrazu |
| **Wyszukaj i zamień** (`Ctrl+T`) | Zamiana tekstu w całym dokumencie |
| **Sprawdź pisownię** | Sprawdzanie pisowni w komentarzach, polach i edytowanym tekście (słowniki w `lex\`) |
| **Dodaj 3D** **[Pro]** | Wstawia model 3D (U3D, PRC, glTF) na stronę |

### Łącza i nawigacja

| Polecenie | Co robi |
|---|---|
| **Łącze** (Link, `L`) | Prostokątny lub dowolny obszar z akcją: przejście do strony lub widoku, otwarcie pliku lub URL, JavaScript, odtworzenie multimediów i inne |
| **Łącza internetowe** | *Utwórz łącza z adresów URL*, *Usuń wszystkie łącza*, *Dołącz wszystkie łącza na stronie*, *Pokaż łącza internetowe* |
| **Odsyłacz** (Cross Reference, `Ctrl+Shift+C`) | Tworzy łącza wewnętrzne do miejsc w dokumencie, np. „zob. rozdz. 3” |
| **Zakładka** (`Ctrl+B`) | Dodaje zakładkę do bieżącego widoku |
| **Automatyczne tworzenie zakładek** **[Pro]** | Generuje zakładki ze stylów tekstu (nagłówków) lub szukanych fraz |
| **Pole artykułu** (Add Article Box) | Definiuje wątki artykułów (kolejność czytania kolumn tekstu) |

### Multimedia i załączniki

| Polecenie | Co robi |
|---|---|
| **Załącznik pliku** | Dołącza plik do całego dokumentu lub jako komentarz na stronie |
| **Adnotacja obrazu** | Obraz jako adnotacja |
| **Wideo i audio** (`M`) | Osadza plik multimedialny z ustawieniami odtwarzania i akcjami |

---

## 8. Karta Organizuj (Organize)

Operacje na stronach i oznaczenia stron. Większość działa też z menu kontekstowego miniatur w panelu Strony, a miniatury można przeciągać między dokumentami.

### Wstawianie i usuwanie

| Polecenie | Co robi |
|---|---|
| **Wstaw** | *Z pliku* (`Ctrl+Shift+I`), *Z folderu*, *Z otwartych plików*, *Ze skanera*, *Ze schowka*, *Ze strony WWW*, *Pusta strona* (`Ctrl+Shift+T`), *Do plików* (wstawia strony do wielu innych PDF). Opcja *Nazwa pliku jako etykieta strony* **[Sub]** |
| **Usuń** (`Ctrl+Shift+D`) | Usuwa wybrane strony (także puste) |
| **Wyodrębnij** | Zapisuje wybrane strony jako nowy PDF lub osobne pliki (opcjonalnie usuwa je z oryginału) |
| **Zastąp** | Zastępuje strony stronami z innego pliku |
| **Duplikuj** (`Ctrl+Alt+D`) | Kopiuje strony w obrębie dokumentu |
| **Przenieś** | Przenosi strony w inne miejsce |
| **Zamień** (Swap) | Zamienia miejscami dwie strony |
| **Przeplatanie** (Interleaving) | Łączy dwa pliki naprzemiennie strona po stronie, np. skany stron parzystych i nieparzystych |
| **Odwróć** (Reverse) | Odwraca kolejność stron |
| **Zmień kolejność** (Rearrange) | Ustawia strony według kolejności zakładek |

### Podział i przekształcenia

| Polecenie | Co robi |
|---|---|
| **Podziel > Podziel dokument** | Dzieli PDF na wiele plików (według liczby stron, rozmiaru pliku albo zakładek najwyższego poziomu) |
| **Podziel > Podziel strony** | Dzieli jedną stronę na kilka (np. skan rozkładówki) |
| **Obróć strony** | W lewo lub w prawo; *Obracanie wielu stron* z wyborem zakresu (strony parzyste, nieparzyste, poziome, pionowe) |
| **Kadruj strony** (`C`) | Przycina marginesy: ramka przycięcia, spadu i inne, pomijanie białych marginesów |
| **Zmień rozmiar stron** | *Bieżący plik* / *Wiele plików*: zmiana formatu strony ze skalowaniem treści |
| **Spłaszcz** (Flatten) | Wtapia komentarze i pola formularzy w treść strony |

### Oznaczenia stron

| Polecenie | Co robi |
|---|---|
| **Znak wodny** | *Dodaj / Aktualizuj / Usuń wszystkie*: tekst, obraz lub PDF jako znak wodny (krycie, obrót, zakres stron) |
| **Tło** | *Dodaj / Aktualizuj / Usuń wszystkie*: kolor lub obraz w tle strony |
| **Nagłówek i stopka** | *Dodaj / Aktualizuj / Usuń wszystkie*: tekst, data, numer strony, marginesy |
| **Numeracja Bates** **[Pro]** | *Dodaj / Usuń wszystkie*: kolejne numery identyfikacyjne na stronach wielu dokumentów (dokumenty prawne) |
| **Format numerów stron** (Format Page Numbers) | Etykiety stron: style numeracji (i, ii… / 1, 2…), prefiksy, sekcje |

---

## 9. Karta Komentarz (Comment)

Adnotacje, recenzja, pomiary i stemple. Wygląd zaznaczonego komentarza zmienia się na karcie **Format** w prawym panelu (skróty `Shift+1…0` wybierają kolory, `Shift+L` przełącza kolor).

### Adiustacja tekstu

| Polecenie | Skrót | Co robi |
|---|---|---|
| **Wyróżnij** | `Shift+H` | Zakreśla tekst |
| **Wyróżnienie obszaru** | — | Zakreśla prostokątny obszar (np. skan) |
| **Podkreśl** | `Ctrl+U` | Podkreślenie |
| **Podkreślenie falowane** | `Shift+U` | Falista linia |
| **Przekreśl** | `Shift+S` | Przekreślenie |
| **Wstaw tekst** | `Shift+I` | Znak wstawienia z propozycją tekstu |
| **Zastąp tekst** | `Ctrl+Shift+R` | Przekreśla i proponuje zamiennik |

### Notatki i tekst

| Polecenie | Co robi |
|---|---|
| **Notatka** (`Ctrl+Shift+N`, `S`) | Ikona notatki z wyskakującym oknem |
| **Plik** (pinezka) | Załącza plik jako komentarz |
| **Maszyna do pisania** (`Shift+T`) | Tekst wpisywany bezpośrednio na stronie (np. w formularzach nieinteraktywnych) |
| **Pole tekstowe** (`Shift+E`, `X`) | Tekst w ramce |
| **Objaśnienie** (Callout, `Shift+C`) | Pole tekstowe ze strzałką wskazującą miejsce |

### Rysowanie

| Polecenie | Co robi |
|---|---|
| **Rysunek** | *Prostokąt, Owal, Wielokąt, Linia łamana, Linia, Strzałka, Łuk, Chmura* (`Q`) |
| **Ołówek** (`Shift+P`) | Rysowanie odręczne (obsługa nacisku pióra PSI) |
| **Gumka** | Wymazuje fragmenty rysunków ołówkiem |
| **Grupuj** | Łączy kilka znaczników w grupę |

### Pomiary

| Polecenie | Co robi |
|---|---|
| **Mierz** | *Odległość, Obwód, Pole wielokąta, Pole koła, Kąt, Promień, Średnica, Długość linii łamanej, Objętość*. Ustawianie skali i kalibracji strony, eksport pomiarów |
| **Kalkulator księgowy** **[Pro]** | Taśma kalkulatora: sumuje liczby zaznaczone w dokumencie i wstawia wynik |
| **Znaczniki (Tickmarks)** **[Pro]** | Symbole księgowe (✓, litery, liczby) do oznaczania sprawdzonych pozycji; zestawy w `TickMark\` |
| **Liczby > Liczba niestandardowa** | Kolejne numery jako adnotacje |

### Stemple

| Polecenie | Co robi |
|---|---|
| **Stempel** (`K`) | Wstawia stempel standardowy (Zatwierdzono, Poufne…), dynamiczny (z datą i użytkownikiem) lub „Podpisz tutaj” |
| **Stempel niestandardowy** | *Utwórz stempel niestandardowy* (z obrazu lub PDF), *Utwórz stempel dynamiczny*, *Utwórz stempel sekwencyjny* (numerowany), *Wklej obraz ze schowka jako stempel*, *Zarządzaj stemplami* |
| **Ulubione stemple / Przybornik ulubionych** | Zapis własnych adnotacji jako szablonów. Zarządzanie przybornikami **[Sub]** |

### Zarządzanie komentarzami

| Polecenie | Co robi |
|---|---|
| **Importuj komentarze** | Z plików FDF, XFDF lub innego PDF |
| **Eksportuj** | *Wszystkie komentarze* (`Ctrl+L`), *Zaznaczone komentarze*, *Wyróżniony tekst* (`Ctrl+I`), *Eksportuj do programu Word* (przenosi poprawki do oryginalnego DOCX) |
| **FDF przez e-mail** | Wysyła komentarze jako plik FDF |
| **Podsumuj komentarze** | Raport *PDF* (układ: komentarze obok strony, z liniami i inne) lub *CSV* |
| **Wyszukaj i wyróżnij** | Wyszukuje frazy lub wzorce (e-maile, telefony, daty) i wyróżnia je jednym kliknięciem |
| **Pokaż / Ukryj wszystkie komentarze** | `Ctrl+8` / `Ctrl+Shift+8` |
| **Usuń wszystkie komentarze** | Usuwa wszystkie adnotacje |
| **Wklej w miejscu** (`Ctrl+Alt+V`) | Wkleja komentarz w tym samym miejscu na innej stronie |
| **Tryb komentowania** (AnnotationMode) | Przełącza widok komentarzy |
| Menu kontekstowe komentarza | Właściwości, odpowiedź, ustaw stan (Zaakceptowano, Odrzucono, Anulowano, Ukończono), znacznik wyboru, wyrównanie, środkowanie, rozkład |

---

## 10. Karta Widok (View)

| Polecenie | Co robi |
|---|---|
| **Układ stron** | *Jedna strona, Ciągłe, Sąsiadująco, Ciągłe sąsiadująco, Podziel, Oddzielna strona tytułowa*, Automatyczny |
| **Tryb odczytu** (`Ctrl+H`) | Ukrywa wstążkę i panele |
| **Pełny ekran** (`F11`) | Prezentacja na pełnym ekranie (przejścia stron według Preferencji) |
| **Odwróć widok** (Reverse View) | Wyświetla strony od końca |
| **Przeglądarka tekstu** (`Ctrl+6`) | Pokazuje sam tekst dokumentu |
| **Zmiana układu** (`Ctrl+4`) | Widok jednokolumnowy |
| **Tryb wizualny** | Zmiana kolorów tła i tekstu dla wygody czytania (np. tryb nocny) |
| **Obróć widok** | Obrót widoku stron |
| **Automatyczne przewijanie** (`Ctrl+Shift+H`, stop: `Esc`) | Przewija dokument w stałym tempie |
| **Asystent** | *Ruchome obramowanie* (Marquee, powiększenie zaznaczonego obszaru, `Alt+5`), *Lupa* (Loupe, okno z powiększeniem fragmentu), *Szkło powiększające* (Magnifier) |
| **Odczyt (Czytanie na głos)** | *Aktywuj czytanie* (`Ctrl+Shift+Y`), *Przeczytaj bieżącą stronę* (`Ctrl+Shift+V`), *Przeczytaj od bieżącej strony* (`Ctrl+Shift+B`), *Pauza* (`Ctrl+Shift+P`), *Stop* (`Ctrl+Shift+E`), głośność i szybkość |
| **Porównaj** | Porównuje dwie wersje PDF (tekst, obrazy, grafika; bez komentarzy). Karta kontekstowa *Wyniki porównania*: następna (`Ctrl+.`) i poprzednia (`Ctrl+,`) zmiana, raport |
| **Widok równoległy** (Parallel View) | Dwa lub więcej PDF obok siebie (poziomo lub pionowo) w jednym oknie |
| **Synchronizacja widoku** | Synchroniczne przewijanie i powiększanie porównywanych dokumentów |
| **Linijki** (`Ctrl+R`), **Przełącz linijkę** (Toggle Ruler) | Linijki poziome i pionowe, prowadnice |
| **Siatka**, *Przyciągaj do siatki* | Siatka pomocnicza (ustawienia: Preferencje > Siatka układu) |
| **Grubość linii** (Line Weights, `Ctrl+5`) | Linie z grubością zdefiniowaną w PDF zamiast stałej grubości 1 px |
| **Współrzędne kursora** | Pozycja kursora na stronie |
| **Przeglądarka obiektów** (Object Browser) | Drzewo obiektów strony (tekst, obrazy, ścieżki) |
| **Liczba słów** (Word Count) | Liczba słów, znaków i stron |
| **Panele nawigacyjne** | Pokazywanie i ukrywanie paneli (`F4`), w tym *Standardy* |
| **Ustawienia widoku** | *Wyświetlanie strony*, *Strona początkowa*, pokazywanie warstw (opcjonalnej zawartości), artykułów, podpisów, pozycji nazwanych, ustawień zabezpieczeń; *Przejścia stron* |
| **Domyślne ustawienia widoku** | Zapisanie bieżącego układu jako domyślnego |
| **Pasek stanu / Pasek menu** | Pokazywanie i ukrywanie elementów interfejsu |

---

## 11. Karta Formularz (Form)

### Pola formularza (tworzenie)

| Polecenie | Co robi |
|---|---|
| **Pole tekstowe** | Pole do wpisywania tekstu (format: liczba, data, procent; walidacja; obliczenia) |
| **Pole wyboru** (Check Box) | Zaznaczenie tak/nie |
| **Przycisk opcji** (Radio Button) | Wybór jednej opcji z grupy (wspólna nazwa, różne wartości eksportu) |
| **Pole kombi** (Combo Box) | Lista rozwijana (opcjonalnie z wpisywaniem) |
| **Pole listy** (List Box) | Lista wielokrotnego wyboru |
| **Przycisk** (Push Button) | Przycisk z akcją (JavaScript, wyślij lub resetuj formularz, otwórz łącze) i wyglądem dla stanów myszy |
| **Pole podpisu** | Miejsce na podpis cyfrowy |
| **Pole obrazu** | Pole, do którego użytkownik wstawia obraz |
| **Pole daty** | Pole z wyborem daty z kalendarza |
| **Pole kodu kreskowego** | Kod 2D (PDF417, QR, DataMatrix) kodujący dane formularza |
| Klawisze `F` / `Shift+F` | Wybór lub przełączanie narzędzi pól |

### Projektowanie i dane

| Polecenie | Co robi |
|---|---|
| **Uruchom rozpoznawanie pól formularza** | Automatycznie wykrywa linie, ramki i kratki i tworzy z nich pola |
| **Asystent projektanta** (Designer Assistant) | Okienko podręczne do szybkiej zmiany nazwy i właściwości wykrytych pól |
| **Dodaj etykietkę narzędzia** (Add Tooltip) | Dodaje opisy (tooltips) polom bez opisu, co pomaga w dostępności |
| **Kolejność obliczania** | Ustala kolejność wykonywania obliczeń między polami |
| **Kolejność tabulacji** | Kolejność przechodzenia klawiszem Tab (`Shift+N` pokazuje numery) |
| **Wyróżnij pola** (`Shift+L`) | Podświetla pola do wypełnienia |
| **Szablon strony** (Page Template) | Strona-wzór, którą formularz dodaje skryptem (formularze rozszerzalne). Zmiana nazwy, zastąpienie, usunięcie |
| **Edytuj statyczny formularz XFA** | Edycja formularzy XFA |
| **Wyrównaj / Wyśrodkuj / Rozłóż / Zmień rozmiar** | Rozmieszczanie wielu pól (`L`, `R`, `T`, `B`, `H`, `V`) |
| **Ustawienia narzędzi > Zachowaj zaznaczone narzędzie** | Narzędzie pozostaje aktywne po utworzeniu pola |
| **Importuj / Eksportuj** | Dane formularza: FDF, XFDF, XML, TXT, CSV; *Eksport do pliku*, *Przez e-mail*; dołączanie do istniejącego CSV |
| **Połącz formularze w arkusz** | Zbiera dane z wielu formularzy PDF do jednego pliku CSV |
| **Generuj kod kreskowy 2D** | Kod z danymi formularza (formularze Ultraform) |
| **Resetuj formularz** | Czyści wszystkie pola |

### JavaScript

| Polecenie | Co robi |
|---|---|
| **Kod JavaScript dokumentu** | Skrypty na poziomie dokumentu (funkcje wspólne, uruchamiane przy otwarciu) |
| **Działanie dokumentu** | Skrypty na zdarzenia: zamknięcie, zapis, druk (przed i po) |
| **Konsola JavaScript** (`Ctrl+J`) | Interaktywne testowanie kodu |
| **Edytuj wszystkie skrypty JavaScript** | Edytor wszystkich skryptów (zakładek, stron, pól) |
| *ADBC / ODBC* | Obiekt JS do łączenia z bazami danych Windows ODBC |

---

## 12. Karta Ochrona (Protect)

### Zabezpieczenia dokumentu

| Polecenie | Co robi |
|---|---|
| **Zabezpiecz dokument > Ochrona hasłem** | *Hasło otwierania* (wymagane do otwarcia) i *hasło uprawnień* (blokada drukowania, kopiowania, edycji, komentowania). Wybór szyfrowania (AES 128/256) |
| **> Ochrona certyfikatem** | Szyfrowanie certyfikatami odbiorców z osobnymi uprawnieniami dla każdej osoby |
| **> Zasady zabezpieczeń** | Zapisane zestawy ustawień (metoda, hasło, uprawnienia) do wielokrotnego użycia |
| **> Usuń zabezpieczenia** | Zdejmuje ochronę (wymaga uprawnień) |
| **> Właściwości zabezpieczeń** | Podgląd ustawień ochrony pliku |
| **Szyfrowanie wsadowe** | Ochrona hasłem, certyfikatem lub MIP wielu plików naraz |

### Microsoft Purview / MIP **[Pro]**

| Polecenie | Co robi |
|---|---|
| **Ogranicz dostęp** (Restrict Access) | Szyfrowanie RMS/MIP szablonem lub z ręcznie nadanymi uprawnieniami użytkownikom |
| **Poufność** (Sensitivity) | Nadaje etykietę poufności zdefiniowaną przez administratora organizacji |
| **Ustawienia > Szablony niestandardowe** | Własne szablony uprawnień MIP (4 kroki, zasady rozszerzone) |
| **Ustawienia > Zabezpieczający znak wodny** | Profile dynamicznych znaków wodnych (np. nazwa użytkownika lub data przy otwarciu) |
| **Chroń redakcje** | Generuje PDF, w którym uprawnieni widzą pełną treść, a pozostali wersję zredagowaną |
| *Narzędzie konfiguracyjne Foxit* | Dla administratorów: zasady rozszerzone, dynamiczne odwołanie uprawnień, dzienniki inspekcji, ograniczenia drukowania |

### Redakcja **[Pro]**

| Polecenie | Co robi |
|---|---|
| **Zaznacz do redakcji** | *Tekst i obrazy* (`Shift+Y`), *Obszar*, *Strony* |
| **Wyszukaj i redaguj** | Wyszukuje frazy lub wzorce (PESEL, e-mail, numery kart) i oznacza je do redakcji. Profile w `Redaction\` (U.S. FOIA, Privacy Act) |
| **Zastosuj redakcje** | Trwale usuwa oznaczoną treść i wstawia czarne prostokąty. Tej operacji nie da się cofnąć |
| **Właściwości redakcji** | Kolor wypełnienia, tekst nakładki, kody redakcji |
| **Wybiel** (Whiteout) | Trwale usuwa treść i zastępuje ją białym tłem |
| **Inteligentna redakcja** (Smart Redact) **[Sub]** | Redakcja wspomagana AI: automatyczne wykrywanie danych wrażliwych, tryb wsadowy, profile |

### Ukryte dane

| Polecenie | Co robi |
|---|---|
| **Oczyszczanie dokumentu** (Sanitize Document) | Usuwa naraz wszystkie ukryte informacje: metadane, załączniki, skrypty, komentarze, ukryte warstwy i inne |
| **Usuń ukryte informacje** | Wybiórcze usuwanie elementów ukrytych |

### Podpisy

| Polecenie | Co robi |
|---|---|
| **Wypełnij i podpisz** | Szybki podpis odręczny (rozdz. 16) |
| **Podpisz i certyfikuj > Umieść podpis** | Rysuje pole i składa podpis cyfrowy identyfikatorem cyfrowym (PKCS#12 lub magazyn Windows) |
| **> Podpisz dokument** | Podpisanie istniejącego pola podpisu |
| **> Certyfikuj** (z podpisem widocznym lub niewidocznym) **[Pro]** | Certyfikacja autora: określa, jakie zmiany są dozwolone po podpisaniu |
| **> Zaufane certyfikaty** | Zarządzanie listą zaufanych certyfikatów |
| **Identyfikatory cyfrowe** (Digital IDs) | Tworzenie (z podpisem własnym), import i zarządzanie identyfikatorami |
| **Weryfikuj** (Validate) | Sprawdza ważność wszystkich podpisów (integralność, certyfikat, odwołanie, LTV) |
| **Dokument ze znacznikiem czasowym** | Dodaje zaufany znacznik czasu z serwera TSA |
| *Podpisz z DocuSign* | Integracja z DocuSign (wtyczka `docusign_plugin`) |

---

## 13. Karta Udostępnianie (Share)

| Polecenie | Co robi |
|---|---|
| **E-mail** | *Wyślij bieżącą kartę* (`Ctrl+Shift+L`) lub *wszystkie otwarte karty* (`Ctrl+Shift+K`) jako załączniki |
| **Evernote / OneNote** | Wysyła PDF do notatek |
| **Udostępnij w celu współpracy** **[Sub]** | Udostępnia dokument łączem lub e-mailem do komentowania w czasie rzeczywistym (Editor, Reader albo Editor Cloud w przeglądarce) |
| **Współpraca nad dokumentami PDF** **[Sub]** | Lista plików udostępnionych przez użytkownika i innym |
| **Wyślij do recenzji udostępnionej** **[Pro]** | Recenzja z komentarzami zbieranymi na serwerze wewnętrznym, w folderze sieciowym lub w SharePoint, synchronizowanymi automatycznie |
| **Zarządzaj recenzją udostępnioną** | *Sprawdź nowe komentarze*, *Opublikuj komentarze*, termin, zakończenie lub ponowienie recenzji |
| **Wyślij do recenzji e-mail** | Recenzja przez załączniki e-mail |
| **Zarządzaj recenzją e-mail** | *Scal komentarze*, *Wyślij komentarze* |
| **Narzędzie śledzenia** (Tracker) | Przegląd wszystkich recenzji: status, terminy, recenzenci, foldery |
| **SharePoint** | *Wyewidencjonuj*, *Zaewidencjonuj*, *Odrzuć wyewidencjonowanie*, *Właściwości dokumentu* |
| **Integracje ECM i chmury** | Przyciski otwierania i zapisu dla: iManage Work 9/10 (Otwórz, Zapisz, Zapisz jako wersję, Właściwości, Oś czasu), OpenText eDOCS, Documentum, Content Suite, ndOffice, Worldox, ShareFile, Epona DMSforLegal, Alfresco, OneDrive, OneDrive dla firm, Dysk Google, Dropbox, Box, Egnyte |
| **Foxit DMS** **[Sub]** | Firmowa chmura dokumentów Foxit |

---

## 14. Karta Ułatwienia dostępu (Accessibility)

| Polecenie | Co robi |
|---|---|
| **Pełne sprawdzenie** (Full Check) | Kontrola zgodności z WCAG 2.0 i PDF/UA. Wyniki w panelu *Sprawdzanie ułatwień dostępu*, gdzie z menu kontekstowego są opcje *Napraw*, *Pomiń*, *Wyjaśnij* |
| **Raport o ułatwieniach dostępu** | Raport: podsumowanie i szczegóły reguł z łączami do pomocy |
| **Automatyczne oznaczanie dokumentu** **[Pro]** | Generuje znaczniki struktury (nagłówki, akapity, tabele, listy) |
| **Automatyczne oznaczanie pól formularza** **[Pro]** | Taguje pola formularzy i tworzy raport rozpoznania |
| **Ustaw tekst alternatywny** **[Pro]** | Opisy alternatywne obrazów |
| **Kolejność odczytu** (`Ctrl+Shift+U`) **[Pro]** | Edycja tagów przez zaznaczanie obszarów i nadawanie im typów (tekst, rysunek, tabela, nagłówek), podgląd grup treści |
| **Ustaw kolejność odczytu obszaru / Zmień / Zastosuj** | Kolejność czytania wskazywana bezpośrednio na stronie |
| **Wzór na MathML** (Formula) **[Sub]** | Konwertuje otagowane wzory matematyczne do MathML |
| **Asystent konfiguracji** (Setup Assistant) | Kreator preferencji dostępności (kolory, czytnik ekranu, przewijanie) |
| **Opcje odczytu** (`Ctrl+Shift+5`) | Kolejność i tryb odczytu bieżącego dokumentu |
| Panele: *Znaczniki, Zawartość, Kolejność, Raport rozpoznania* | Ręczna edycja struktury logicznej PDF |

---

## 15. Karta Pomoc (Help)

| Polecenie | Co robi |
|---|---|
| **Instrukcja obsługi** (`F1`) | Otwiera podręcznik |
| **Centrum pomocy** | Pomoc online i **Foxit AI Chatbot** (instrukcje krok po kroku) |
| **Samouczki online** | Filmy i poradniki |
| **Opinie / Pomoc techniczna** | Zgłaszanie problemów |
| **Sprawdź aktualizacje** | Ręczna aktualizacja (dodatki krytyczne, np. FileOpen) |
| **Zarządzanie wtyczkami** (Plug-in Management) | Włączanie i wyłączanie wtyczek, instalacja dodatków |
| **Aktywuj / Dezaktywuj** | Zarządzanie licencją wieczystą (przeniesienie na inny komputer) |
| **Zarządzaj** | Urządzenia z licencją (Foxit Admin Console, subskrypcja) |
| **Informacje o programie** | Wersja i licencja |

---

## 16. Karty dodatkowe i kontekstowe

| Karta | Kiedy się pojawia | Funkcje |
|---|---|---|
| **Wypełnij i podpisz** (Fill & Sign) | Początek/Ochrona > Wypełnij i podpisz | Narzędzia dla formularzy nieinteraktywnych: tekst, ✓, ✗, kropka, linia, prostokąt; paleta podpisów odręcznych (utwórz z rysunku, pisma lub obrazu); *Zarządzaj podpisami*; *Zastosuj wszystkie podpisy* (z opcją spłaszczenia); *Zachowaj zaznaczone narzędzie* |
| **Foxit eSign** **[Sub]** | Po zalogowaniu | Prawnie wiążące e-podpisy: tworzenie podpisów, *Zażądaj podpisu* (obieg dla wielu osób), *Wyślij zbiorczo*, *Utwórz formularz online* |
| **Asystent AI** **[Sub]** | Przycisk na wstążce | *Czat AI* (panel po prawej, agent z narzędziami), umiejętności (streszczanie, tłumaczenie, przeformułowanie, analiza JS, własne), generowanie obrazów i diagramów, agentowa edycja PDF, łączniki MCP, lokalna AI (BYOM/SLM), tłumaczenie dokumentu, generowanie zakładek, wzory do MathML/LaTeX, czytanie streszczeń, *Inteligentny podcast*; *Ustawienia AI* (karta Wykorzystanie: kredyty) |
| **Format** (prawy panel) | Zaznaczenie obiektu, komentarza lub pola | Czcionka, akapit, kolor, krycie, obramowanie, wyrównanie, *Dodaj do przybornika ulubionych*, *Edytuj notatkę* (`Shift+O`) |
| **Wyniki porównania** | Po Widok > Porównaj | Nawigacja po zmianach, filtrowanie typów, raport |
| **Narzędzia 3D** | Po aktywacji modelu 3D | Obrót, przesuwanie, zoom, drzewo modelu, widoki, oświetlenie, renderowanie, przekrój, pomiary 3D, komentarze 3D |
| **Portfolio** | Po otwarciu portfolio PDF | Układ, dodawanie plików i folderów, podgląd, edycja składników |
| **Asystent artykułów naukowych** (PaperAssistant) | Wtyczka (zależnie od wydania) | Sprawdzanie dokumentu i historia |

---

## 17. Panele nawigacyjne

| Panel | Przeznaczenie |
|---|---|
| **Zakładki** | Spis treści dokumentu: dodawanie, zagnieżdżanie, przenoszenie, zakładki tagowane, import i eksport, drukowanie i usuwanie stron przez zakładki |
| **Strony** (Miniatury) | Podgląd, przeciąganie, organizacja stron, etykiety stron **[Sub]**, osadzanie miniatur |
| **Komentarze** | Lista adnotacji: filtrowanie, sortowanie, odpowiedzi, stan (`Ctrl+Enter` wysyła odpowiedź) |
| **Załączniki** | Pliki dołączone do PDF |
| **Warstwy** | Pokazywanie i ukrywanie warstw (OCG); import, scalanie i spłaszczanie warstw **[Pro]**; kolejność, właściwości |
| **Podpisy cyfrowe** | Lista podpisów, weryfikacja, wersje podpisane |
| **Miejsca docelowe** | Nazwane miejsca docelowe łączy |
| **Artykuły** | Wątki artykułów |
| **Pola formularza** | Drzewo pól i kolejność tabulacji |
| **Znaczniki / Zawartość / Kolejność** | Struktura logiczna (dostępność) |
| **Sprawdzanie ułatwień dostępu / Raport** | Wyniki kontroli dostępności |
| **Standardy** | Informacje o zgodności PDF/A, E, X |
| **Wyniki wyszukiwania** | Wyniki wyszukiwania zaawansowanego (zapis do CSV lub PDF) |
| **Ulubione stemple / Przybornik ulubionych** | Szybki dostęp do własnych adnotacji |
| **Drzewo modelu** | Części modelu 3D |

---

## 18. Preferencje (Plik > Preferencje)

| Kategoria | Co ustawia |
|---|---|
| **3D** | Renderer, wyświetlanie paska 3D, domyślne widoki |
| **Ułatwienia dostępu** | Kolory dokumentu, kolejność odczytu, fokus klawiatury |
| **Kreator działań** | Ustawienia działań |
| **Komentowanie** | Czcionka notatek, krycie, autor, automatyczne otwieranie okienek, drukowanie komentarzy |
| **Tworzenie PDF** | Ustawienia konwersji (profile z `Setting\`) |
| **Dokumenty** | Otwieranie (jedna karta lub wiele, nowe okno lub instancja), przywracanie sesji, zapisywanie, usuwanie ukrytych danych przy zamknięciu lub wysłaniu **[Sub]** |
| **Integracja ECM** | Włączanie integracji z systemami ECM |
| **Edycja** | Czcionki zastępcze, edytor obrazów, zachowanie edycji tekstu |
| **Skojarzenia plików** | Foxit jako domyślna aplikacja PDF, obsługa w przeglądarkach |
| **Formularze** | Podświetlanie pól, autouzupełnianie (Podstawowe/Zaawansowane, pamiętanie liczb), automatyczne rozpoznawanie pól, okno właściwości po utworzeniu pola |
| **Pełny ekran** | Nawigacja, przejścia, kolor tła |
| **Ogólne** | Klawisze jednoliterowe, kursor, strona startowa, pasek zadań |
| **Historia** | Lista ostatnich dokumentów |
| **Tożsamość** | Imię, organizacja, e-mail (autor komentarzy, recenzje, certyfikaty) |
| **Indeks** | Indeksowanie pełnotekstowe |
| **JavaScript** | Włączenie JS, debugger, edytor zewnętrzny |
| **Języki** | Język interfejsu (17 języków, w tym polski) |
| **Siatka układu** | Odstępy, przesunięcie, podział, kolor siatki |
| **Pomiary** | Jednostki, przyciąganie, etykiety |
| **Ustawienia MIP** **[Pro]** | Serwer i zachowanie MIP |
| **Multimedia (starsze)** | Odtwarzacze i opcje |
| **Wyświetlanie strony** | Domyślny układ i powiększenie, wygładzanie tekstu, rozdzielczość, siatka i linijki, wyświetlanie przezroczystości |
| **Podpis PDF** | Podpis odręczny (spłaszczanie przy zastosowaniu) |
| **Drukowanie** | Domyślne opcje druku |
| **Odczyt** | Kolejność odczytu, czytnik ekranu, czytanie na głos |
| **Recenzja** | Serwer recenzji, synchronizacja |
| **Wyszukiwanie** | Opcje wyszukiwania i indeksów |
| **Zabezpieczenia** | Tryb bezpiecznego odczytu, załączniki spoza PDF, tryb FIPS, Windows Information Protection |
| **Podpis** | Weryfikacja przy otwarciu, sprawdzanie odwołań, wygląd, format podpisu |
| **Mowa** | Głos, szybkość i głośność czytania |
| **Pisownia** | Słowniki i język sprawdzania |
| **Tablet** | Obsługa pióra |
| **Serwery sygnatur czasowych** | Domyślny serwer TSA |
| **Menedżer zaufania** | Białe listy URL i aplikacji, uprawnienia skryptów, automatyczna aktualizacja list zaufania (AATL, EUTL) |
| **Aktualizator** | Tryb aktualizacji automatycznych |

---

## 19. Wtyczki (pliki .fpi)

Wtyczki w `plugins\` i ich rola (na podstawie `DefaultPluginsList.xml` i nazw plików):

| Wtyczka | Funkcja | Miejsce w interfejsie |
|---|---|---|
| `ActionWizard.fpi` | Kreator działań | Plik |
| `AiAssistant.fpi` | Asystent AI | Początek, karta Asystent AI |
| `AIPLabel.fpi`, `FRMSPlgV2.fpi` | Etykiety i szyfrowanie Microsoft Purview / RMS | Ochrona |
| `Binder.fpi` | Segregator PDF | Konwertuj > Utwórz |
| `Bookmark.fpi` | Zakładki | Początek, Edytuj |
| `Calculator.fpi` | Kalkulator księgowy | Komentarz > Pomiary |
| `CommentsSummary.fpi` | Podsumowanie komentarzy | Komentarz |
| `ComparePDF.fpi` | Porównywanie PDF | Widok |
| `CrossReferenceLinks.fpi` | Odsyłacze | Edytuj > Łącza |
| `DocProcess.fpi` | Właściwości dokumentu, rozpoznawanie pól, szablony stron, etykietki, numeracja stron, artykuły, łącza WWW, przejścia | Plik, Formularz, Organizuj, Edytuj, Widok |
| `Email.fpi` | Wysyłanie e-mail | Udostępnianie |
| `FileOpen.fpi` | Otwieranie PDF chronionych systemem FileOpen DRM | — |
| `FXExport.fpi` | Eksport do Word, Excel, PPT, obrazów, HTML, RTF, TXT, XPS | Konwertuj, Plik |
| `InkSign.fpi` | Podpis odręczny | Wypełnij i podpisz |
| `LayerPanelToolPlugin.fpi` | Warstwy | Widok |
| `LoupeTool.fpi` | Lupa | Widok > Asystent |
| `NamedPosition.fpi` | Pozycje nazwane | Widok |
| `OCRRecognition.fpi` + `OCR\` (ABBYY) | OCR | Początek, Konwertuj |
| `PageEditor.fpi` | Edytuj obiekt | Edytuj |
| `touchup.fpi` | Edytuj tekst | Edytuj |
| `PageFormat.fpi` | Znak wodny, tło, nagłówek i stopka, Bates | Organizuj |
| `PageOrganizer.fpi` | Organizacja stron | Organizuj |
| `PDFAccessibility.fpi` | Ułatwienia dostępu, eksport tekstu dostępnego | Ułatwienia dostępu |
| `PDFOptimizer.fpi` | Optymalizator, spłaszczanie | Plik, Organizuj |
| `Portfolio.fpi` | Portfolio PDF | Konwertuj |
| `Ruler.fpi` | Linijka | Widok |
| `Security.fpi` | Hasła, certyfikaty, zasady | Ochrona |
| `Signature.fpi` | Podpisy cyfrowe, certyfikacja, znaczniki czasu, zaufane certyfikaty | Ochrona, Formularz |
| `Speech.fpi` | Czytanie na głos | Widok |
| `TickMark.fpi` | Znaczniki księgowe | Komentarz |
| `U3DBrowser.fpi` + `PDF3D\` | Zawartość 3D | Edytuj, kontekstowo |
| `ShareReviewPlugin.fpi` | Recenzje udostępnione i e-mail | Udostępnianie |
| `IntegrateWithSP.fpi` | SharePoint | Udostępnianie, Plik |
| `iManage9/10.fpi`, `OpenText.fpi`, `Documentum.fpi`, `ContentSuite.fpi`, `NdOffice.fpi`, `Worldox.fpi`, `ShareFile.fpi`, `DMSforLegal.fpi`, `Alfresco.fpi` | Integracje ECM | Udostępnianie, Plik |
| `OneDrivePlugin`, `OneDriveforBusiness`, `GoogleDrivePlugin`, `DropboxPlugin`, `BoxPlugin`, `EgnytePlugin` | Usługi chmurowe | Plik > Otwórz/Zapisz |
| `WIP.fpi` | Windows Information Protection | — |
| `FoxitAccountManagement.fpi`, `LicenseManager.fpi`, `Subscribe.fpi` | Konto Foxit, licencje, subskrypcja | — |
| `Updater.fpi`, `PlgDynLoader.fpi`, `PlgPltfm.fpi` | Aktualizacje, dynamiczne ładowanie wtyczek, platforma wtyczek | — |
| `Browser.fpi`, `npFoxitPDFEditorPlugin.dll`, `FoxitPDFEditorBrowserAx(64).dll` | Wyświetlanie PDF w przeglądarkach | — |
| `ConvertToPDFShellExtension_x64.dll` | „Konwertuj na PDF” w menu kontekstowym Eksploratora | Windows |
| `Creator\` (Chrome/Firefox/IE Addin, html2pdf) | Konwersja stron WWW z przeglądarki | Przeglądarki |
| `JAWSScripts\` | Skrypty dla czytnika ekranu JAWS | — |
| `PDFA\` | Walidacja PDF/A | Konwertuj > Preflight |

**Komponenty systemowe instalowane razem z programem:**

- **Foxit PDF Editor Printer:** wirtualna drukarka, która z każdej aplikacji „drukuje” do PDF,
- **Print to Evernote:** drukuje do PDF i dołącza plik do notatki Evernote,
- **dodatki MS Office** (Word, Excel, PowerPoint, Outlook, Visio **[Pro]**, Project **[Pro]**): karta „Foxit PDF” z tworzeniem PDF jednym kliknięciem, korespondencją seryjną do PDF i PDF z redakcją,
- **rozszerzenia powłoki:** miniatury i podgląd PDF w Eksploratorze oraz w Outlooku i Office.

---

## 20. Skróty klawiszowe

### Ogólne

| Skrót | Działanie |
|---|---|
| `Ctrl+O` / `Ctrl+S` / `Ctrl+Shift+S` | Otwórz / Zapisz / Zapisz jako |
| `Ctrl+W`, `Ctrl+F4` / `Ctrl+Shift+W` | Zamknij / Zamknij wszystko |
| `Ctrl+P` | Drukuj |
| `Ctrl+D` | Właściwości dokumentu |
| `Ctrl+K` | Preferencje |
| `Ctrl+Q`, `Alt+F4` | Zakończ |
| `Ctrl+Z` / `Ctrl+Shift+Z` | Cofnij / Ponów |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Następna / poprzednia karta dokumentu |
| `Ctrl+F11` | Zwiń wstążkę |
| `F4` | Pokaż/ukryj panel nawigacyjny |
| `Shift+F4` | Panel zadań |
| `F10` / `F5` / `F6` | Fokus: wstążka / dokument / przełączanie paneli |
| `Alt+Q` | Pole Wyszukaj (polecenia) |
| `F1` | Instrukcja |

### Nawigacja i widok

| Skrót | Działanie |
|---|---|
| `Ctrl+G` | Przejdź do strony |
| `Ctrl+Home` / `End` | Pierwsza / ostatnia strona |
| `Ctrl+PgUp` / `Ctrl+PgDn` | Poprzednia / następna strona |
| `Alt+←` / `Alt+→` | Poprzedni / następny widok |
| `Ctrl+1/0/2/3` | Rzeczywisty rozmiar / Dopasuj stronę / szerokość / widoczne |
| `Ctrl+4` | Zmiana układu (Reflow) |
| `Ctrl+5` | Grubość linii |
| `Ctrl+6` | Przeglądarka tekstu |
| `Ctrl+M` | Powiększ do… |
| `Ctrl+H` | Tryb odczytu |
| `F11` | Pełny ekran |
| `Ctrl+R` | Linijki |
| `Ctrl+Shift+H` | Automatyczne przewijanie |
| `Ctrl+F` / `F3` / `Shift+F3` | Znajdź / następny / poprzedni |
| `Ctrl+Shift+F` | Wyszukiwanie zaawansowane |

### Edycja, strony i komentarze

| Skrót | Działanie |
|---|---|
| `Ctrl+N` | Utwórz PDF z pliku |
| `Ctrl+B` | Dodaj zakładkę |
| `Ctrl+T` | Wyszukaj i zamień |
| `Ctrl+Shift+C` | Odsyłacz |
| `Ctrl+Shift+I` / `Ctrl+Shift+T` | Wstaw z pliku / pustą stronę |
| `Ctrl+Shift+D` / `Ctrl+Alt+D` | Usuń / duplikuj strony |
| `Shift+H/U/S/I` | Wyróżnij / Falowane / Przekreśl / Wstaw tekst |
| `Ctrl+U`, `Ctrl+Shift+R` | Podkreśl, Zastąp tekst |
| `Shift+T/E/C/P` | Maszyna do pisania / Pole tekstowe / Objaśnienie / Ołówek |
| `Ctrl+Shift+N` | Notatka |
| `Ctrl+8` / `Ctrl+Shift+8` | Pokaż / ukryj komentarze |
| `Ctrl+L` / `Ctrl+I` | Eksport komentarzy / wyróżnionego tekstu |
| `Ctrl+J` | Konsola JavaScript |
| `Ctrl+Shift+Y/V/B/P/E` | Czytanie na głos: aktywuj / strona / od strony / pauza / stop |
| `Ctrl+Shift+K` / `Ctrl+Shift+L` | E-mail: wszystkie karty / bieżąca |
| `Ctrl+Shift+5` / `Ctrl+Shift+U` | Opcje odczytu / Kolejność odczytu |

### Klawisze jednoliterowe

Działają po włączeniu w *Preferencje > Ogólne > Użyj klawiszy jednoliterowych*.

`H` Ręka · `V` Zaznacz · `R` Wybierz adnotację · `O` Edytuj obiekt · `T` Edytuj tekst · `C` Kadruj · `L` Łącze · `F` / `Shift+F` pola formularza · `M` Wideo/audio · `Z` / `Shift+Z` Zoom/Lupa/Szkło · `S` Notatka · `E` Zastąp tekst · `K` Stempel · `U` / `Shift+U` wyróżnianie · `D` / `Shift+D` rysowanie · `Q` Chmura · `X` Pole tekstowe · `J` / `Shift+J` stempel lub załącznik · `Shift+Y` redakcja · Spacja: chwilowo Ręka

---

## 21. Wiersz poleceń

### Uruchamianie programu

| Parametr | Działanie |
|---|---|
| `-Register` | Ustawia Foxit jako domyślny czytnik PDF |
| `-pwd<hasło>` | Otwiera PDF chroniony hasłem |
| `/p <plik.pdf>` | Drukuje na drukarce domyślnej |
| `/t <plik.pdf> [drukarka]` | Drukuje na wskazanej drukarce |
| `<plik.pdf> /A [...]` | Otwiera z parametrami widoku (strona, powiększenie itp.) |
| `<plik.pdf> /A reverse=1` | Otwiera w widoku odwróconym |

### Instalator EXE

`/help`, `/install`, `/repair`, `/uninstall`, `/quiet`, `/norestart`, `/log <ścieżka>`, `/lang <pl|en|…>`, `/DIR`, `/clean`, `/keycode`, `/DisableInternet`, `/displayadobeindoc`, `/noshortcut`, `/DISABLE_UNINSTALL_SURVEY`

### Instalator MSI

Właściwości instalatora MSI: `ADDLOCAL` (składniki: `FX_PDFVIEWER`, `FX_SPELLCHECK`, `FX_PDFA`, `FX_OCR`, `FX_CREATOR`, `FX_CONVERTEXT`, `FX_CREATORWORDADDIN`, `FX_CREATOREXCELADDIN`, `FX_CREATORPPTADDIN`, `FX_CREATOROUTLOOKADDIN`, `FX_SE`, `FX_BROWSERADDIN`), `INSTALLLOCATION`, `MAKEDEFAULT`, `VIEW_IN_BROWSER`, `DESKTOP_SHORTCUT`, `SETDEFAULTPRINTER`, `AUTO_UPDATE` (0/1/2), `INTERNET_DISABLE`, `KEYPATH`, `KEYCODE`, `CLEAN`, `REMOVENEWVERSION`, `READ_MODE`, `EMBEDDED_PDF_INOFFICE`, `ADVERTISE`

Programem można też sterować z VBA w Microsoft Office (biblioteka zgodna z API Acrobat, `Interop.Acrobat.dll`).

---

## 22. Podsumowanie

Foxit PDF Editor 14.0.7 to pełny zamiennik Adobe Acrobat Pro o budowie modułowej: rdzeń z silnikiem PDF i około 70 wtyczek. Każda wtyczka dodaje funkcje do jednej lub kilku kart wstążki. Główne obszary:

1. **Tworzenie i konwersja:** z dowolnego pliku, skanera, WWW i przez drukarkę; eksport do Office, obrazów i HTML; OCR ABBYY.
2. **Edycja:** tekst w akapitach, obiekty, obrazy, łącza, zakładki, warstwy, multimedia, 3D.
3. **Organizacja stron:** pełny zestaw operacji, oznaczenia stron i numeracja Bates.
4. **Współpraca:** komentarze, pomiary, stemple, recenzje udostępnione, ponad 15 integracji z ECM i chmurą.
5. **Formularze:** projektowanie, automatyczne rozpoznawanie pól, JavaScript (silnik V8), połączenie z bazami ODBC.
6. **Bezpieczeństwo:** hasła, certyfikaty, Microsoft Purview, redakcja (także AI), oczyszczanie dokumentu, podpisy cyfrowe, certyfikacja, znaczniki czasu, eSign i DocuSign.
7. **Zgodność:** preflight PDF/A, E, X, UA, VT; dostępność WCAG z tagowaniem.
8. **Automatyzacja:** Kreator działań (7 gotowych sekwencji po polsku), przetwarzanie wsadowe, wiersz poleceń, VBA.
9. **AI [Sub]:** czat z agentem, tłumaczenie, streszczanie, generowanie zakładek, łączniki MCP, lokalne modele.

Funkcje oznaczone **[Pro]** wymagają edycji Pro, a **[Sub]** wydania subskrypcyjnego. Na tej instalacji nie sprawdzano, która edycja jest aktywna (zależy to od licencji w `Activation.exe` / koncie Foxit).
