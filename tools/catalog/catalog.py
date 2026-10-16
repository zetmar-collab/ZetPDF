# -*- coding: utf-8 -*-
"""
Jedyne źródło prawdy dla katalogu poleceń i układu wstążki ZetPDF.

Generuje:
  src/ZetPDF.Core/Commands/CommandIds.g.cs     – stałe identyfikatorów poleceń
  src/ZetPDF.Core/Commands/RibbonCatalog.g.cs  – deskryptory poleceń + układ kart/grup
  docs/WSTAZKA.md                              – tabela dla ludzi
  --resx: src/ZetPDF.App/Resources/Strings*.resx (TYLKO przy pierwszym tworzeniu szkieletu;
          potem plik należy do ui-shell i NIE jest nadpisywany)

Uruchomienie:  python tools/catalog/catalog.py [--resx]
Zmiana katalogu = zmiana kontraktu (ZetPDF.Core) -> tylko przez koordynatora.
"""
import sys, os, io, html

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

# Moduły (właściciel implementacji polecenia)
SHELL, VIEWER, PAGES, ANNOT, EDIT, SEC = "Shell", "Viewer", "Pages", "Annotations", "Editing", "Security"
AGENT = {SHELL: "ui-shell", VIEWER: "renderer-viewer", PAGES: "pages-engine", ANNOT: "annotations-forms", EDIT: "edit-convert-ocr", SEC: "security-signatures"}

# (id, moduł, skrót, klawisz jednoliterowy, rodzaj, glif Segoe Fluent/MDL2, pl, en)
B, T, M = "Button", "Toggle", "Menu"
C = []
def cmd(id, mod, sc, key, kind, glyph, pl, en):
    C.append(dict(id=id, mod=mod, sc=sc, key=key, kind=kind, glyph=glyph, pl=pl, en=en))

# ---- Plik / globalne (ui-shell)
cmd("File.Open", SHELL, "Ctrl+O", "", B, "E8E5", "Otwórz", "Open")
cmd("File.Save", SHELL, "Ctrl+S", "", B, "E74E", "Zapisz", "Save")
cmd("File.SaveAs", SHELL, "Ctrl+Shift+S", "", B, "E792", "Zapisz jako", "Save As")
cmd("File.Revert", SHELL, "", "", B, "E777", "Przywróć", "Revert")
cmd("File.Close", SHELL, "Ctrl+W;Ctrl+F4", "", B, "E711", "Zamknij", "Close")
cmd("File.CloseAll", SHELL, "Ctrl+Shift+W", "", B, "E8BB", "Zamknij wszystko", "Close All")
cmd("File.Properties", SHELL, "Ctrl+D", "", B, "E946", "Właściwości dokumentu", "Document Properties")
cmd("File.Settings", SHELL, "Ctrl+K", "", B, "E713", "Ustawienia", "Settings")
cmd("File.Exit", SHELL, "Ctrl+Q", "", B, "E7E8", "Zakończ", "Exit")
cmd("File.Print", VIEWER, "Ctrl+P", "", B, "E749", "Drukuj", "Print")
cmd("File.CreateFromFile", EDIT, "Ctrl+N", "", B, "E8A5", "Z pliku", "From File")
cmd("File.CombineFiles", EDIT, "", "", B, "E8F1", "Połącz pliki", "Combine Files")
cmd("File.CreateBlank", PAGES, "", "", B, "E7C3", "Pusty dokument", "Blank Document")
cmd("File.CreateFromClipboard", EDIT, "", "", B, "E77F", "Ze schowka", "From Clipboard")
cmd("File.ActionWizard", SEC, "", "", B, "E945", "Kreator działań", "Action Wizard")
cmd("File.OptimizeReduceSize", PAGES, "", "", B, "E91B", "Zmniejsz rozmiar pliku", "Reduce File Size")
cmd("File.OptimizeAdvanced", PAGES, "", "", B, "E9F5", "Optymalizacja zaawansowana", "Advanced Optimization")
cmd("Edit.Undo", SHELL, "Ctrl+Z", "", B, "E7A7", "Cofnij", "Undo")
cmd("Edit.Redo", SHELL, "Ctrl+Shift+Z;Ctrl+Y", "", B, "E7A6", "Ponów", "Redo")
cmd("Edit.Copy", VIEWER, "Ctrl+C", "", B, "E8C8", "Kopiuj", "Copy")
cmd("Edit.SelectAll", VIEWER, "Ctrl+A", "", B, "E8B3", "Zaznacz wszystko", "Select All")
cmd("Shell.CommandSearch", SHELL, "Alt+Q", "", B, "E721", "Wyszukaj polecenie", "Search Commands")
cmd("Shell.NextDocument", SHELL, "Ctrl+Tab", "", B, "E893", "Następny dokument", "Next Document")
cmd("Shell.PreviousDocument", SHELL, "Ctrl+Shift+Tab", "", B, "E892", "Poprzedni dokument", "Previous Document")

# ---- Narzędzia (przełączniki)
cmd("Tool.Hand", VIEWER, "", "H", T, "E7C9", "Ręka", "Hand")
cmd("Tool.SelectText", VIEWER, "", "V", T, "E8E9", "Zaznacz tekst", "Select Text")
cmd("Tool.Snapshot", VIEWER, "", "", T, "E722", "Migawka", "Snapshot")
cmd("Tool.SelectAnnotation", ANNOT, "", "R", T, "E8B0", "Wybierz adnotację", "Select Annotation")

