# Narzędzia kontroli recenzenta

- `../check_localization.py` – klucze pl/en, teksty na sztywno w XAML/C#, klucze użyte w kodzie.
- `../check_themes.py` – kolory poza słownikami motywów, kontrast tekst/tło (WCAG ≥ 4.5:1) w Light/Dark.
- `ReviewCheck.Program.cs.txt` + `ReviewCheck.csproj.txt` – program konsolowy (skopiuj do `scratch\ReviewCheck\` jako `Program.cs`/`ReviewCheck.csproj`, `dotnet run -c Release -- "<folder>"`): AES-256 przez DocumentManager (PDFium, PdfPig), redakcja i oczyszczanie na prawdziwych usługach, próbki do testu dymnego exe.
- `otherreaders.py` – te same pliki w MuPDF (PyMuPDF) i pypdf, jeśli są zainstalowane (tylko odczyt, poza programem).
- `winpdf.ps1` – Windows.Data.Pdf (Windows PowerShell 5.1); uwaga: ten silnik nie obsługuje AES-256 w ogóle.
