# Ograniczenia – ui-shell (ZetPDF.App)

- Okna wyboru plików/folderów są systemowe (OpenFileDialog/OpenFolderDialog) – ich język i motyw zależą od Windows, nie od ustawień ZetPDF.
- Pasek tytułu okien dialogowych modułów: ciemny/jasny przez DWM (Windows 10 20H1+ / 11). Po przełączeniu motywu przy otwartym oknie pasek tytułu odświeża się dopiero po zmianie aktywacji okna.
- Brak stylów domyślnych dla DatePicker/Calendar, RichTextBox ma styl pola tekstowego bez paska narzędzi.
- Teksty licencji: pełne MIT, Apache-2.0, BSD-2, BSD-3; dla komponentów PDFium (FreeType FTL, IJG, libpng, ICU, zlib, OpenJPEG, lcms2, Abseil, AGG) – wymagane noty i odesłanie do pełnych tekstów w pliku LICENSE projektu pdfium-binaries. Do `dist\` należy dołączyć ten plik LICENSE (zadanie publikacji/recenzenta).
- Brak KeyTips (skrótów Alt+litera dla kart wstążki) – Fluent.Ribbon je obsługuje, ale klucze nie są zdefiniowane w katalogu.
- Menu wstążki: SplitButton (kliknięcie części głównej) wybierany przy budowie wstążki, jeśli moduł zarejestrował obsługę polecenia typu Menu przed utworzeniem okna; późniejsza rejestracja daje DropDownButton.
- Polecenia View.ReadingMode / View.FullScreen należą do Viewer; jeśli Viewer ich nie zarejestruje, powłoka rejestruje własne przełączniki na IShellService (zapasowo). Esc wychodzi z trybu odczytu/pełnego ekranu.
- Szerokość panelu bocznego zapisywana w sekcji "Shell" pliku settings.json (ShellSettings), bo AppSettings nie ma tego pola.
- Folder tymczasowy z Ustawień działa po ponownym uruchomieniu (ścieżki ustalane przy starcie).
- Widoki dokumentów (IDocumentView) nie są zwalniane przez powłokę – robi to ViewerService (renderer-viewer) przy DocumentClosed.
- Ukryta opcja diagnostyczna `ZetPDF.exe --diag-screenshots <folder>`: renderuje okno główne i okna dialogowe w obu motywach i językach do PNG (RenderTargetBitmap) i kończy program; tworzy w sesji dwa próbne dokumenty niezapisane i panel testowy. Służy do kontroli wyglądu bez dostępu do pulpitu.
- Dodawanie poleceń do paska szybkiego dostępu odbywa się menu kontekstowym Fluent.Ribbon (teksty tego menu z lokalizacji Fluent.Ribbon, przełączane razem z językiem).