# ---- Widok i nawigacja (renderer-viewer, poza oznaczonymi)
cmd("View.ZoomIn", VIEWER, "Ctrl+OemPlus;Ctrl+Add", "", B, "E8A3", "Powiększ", "Zoom In")
cmd("View.ZoomOut", VIEWER, "Ctrl+OemMinus;Ctrl+Subtract", "", B, "E71F", "Pomniejsz", "Zoom Out")
cmd("View.ZoomTo", VIEWER, "Ctrl+M", "", B, "E71E", "Powiększ do…", "Zoom To…")
cmd("View.ActualSize", VIEWER, "Ctrl+D1", "", B, "E9A6", "Rzeczywisty rozmiar", "Actual Size")
cmd("View.FitPage", VIEWER, "Ctrl+D0", "", B, "E9A6", "Dopasuj stronę", "Fit Page")
cmd("View.FitWidth", VIEWER, "Ctrl+D2", "", B, "E799", "Dopasuj szerokość", "Fit Width")
cmd("View.FitVisible", VIEWER, "Ctrl+D3", "", B, "E9A6", "Dopasuj widoczne", "Fit Visible")
cmd("View.RotateLeft", VIEWER, "Ctrl+Shift+OemMinus", "", B, "E7AD", "Obróć widok w lewo", "Rotate View Left")
cmd("View.RotateRight", VIEWER, "Ctrl+Shift+OemPlus", "", B, "E7AD", "Obróć widok w prawo", "Rotate View Right")
cmd("View.SinglePage", VIEWER, "", "", T, "E7C3", "Jedna strona", "Single Page")
cmd("View.Continuous", VIEWER, "", "", T, "E8A9", "Ciągły", "Continuous")
cmd("View.TwoPages", VIEWER, "", "", T, "E89A", "Dwie strony", "Two Pages")
cmd("View.TwoPagesContinuous", VIEWER, "", "", T, "E8A9", "Dwie strony ciągłe", "Two Pages Continuous")
cmd("View.ReadingMode", VIEWER, "Ctrl+H", "", T, "E736", "Tryb odczytu", "Reading Mode")
cmd("View.FullScreen", VIEWER, "F11", "", T, "E740", "Pełny ekran", "Full Screen")
cmd("View.TogglePanel", SHELL, "F4", "", T, "E700", "Panel boczny", "Side Panel")
cmd("View.ToggleRibbon", SHELL, "Ctrl+F11", "", T, "E70E", "Zwiń wstążkę", "Collapse Ribbon")
cmd("View.PanelThumbnails", SHELL, "", "", B, "E8A9", "Miniatury", "Thumbnails")
cmd("View.PanelBookmarks", SHELL, "", "", B, "E8A4", "Zakładki", "Bookmarks")
cmd("View.PanelComments", SHELL, "", "", B, "E90A", "Komentarze", "Comments")
cmd("View.PanelAttachments", SHELL, "", "", B, "E723", "Załączniki", "Attachments")
cmd("View.PanelSignatures", SHELL, "", "", B, "EB95", "Podpisy", "Signatures")
cmd("Nav.GoToPage", VIEWER, "Ctrl+G", "", B, "E8AD", "Przejdź do strony", "Go to Page")
cmd("Nav.FirstPage", VIEWER, "Ctrl+Home", "", B, "E892", "Pierwsza strona", "First Page")
cmd("Nav.PreviousPage", VIEWER, "Ctrl+PageUp", "", B, "E76B", "Poprzednia strona", "Previous Page")
cmd("Nav.NextPage", VIEWER, "Ctrl+PageDown", "", B, "E76C", "Następna strona", "Next Page")
cmd("Nav.LastPage", VIEWER, "Ctrl+End", "", B, "E893", "Ostatnia strona", "Last Page")
cmd("Nav.Find", VIEWER, "Ctrl+F", "", B, "E721", "Znajdź", "Find")
cmd("Nav.FindNext", VIEWER, "F3", "", B, "E74B", "Znajdź następny", "Find Next")
cmd("Nav.FindPrevious", VIEWER, "Shift+F3", "", B, "E74A", "Znajdź poprzedni", "Find Previous")
cmd("Nav.AddBookmark", VIEWER, "Ctrl+B", "", B, "E8A4", "Dodaj zakładkę", "Add Bookmark")

# ---- Konwersja i OCR (edit-convert-ocr; skala szarości: pages-engine)
cmd("Convert.ToImages", EDIT, "", "", B, "EB9F", "Do obrazów", "To Images")
cmd("Convert.ToText", EDIT, "", "", B, "E8D2", "Do tekstu (TXT)", "To Text (TXT)")
cmd("Convert.ToDocx", EDIT, "", "", B, "E8A5", "Do Word (DOCX)", "To Word (DOCX)")
cmd("Convert.Grayscale", PAGES, "", "", B, "E790", "Skala szarości", "Grayscale")
cmd("Ocr.RecognizeDocument", EDIT, "", "", B, "E8EF", "Rozpoznaj tekst (OCR)", "Recognize Text (OCR)")
cmd("Ocr.RecognizeRegion", EDIT, "", "", B, "EF20", "OCR zaznaczonego obszaru", "OCR Selected Area")
cmd("Ocr.QuickRecognize", EDIT, "", "", B, "E945", "Szybkie rozpoznawanie", "Quick Recognition")
cmd("Ocr.SuspectResults", EDIT, "", "", B, "E9CE", "Podejrzane wyniki", "Suspect Results")

# ---- Edycja treści, łącza (edit-convert-ocr)
cmd("Edit.EditText", EDIT, "", "T", T, "E8D2", "Edytuj tekst", "Edit Text")
cmd("Edit.EditObject", EDIT, "", "O", T, "E7C9", "Edytuj obiekt", "Edit Object")
cmd("Edit.AddText", EDIT, "", "", T, "E8E4", "Dodaj tekst", "Add Text")
cmd("Edit.AddImage", EDIT, "", "", B, "EB9F", "Dodaj obraz", "Add Image")
cmd("Edit.AddShape", EDIT, "", "", M, "E91F", "Dodaj kształt", "Add Shape")
cmd("Edit.FindReplace", EDIT, "Ctrl+T", "", B, "E8AB", "Wyszukaj i zamień", "Find and Replace")
cmd("Edit.Link", EDIT, "", "L", T, "E71B", "Łącze", "Link")
cmd("Edit.CreateLinksFromUrls", EDIT, "", "", B, "E71B", "Utwórz łącza z adresów URL", "Create Links from URLs")
cmd("Edit.RemoveAllLinks", EDIT, "", "", B, "E74D", "Usuń wszystkie łącza", "Remove All Links")
cmd("Edit.BookmarksFromHeadings", EDIT, "", "", B, "E8FD", "Zakładki z nagłówków", "Bookmarks from Headings")

# ---- Organizacja stron i oznaczenia (pages-engine)
cmd("Pages.InsertFromFile", PAGES, "Ctrl+Shift+I", "", B, "E8E5", "Wstaw z pliku", "Insert from File")
cmd("Pages.InsertBlank", PAGES, "Ctrl+Shift+T", "", B, "E7C3", "Pusta strona", "Blank Page")
cmd("Pages.Delete", PAGES, "Ctrl+Shift+D", "", B, "E74D", "Usuń strony", "Delete Pages")
cmd("Pages.Extract", PAGES, "", "", B, "E8C6", "Wyodrębnij", "Extract")
cmd("Pages.Replace", PAGES, "", "", B, "E8AB", "Zastąp", "Replace")
cmd("Pages.Duplicate", PAGES, "Ctrl+Alt+D", "", B, "E8C8", "Duplikuj", "Duplicate")
cmd("Pages.Move", PAGES, "", "", B, "E8DE", "Przenieś", "Move")
cmd("Pages.Swap", PAGES, "", "", B, "E8AB", "Zamień", "Swap")
cmd("Pages.Reverse", PAGES, "", "", B, "E8CB", "Odwróć kolejność", "Reverse Order")
cmd("Pages.Interleave", PAGES, "", "", B, "E8F1", "Przeplatanie", "Interleave")
cmd("Pages.Split", PAGES, "", "", B, "E8C6", "Podziel dokument", "Split Document")
cmd("Pages.Merge", PAGES, "", "", B, "E8F1", "Scal pliki PDF", "Merge PDFs")
cmd("Pages.RotateLeft", PAGES, "", "", B, "E7AD", "Obróć w lewo", "Rotate Left")
cmd("Pages.RotateRight", PAGES, "", "", B, "E7AD", "Obróć w prawo", "Rotate Right")
cmd("Pages.Rotate", PAGES, "", "", B, "E7AD", "Obracanie stron…", "Rotate Pages…")
cmd("Pages.Crop", PAGES, "", "C", B, "E7A8", "Kadruj strony", "Crop Pages")
cmd("Pages.Resize", PAGES, "", "", B, "E740", "Zmień rozmiar stron", "Resize Pages")
cmd("Pages.Flatten", PAGES, "", "", B, "E81E", "Spłaszcz", "Flatten")
cmd("Pages.PageLabels", PAGES, "", "", B, "E8EF", "Format numerów stron", "Page Number Format")
cmd("Stamp.Watermark", PAGES, "", "", M, "E7E6", "Znak wodny", "Watermark")
cmd("Stamp.Watermark.Add", PAGES, "", "", B, "E710", "Dodaj znak wodny", "Add Watermark")
cmd("Stamp.Watermark.Update", PAGES, "", "", B, "E70F", "Aktualizuj znak wodny", "Update Watermark")
cmd("Stamp.Watermark.RemoveAll", PAGES, "", "", B, "E74D", "Usuń wszystkie znaki wodne", "Remove All Watermarks")
cmd("Stamp.Background", PAGES, "", "", M, "E790", "Tło", "Background")
cmd("Stamp.Background.Add", PAGES, "", "", B, "E710", "Dodaj tło", "Add Background")
cmd("Stamp.Background.Update", PAGES, "", "", B, "E70F", "Aktualizuj tło", "Update Background")
cmd("Stamp.Background.RemoveAll", PAGES, "", "", B, "E74D", "Usuń wszystkie tła", "Remove All Backgrounds")
cmd("Stamp.HeaderFooter", PAGES, "", "", M, "E8C1", "Nagłówek i stopka", "Header & Footer")
cmd("Stamp.HeaderFooter.Add", PAGES, "", "", B, "E710", "Dodaj nagłówek i stopkę", "Add Header & Footer")
cmd("Stamp.HeaderFooter.Update", PAGES, "", "", B, "E70F", "Aktualizuj nagłówek i stopkę", "Update Header & Footer")
cmd("Stamp.HeaderFooter.RemoveAll", PAGES, "", "", B, "E74D", "Usuń nagłówki i stopki", "Remove Headers & Footers")
cmd("Stamp.PageNumbers", PAGES, "", "", B, "E8EF", "Numeracja stron", "Page Numbers")
cmd("Stamp.Bates", PAGES, "", "", M, "E8EF", "Numeracja Bates", "Bates Numbering")
cmd("Stamp.Bates.Add", PAGES, "", "", B, "E710", "Dodaj numerację Bates", "Add Bates Numbering")
cmd("Stamp.Bates.RemoveAll", PAGES, "", "", B, "E74D", "Usuń numerację Bates", "Remove Bates Numbering")
cmd("Sanitize.Document", PAGES, "", "", B, "E8F6", "Oczyszczanie dokumentu", "Sanitize Document")

# ---- Komentarze (annotations-forms)
cmd("Comment.Highlight", ANNOT, "Shift+H", "", T, "E7E6", "Wyróżnij", "Highlight")
cmd("Comment.AreaHighlight", ANNOT, "", "", T, "E7E6", "Wyróżnienie obszaru", "Area Highlight")
cmd("Comment.Underline", ANNOT, "Ctrl+U", "", T, "E8DC", "Podkreśl", "Underline")
cmd("Comment.Squiggly", ANNOT, "Shift+U", "", T, "E8DC", "Podkreślenie falowane", "Squiggly Underline")
cmd("Comment.StrikeOut", ANNOT, "Shift+S", "", T, "EDE0", "Przekreśl", "Strikethrough")
cmd("Comment.Note", ANNOT, "Ctrl+Shift+N", "S", T, "E70B", "Notatka", "Note")
cmd("Comment.Typewriter", ANNOT, "Shift+T", "", T, "E8D2", "Maszyna do pisania", "Typewriter")
cmd("Comment.TextBox", ANNOT, "Shift+E", "X", T, "E8E4", "Pole tekstowe", "Text Box")
cmd("Comment.Callout", ANNOT, "Shift+C", "", T, "E8BD", "Objaśnienie", "Callout")
cmd("Comment.Rectangle", ANNOT, "", "", T, "E739", "Prostokąt", "Rectangle")
cmd("Comment.Oval", ANNOT, "", "", T, "EA3A", "Owal", "Oval")
cmd("Comment.Line", ANNOT, "", "", T, "E738", "Linia", "Line")
cmd("Comment.Arrow", ANNOT, "", "", T, "E72A", "Strzałka", "Arrow")
cmd("Comment.Polygon", ANNOT, "", "", T, "E879", "Wielokąt", "Polygon")
cmd("Comment.Polyline", ANNOT, "", "", T, "E879", "Linia łamana", "Polyline")
cmd("Comment.Pencil", ANNOT, "Shift+P", "", T, "EE56", "Ołówek", "Pencil")
cmd("Comment.Stamp", ANNOT, "", "K", M, "E8EC", "Stempel", "Stamp")
cmd("Comment.CustomStamp", ANNOT, "", "", B, "EB9F", "Stempel z obrazu", "Stamp from Image")
cmd("Comment.Import", ANNOT, "", "", B, "E8B5", "Importuj komentarze", "Import Comments")
cmd("Comment.ExportAll", ANNOT, "Ctrl+L", "", B, "EDE1", "Eksportuj komentarze", "Export Comments")
cmd("Comment.ShowAll", ANNOT, "Ctrl+D8", "", B, "E7B3", "Pokaż wszystkie komentarze", "Show All Comments")
cmd("Comment.HideAll", ANNOT, "Ctrl+Shift+D8", "", B, "ED1A", "Ukryj wszystkie komentarze", "Hide All Comments")
cmd("Comment.DeleteAll", ANNOT, "", "", B, "E74D", "Usuń wszystkie komentarze", "Delete All Comments")
cmd("Attach.AddFile", ANNOT, "", "", B, "E723", "Załącz plik", "Attach File")

# ---- Formularze (annotations-forms)
cmd("Form.TextField", ANNOT, "", "", T, "E8E4", "Pole tekstowe", "Text Field")
cmd("Form.CheckBox", ANNOT, "", "", T, "E73A", "Pole wyboru", "Check Box")
cmd("Form.RadioButton", ANNOT, "", "", T, "ECCB", "Przycisk opcji", "Radio Button")
cmd("Form.ComboBox", ANNOT, "", "", T, "E70D", "Pole kombi", "Combo Box")
cmd("Form.ListBox", ANNOT, "", "", T, "EA37", "Pole listy", "List Box")
cmd("Form.Button", ANNOT, "", "", T, "E815", "Przycisk", "Button")
cmd("Form.SignatureField", ANNOT, "", "", T, "EB95", "Pole podpisu", "Signature Field")
cmd("Form.DateField", ANNOT, "", "", T, "E787", "Pole daty", "Date Field")
cmd("Form.TabOrder", ANNOT, "", "", B, "E8FD", "Kolejność tabulacji", "Tab Order")
cmd("Form.HighlightFields", ANNOT, "", "", T, "E7E6", "Wyróżnij pola", "Highlight Fields")
cmd("Form.ImportData", ANNOT, "", "", B, "E8B5", "Importuj dane", "Import Data")
cmd("Form.ExportData", ANNOT, "", "", B, "EDE1", "Eksportuj dane", "Export Data")
cmd("Form.Reset", ANNOT, "", "", B, "E72C", "Resetuj formularz", "Reset Form")
cmd("FillSign.Open", ANNOT, "", "", B, "EE56", "Wypełnij i podpisz", "Fill & Sign")
cmd("FillSign.Text", ANNOT, "", "", T, "E8D2", "Tekst", "Text")
cmd("FillSign.Check", ANNOT, "", "", T, "E73E", "Znacznik ✓", "Check Mark ✓")
cmd("FillSign.Cross", ANNOT, "", "", T, "E711", "Krzyżyk ✗", "Cross ✗")
cmd("FillSign.Dot", ANNOT, "", "", T, "E91F", "Kropka", "Dot")
cmd("FillSign.Line", ANNOT, "", "", T, "E738", "Linia", "Line")
cmd("FillSign.Signature", ANNOT, "", "", M, "EE56", "Podpis odręczny", "Handwritten Signature")
cmd("FillSign.ManageSignatures", ANNOT, "", "", B, "E713", "Zarządzaj podpisami", "Manage Signatures")
cmd("FillSign.Flatten", ANNOT, "", "", B, "E81E", "Spłaszcz przy zapisie", "Flatten on Save")
cmd("FillSign.Close", ANNOT, "", "", B, "E711", "Zamknij Wypełnij i podpisz", "Close Fill & Sign")

# ---- Ochrona (security-signatures; redakcja: edit-convert-ocr)
cmd("Security.PasswordProtect", SEC, "", "", B, "E72E", "Ochrona hasłem", "Password Protection")
cmd("Security.RemoveSecurity", SEC, "", "", B, "E785", "Usuń zabezpieczenia", "Remove Security")
cmd("Security.Properties", SEC, "", "", B, "E946", "Właściwości zabezpieczeń", "Security Properties")
cmd("Redact.MarkText", EDIT, "", "", T, "E8E9", "Zaznacz tekst do redakcji", "Mark Text for Redaction")
cmd("Redact.MarkArea", EDIT, "", "", T, "EF20", "Zaznacz obszar do redakcji", "Mark Area for Redaction")
cmd("Redact.SearchAndRedact", EDIT, "", "", B, "E721", "Wyszukaj i redaguj", "Search & Redact")
cmd("Redact.Apply", EDIT, "", "", B, "E73E", "Zastosuj redakcje", "Apply Redactions")
cmd("Sign.PlaceSignature", SEC, "", "", B, "EB95", "Podpisz cyfrowo", "Sign Digitally")
cmd("Sign.Validate", SEC, "", "", B, "E9D5", "Weryfikuj podpisy", "Validate Signatures")
cmd("Sign.Timestamp", SEC, "", "", B, "E823", "Znacznik czasu dokumentu", "Document Timestamp")
cmd("Sign.DigitalIds", SEC, "", "", B, "E8D7", "Identyfikatory cyfrowe", "Digital IDs")
cmd("Compare.Start", SEC, "", "", B, "E89A", "Porównaj dokumenty", "Compare Documents")
cmd("Compare.Next", SEC, "Ctrl+OemPeriod", "", B, "E74B", "Następna zmiana", "Next Change")
cmd("Compare.Previous", SEC, "Ctrl+OemComma", "", B, "E74A", "Poprzednia zmiana", "Previous Change")
cmd("Compare.Report", SEC, "", "", B, "E9F9", "Raport porównania", "Comparison Report")
cmd("Compare.Close", SEC, "", "", B, "E711", "Zamknij porównanie", "Close Comparison")

# ---- Pomoc (ui-shell)
cmd("Help.Shortcuts", SHELL, "F1", "", B, "E765", "Skróty klawiszowe", "Keyboard Shortcuts")
cmd("Help.Licenses", SHELL, "", "", B, "E8F1", "Licencje bibliotek", "Third-party Licenses")
cmd("Help.OpenLogFolder", SHELL, "", "", B, "E838", "Folder dziennika", "Log Folder")
cmd("Help.About", SHELL, "", "", B, "E946", "O programie", "About")

# Menu: dzieci poleceń typu Menu (rozwijane)
MENUS = {
    "Stamp.Watermark": ["Stamp.Watermark.Add", "Stamp.Watermark.Update", "Stamp.Watermark.RemoveAll"],
    "Stamp.Background": ["Stamp.Background.Add", "Stamp.Background.Update", "Stamp.Background.RemoveAll"],
    "Stamp.HeaderFooter": ["Stamp.HeaderFooter.Add", "Stamp.HeaderFooter.Update", "Stamp.HeaderFooter.RemoveAll"],
    "Stamp.Bates": ["Stamp.Bates.Add", "Stamp.Bates.RemoveAll"],
}
# Comment.Stamp, Edit.AddShape, FillSign.Signature – dzieci dynamiczne (dostarcza moduł przez RegisterMenuItems)

# Układ wstążki: (id karty, kontekstowa?, pl, en, [(id grupy, pl, en, [polecenia])])
TABS = [
    ("Home", False, "Początek", "Home", [
        ("Home.Tools", "Narzędzia", "Tools", ["Tool.Hand", "Tool.SelectText", "Tool.SelectAnnotation", "Tool.Snapshot"]),
        ("Home.Zoom", "Powiększenie", "Zoom", ["View.ZoomIn", "View.ZoomOut", "View.ZoomTo", "View.FitPage", "View.FitWidth", "View.FitVisible", "View.ActualSize"]),
        ("Home.View", "Widok", "View", ["View.RotateLeft", "View.RotateRight"]),
        ("Home.Edit", "Edycja", "Edit", ["Edit.EditText", "Edit.EditObject", "Comment.Typewriter", "Comment.Highlight"]),
        ("Home.Pages", "Strony", "Pages", ["Pages.InsertFromFile", "Pages.InsertBlank", "Nav.AddBookmark"]),
        ("Home.Recognize", "Rozpoznawanie", "Recognition", ["Ocr.QuickRecognize"]),
        ("Home.Sign", "Podpis", "Sign", ["FillSign.Open"]),
    ]),
    ("Convert", False, "Konwertuj", "Convert", [
        ("Convert.Create", "Utwórz", "Create", ["File.CreateFromFile", "File.CombineFiles", "File.CreateBlank", "File.CreateFromClipboard"]),
        ("Convert.Export", "Eksport", "Export", ["Convert.ToImages", "Convert.ToText", "Convert.ToDocx"]),
        ("Convert.Ocr", "OCR", "OCR", ["Ocr.RecognizeDocument", "Ocr.RecognizeRegion", "Ocr.QuickRecognize", "Ocr.SuspectResults"]),
        ("Convert.Color", "Kolor", "Color", ["Convert.Grayscale"]),
    ]),
    ("Edit", False, "Edytuj", "Edit", [
        ("Edit.Content", "Edycja treści", "Content Editing", ["Edit.EditText", "Edit.EditObject", "Edit.AddText", "Edit.AddImage", "Edit.AddShape", "Edit.FindReplace"]),
        ("Edit.Links", "Łącza i nawigacja", "Links & Navigation", ["Edit.Link", "Edit.CreateLinksFromUrls", "Edit.RemoveAllLinks", "Nav.AddBookmark", "Edit.BookmarksFromHeadings"]),
        ("Edit.Attachments", "Załączniki", "Attachments", ["Attach.AddFile"]),
    ]),
    ("Organize", False, "Organizuj", "Organize", [
        ("Organize.Pages", "Strony", "Pages", ["Pages.InsertFromFile", "Pages.InsertBlank", "Pages.Delete", "Pages.Extract", "Pages.Replace", "Pages.Duplicate", "Pages.Move", "Pages.Swap", "Pages.Reverse", "Pages.Interleave"]),
        ("Organize.Transform", "Podział i przekształcenia", "Split & Transform", ["Pages.Split", "Pages.Merge", "Pages.RotateLeft", "Pages.RotateRight", "Pages.Rotate", "Pages.Crop", "Pages.Resize", "Pages.Flatten"]),
        ("Organize.Marks", "Oznaczenia stron", "Page Marks", ["Stamp.Watermark", "Stamp.Background", "Stamp.HeaderFooter", "Stamp.PageNumbers", "Stamp.Bates", "Pages.PageLabels"]),
    ]),
    ("Comment", False, "Komentarz", "Comment", [
        ("Comment.Markup", "Adiustacja tekstu", "Text Markup", ["Comment.Highlight", "Comment.AreaHighlight", "Comment.Underline", "Comment.Squiggly", "Comment.StrikeOut"]),
        ("Comment.Text", "Notatki i tekst", "Notes & Text", ["Comment.Note", "Comment.Typewriter", "Comment.TextBox", "Comment.Callout"]),
        ("Comment.Drawing", "Rysowanie", "Drawing", ["Comment.Rectangle", "Comment.Oval", "Comment.Line", "Comment.Arrow", "Comment.Polygon", "Comment.Polyline", "Comment.Pencil"]),
        ("Comment.Stamps", "Stemple", "Stamps", ["Comment.Stamp", "Comment.CustomStamp"]),
        ("Comment.Manage", "Zarządzanie", "Manage", ["Comment.Import", "Comment.ExportAll", "Comment.ShowAll", "Comment.HideAll", "Comment.DeleteAll", "View.PanelComments"]),
    ]),
    ("View", False, "Widok", "View", [
        ("View.Layout", "Układ stron", "Page Layout", ["View.SinglePage", "View.Continuous", "View.TwoPages", "View.TwoPagesContinuous"]),
        ("View.Mode", "Tryb", "Mode", ["View.ReadingMode", "View.FullScreen", "View.RotateLeft", "View.RotateRight"]),
        ("View.Navigation", "Nawigacja", "Navigation", ["Nav.GoToPage", "Nav.FirstPage", "Nav.PreviousPage", "Nav.NextPage", "Nav.LastPage"]),
        ("View.Find", "Wyszukiwanie", "Find", ["Nav.Find", "Nav.FindNext", "Nav.FindPrevious"]),
        ("View.Compare", "Porównanie", "Compare", ["Compare.Start"]),
        ("View.Panels", "Panele", "Panels", ["View.TogglePanel", "View.PanelThumbnails", "View.PanelBookmarks", "View.PanelComments", "View.PanelAttachments", "View.PanelSignatures", "View.ToggleRibbon"]),
    ]),
    ("Form", False, "Formularz", "Form", [
        ("Form.Fields", "Pola formularza", "Form Fields", ["Form.TextField", "Form.CheckBox", "Form.RadioButton", "Form.ComboBox", "Form.ListBox", "Form.Button", "Form.SignatureField", "Form.DateField"]),
        ("Form.Design", "Projektowanie", "Design", ["Tool.SelectAnnotation", "Form.TabOrder", "Form.HighlightFields"]),
        ("Form.Data", "Dane", "Data", ["Form.ImportData", "Form.ExportData", "Form.Reset"]),
    ]),
    ("Protect", False, "Ochrona", "Protect", [
        ("Protect.Security", "Zabezpieczenia", "Security", ["Security.PasswordProtect", "Security.RemoveSecurity", "Security.Properties"]),
        ("Protect.Redaction", "Redakcja", "Redaction", ["Redact.MarkText", "Redact.MarkArea", "Redact.SearchAndRedact", "Redact.Apply"]),
        ("Protect.HiddenData", "Ukryte dane", "Hidden Data", ["Sanitize.Document"]),
        ("Protect.Signatures", "Podpisy", "Signatures", ["FillSign.Open", "Sign.PlaceSignature", "Sign.Validate", "Sign.Timestamp", "Sign.DigitalIds"]),
    ]),
    ("Help", False, "Pomoc", "Help", [
        ("Help.Main", "Pomoc", "Help", ["Help.Shortcuts", "Help.Licenses", "Help.OpenLogFolder", "Help.About"]),
    ]),
    ("FillSign", True, "Wypełnij i podpisz", "Fill & Sign", [
        ("FillSign.Tools", "Narzędzia", "Tools", ["FillSign.Text", "FillSign.Check", "FillSign.Cross", "FillSign.Dot", "FillSign.Line"]),
        ("FillSign.Signatures", "Podpisy", "Signatures", ["FillSign.Signature", "FillSign.ManageSignatures", "FillSign.Flatten"]),
        ("FillSign.CloseGroup", "Zamknij", "Close", ["FillSign.Close"]),
    ]),
    ("CompareResults", True, "Wyniki porównania", "Compare Results", [
        ("CompareResults.Navigate", "Zmiany", "Changes", ["Compare.Previous", "Compare.Next"]),
        ("CompareResults.Report", "Raport", "Report", ["Compare.Report", "Compare.Close"]),
    ]),
]
# Plik (backstage) – polecenia pokazywane w menu Plik, w tej kolejności (ostatnie pliki dodaje powłoka)
BACKSTAGE = ["File.Open", "File.Save", "File.SaveAs", "File.Revert", "File.Close", "File.CloseAll", "File.Properties",
             "File.Print", "File.CreateFromFile", "File.CombineFiles", "File.CreateBlank", "File.CreateFromClipboard",
             "File.OptimizeReduceSize", "File.OptimizeAdvanced", "File.ActionWizard", "File.Settings", "File.Exit"]
QAT = ["File.Open", "File.Save", "Edit.Undo", "Edit.Redo", "File.Print"]

# ------------------------------------------------------------------ walidacja
ids = [c["id"] for c in C]
assert len(ids) == len(set(ids)), "duplikat id"
byid = {c["id"]: c for c in C}
used = set(BACKSTAGE) | set(QAT)
for t in TABS:
    for g in t[4]:
        for i in g[3]:
            assert i in byid, i
            used.add(i)
for p, ch in MENUS.items():
    assert byid[p]["kind"] == M
    for i in ch:
        assert i in byid, i
        used.add(i)
scs = {}
for c in C:
    for s in filter(None, c["sc"].split(";")):
        assert s not in scs, f"konflikt skrótu {s}: {scs[s]} i {c['id']}"
        scs[s] = c["id"]
keys = {}
for c in C:
    if c["key"]:
        assert c["key"] not in keys, f"konflikt klawisza {c['key']}"
        keys[c["key"]] = c["id"]
unused = [i for i in ids if i not in used and i not in ("Edit.Copy", "Edit.SelectAll", "Shell.CommandSearch", "Shell.NextDocument", "Shell.PreviousDocument")]
assert not unused, unused

def cs_name(id):
    return id.replace(".", "_")

def cs_str(s):
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'

# ------------------------------------------------------------------ CommandIds.g.cs
def gen_ids():
    groups = {}
    for c in C:
        area = c["id"].split(".")[0]
        groups.setdefault(area, []).append(c)
    o = io.StringIO()
    o.write("// <auto-generated> Wygenerowano przez tools/catalog/catalog.py – nie edytuj ręcznie. </auto-generated>\n")
    o.write("namespace ZetPDF.Core.Commands;\n\n")
    o.write("/// <summary>Identyfikatory wszystkich poleceń z katalogu wstążki (kontrakt zamrożony).</summary>\n")
    o.write("public static class CommandIds\n{\n")
    for area, cs in groups.items():
        o.write(f"    public static class {area}\n    {{\n")
        for c in cs:
            name = c["id"][len(area) + 1:].replace(".", "")
            o.write(f"        /// <summary>{html.escape(c['pl'])} / {html.escape(c['en'])} – moduł {c['mod']}.</summary>\n")
            o.write(f"        public const string {name} = {cs_str(c['id'])};\n")
        o.write("    }\n\n")
    o.write("}\n")
    return o.getvalue()

def gen_catalog():
    o = io.StringIO()
    o.write("// <auto-generated> Wygenerowano przez tools/catalog/catalog.py – nie edytuj ręcznie. </auto-generated>\n")
    o.write("namespace ZetPDF.Core.Commands;\n\n")
    o.write("public static partial class RibbonCatalog\n{\n")
    o.write("    private static CommandDescriptor[] CreateCommands() => new CommandDescriptor[]\n    {\n")
    for c in C:
        glyph = "\\u" + c["glyph"]
        o.write(f"        new({cs_str(c['id'])}, FeatureModuleId.{c['mod']}, CommandKind.{c['kind']}, \"{glyph}\", {cs_str(c['sc'])}, {cs_str(c['key'])}),\n")
    o.write("    };\n\n")
    o.write("    private static RibbonTabDescriptor[] CreateTabs() => new RibbonTabDescriptor[]\n    {\n")
    for (tid, ctx, pl, en, groups) in TABS:
        o.write(f"        new({cs_str(tid)}, {str(ctx).lower()}, new RibbonGroupDescriptor[]\n        {{\n")
        for (gid, gpl, gen, items) in groups:
            o.write(f"            new({cs_str(gid)}, new[] {{ {', '.join(cs_str(i) for i in items)} }}),\n")
        o.write("        }),\n")
    o.write("    };\n\n")
    o.write("    private static Dictionary<string, string[]> CreateMenus() => new()\n    {\n")
    for p, ch in MENUS.items():
        o.write(f"        [{cs_str(p)}] = new[] {{ {', '.join(cs_str(i) for i in ch)} }},\n")
    o.write("    };\n\n")
    o.write(f"    private static string[] CreateBackstage() => new[] {{ {', '.join(cs_str(i) for i in BACKSTAGE)} }};\n")
    o.write(f"    private static string[] CreateQuickAccess() => new[] {{ {', '.join(cs_str(i) for i in QAT)} }};\n")
    o.write("}\n")
    return o.getvalue()

def gen_md():
    o = io.StringIO()
    o.write("# Wstążka ZetPDF – karty, grupy i polecenia\n\n")
    o.write("> Wygenerowano przez `tools/catalog/catalog.py` (jedyne źródło prawdy: ten skrypt → `src/ZetPDF.Core/Commands/*.g.cs`). ")
    o.write("Teksty: klucz `Cmd.<Id>` (etykieta), `Cmd.<Id>.Tip` (podpowiedź, opcjonalnie), `Ribbon.Tab.<Id>`, `Ribbon.Group.<Id>` w `src/ZetPDF.App/Resources/Strings*.resx` (ui-shell).\n")
    o.write("> Skróty w notacji WPF `KeyGesture` (`D0` = klawisz 0, `OemPlus` = `+`). Kilka skrótów rozdzielonych `;`. Klawisze jednoliterowe działają tylko po włączeniu w Ustawieniach i nigdy, gdy fokus jest w polu tekstowym.\n\n")
    o.write("## Pasek szybkiego dostępu\n\n" + ", ".join(f"`{i}`" for i in QAT) + "\n\n")
    o.write("## Plik (backstage)\n\n| Id | PL | EN | Moduł (agent) | Skrót |\n|---|---|---|---|---|\n")
    for i in BACKSTAGE:
        c = byid[i]; o.write(f"| `{i}` | {c['pl']} | {c['en']} | {c['mod']} ({AGENT[c['mod']]}) | {c['sc'].replace(';', ', ')} |\n")
    o.write("\n")
    for (tid, ctx, pl, en, groups) in TABS:
        o.write(f"## Karta `{tid}` – {pl} / {en}{' (kontekstowa)' if ctx else ''}\n\n")
        o.write("| Grupa | Id polecenia | PL | EN | Moduł (agent) | Rodzaj | Skrót | Klawisz |\n|---|---|---|---|---|---|---|---|\n")
        for (gid, gpl, gen, items) in groups:
            for i in items:
                c = byid[i]
                o.write(f"| `{gid}` {gpl} | `{i}` | {c['pl']} | {c['en']} | {c['mod']} ({AGENT[c['mod']]}) | {c['kind']} | {c['sc'].replace(';', ', ')} | {c['key']} |\n")
                for ch in MENUS.get(i, []):
                    d = byid[ch]
                    o.write(f"| ↳ menu | `{ch}` | {d['pl']} | {d['en']} | {d['mod']} ({AGENT[d['mod']]}) | {d['kind']} | {d['sc']} | {d['key']} |\n")
        o.write("\n")
    o.write("## Polecenia bez miejsca na wstążce (tylko skrót / menu kontekstowe)\n\n| Id | PL | EN | Moduł | Skrót |\n|---|---|---|---|---|\n")
    for i in ("Edit.Copy", "Edit.SelectAll", "Shell.CommandSearch", "Shell.NextDocument", "Shell.PreviousDocument"):
        c = byid[i]; o.write(f"| `{i}` | {c['pl']} | {c['en']} | {c['mod']} ({AGENT[c['mod']]}) | {c['sc'].replace(';', ', ')} |\n")
    o.write("\n## Liczba poleceń według modułu\n\n| Moduł | Agent | Liczba |\n|---|---|---|\n")
    for m in (SHELL, VIEWER, PAGES, ANNOT, EDIT, SEC):
        o.write(f"| {m} | {AGENT[m]} | {sum(1 for c in C if c['mod'] == m)} |\n")
    o.write(f"\nRazem: {len(C)} poleceń, {len(TABS)} kart, {sum(len(t[4]) for t in TABS)} grup.\n")
    return o.getvalue()

def resx(entries):
    o = io.StringIO()
    o.write('<?xml version="1.0" encoding="utf-8"?>\n<root>\n')
    o.write('  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>\n')
    o.write('  <resheader name="version"><value>2.0</value></resheader>\n')
    o.write('  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>\n')
    o.write('  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>\n')
    for k, v in entries:
        o.write(f'  <data name="{html.escape(k)}" xml:space="preserve"><value>{html.escape(v, quote=False)}</value></data>\n')
    o.write("</root>\n")
    return o.getvalue()

def gen_resx(lang):
    e = []
    for (tid, ctx, pl, en, groups) in TABS:
        e.append((f"Ribbon.Tab.{tid}", pl if lang == "pl" else en))
        for (gid, gpl, gen, items) in groups:
            e.append((f"Ribbon.Group.{gid}", gpl if lang == "pl" else gen))
    e.append(("Ribbon.Tab.File", "Plik" if lang == "pl" else "File"))
    for c in C:
        e.append((f"Cmd.{c['id']}", c["pl"] if lang == "pl" else c["en"]))
    return e

def write(path, text):
    full = os.path.join(ROOT, path)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    with open(full, "w", encoding="utf-8", newline="\r\n") as f:
        f.write(text)
    print("zapisano", path)

if __name__ == "__main__":
    write("src/ZetPDF.Core/Commands/CommandIds.g.cs", gen_ids())
    write("src/ZetPDF.Core/Commands/RibbonCatalog.g.cs", gen_catalog())
    write("docs/WSTAZKA.md", gen_md())
    if "--resx" in sys.argv:
        shell_extra_en = [("Shell.AppTitle", "ZetPDF"), ("Shell.NoDocument", "Open a PDF document (Ctrl+O) or drop a file here."), ("Shell.Status.Ready", "Ready"), ("Shell.Error.Unhandled", "An unexpected error occurred. Details were written to the log file:\n{0}")]
        shell_extra_pl = [("Shell.AppTitle", "ZetPDF"), ("Shell.NoDocument", "Otwórz dokument PDF (Ctrl+O) lub upuść plik tutaj."), ("Shell.Status.Ready", "Gotowy"), ("Shell.Error.Unhandled", "Wystąpił nieoczekiwany błąd. Szczegóły zapisano w pliku dziennika:\n{0}")]
        write("src/ZetPDF.App/Resources/Strings.resx", resx(shell_extra_en + gen_resx("en")))
        write("src/ZetPDF.App/Resources/Strings.pl.resx", resx(shell_extra_pl + gen_resx("pl")))
    print(f"{len(C)} poleceń OK")
