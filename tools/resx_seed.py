# -*- coding: utf-8 -*-
# Jednorazowe utworzenie plików .resx rdzenia i modułów (szkielet). Potem pliki należą do właścicieli modułów.
import os, html, sys
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))

def resx(entries):
    o = ['<?xml version="1.0" encoding="utf-8"?>', '<root>',
         '  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>',
         '  <resheader name="version"><value>2.0</value></resheader>',
         '  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>',
         '  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>']
    for k, v in entries:
        o.append(f'  <data name="{html.escape(k)}" xml:space="preserve"><value>{html.escape(v, quote=False)}</value></data>')
    o.append('</root>')
    return "\n".join(o) + "\n"

def write(project, entries):
    d = os.path.join(ROOT, "src", project, "Resources")
    os.makedirs(d, exist_ok=True)
    for lang, idx in (("", 1), (".pl", 2)):
        p = os.path.join(d, f"Strings{lang}.resx")
        if os.path.exists(p) and "--force" not in sys.argv:
            print("pomijam (istnieje)", p); continue
        with open(p, "w", encoding="utf-8", newline="\r\n") as f:
            f.write(resx([(e[0], e[idx]) for e in entries]))
        print("zapisano", p)

CORE = [
 ("Core.FileFilter.Pdf", "PDF documents (*.pdf)", "Dokumenty PDF (*.pdf)"),
 ("Core.FileFilter.All", "All files (*.*)", "Wszystkie pliki (*.*)"),
 ("Core.FileFilter.Images", "Images", "Obrazy"),
 ("Core.Error.Title", "Error", "Błąd"),
 ("Core.Error.Corrupt", "The file \"{0}\" is damaged or is not a PDF document.", "Plik „{0}” jest uszkodzony lub nie jest dokumentem PDF."),
 ("Core.Error.PasswordRequired", "This document requires a password.", "Ten dokument wymaga hasła."),
 ("Core.Error.InvalidPassword", "The password is incorrect.", "Hasło jest nieprawidłowe."),
 ("Core.Error.PermissionDenied", "This operation is not allowed by the document's security settings.", "Ta operacja jest niedozwolona przez ustawienia zabezpieczeń dokumentu."),
 ("Core.Error.FileAccess", "Cannot access the file \"{0}\": {1}", "Brak dostępu do pliku „{0}”: {1}"),
 ("Core.Error.Generic", "The operation failed: {0}", "Operacja nie powiodła się: {0}"),
 ("Core.Error.NotImplemented", "This feature is not available yet.", "Ta funkcja nie jest jeszcze dostępna."),
 ("Core.Password.Title", "Password required", "Wymagane hasło"),
 ("Core.Password.Prompt", "The document \"{0}\" is protected. Enter the password:", "Dokument „{0}” jest chroniony. Wpisz hasło:"),
 ("Core.Password.Retry", "Incorrect password. Try again:", "Nieprawidłowe hasło. Spróbuj ponownie:"),
 ("Core.Encryption.ResaveOwnerOnly", "The document was opened with the owner password and the original open password is unknown. When saving, the owner password will also be used as the open password. You can change this in Protect > Password Protection.", "Dokument otwarto hasłem właściciela, a pierwotne hasło otwierania jest nieznane. Przy zapisie hasło właściciela zostanie użyte także jako hasło otwierania. Możesz to zmienić w Ochrona > Ochrona hasłem."),
 ("Core.Undo.Prefix", "Undo: {0}", "Cofnij: {0}"),
 ("Core.Redo.Prefix", "Redo: {0}", "Ponów: {0}"),
 ("Core.Undo.SetEncryption", "Password protection", "Ochrona hasłem"),
 ("Core.Undo.RemoveEncryption", "Remove security", "Usunięcie zabezpieczeń"),
 ("Core.Undo.Metadata", "Document properties", "Właściwości dokumentu"),
 ("Core.Button.Ok", "OK", "OK"),
 ("Core.Button.Cancel", "Cancel", "Anuluj"),
 ("Core.Button.Yes", "Yes", "Tak"),
 ("Core.Button.No", "No", "Nie"),
 ("Core.Button.Close", "Close", "Zamknij"),
 ("Core.Button.Apply", "Apply", "Zastosuj"),
 ("Core.Button.Browse", "Browse…", "Przeglądaj…"),
 ("Core.Button.Add", "Add", "Dodaj"),
 ("Core.Button.Remove", "Remove", "Usuń"),
 ("Core.Button.Save", "Save", "Zapisz"),
 ("Core.Progress.Working", "Working…", "Trwa przetwarzanie…"),
 ("Core.Progress.Page", "Page {0} of {1}", "Strona {0} z {1}"),
 ("Core.Progress.File", "File {0} of {1}", "Plik {0} z {1}"),
 ("Core.PageRange.All", "All pages", "Wszystkie strony"),
 ("Core.PageRange.Current", "Current page", "Bieżąca strona"),
 ("Core.PageRange.Selected", "Selected pages", "Zaznaczone strony"),
 ("Core.PageRange.Custom", "Pages:", "Strony:"),
 ("Core.PageRange.Hint", "e.g. 1-3, 5, 8-", "np. 1-3, 5, 8-"),
 ("Core.PageRange.Invalid", "Invalid page range.", "Nieprawidłowy zakres stron."),
 ("Core.PageRange.Even", "Even pages", "Strony parzyste"),
 ("Core.PageRange.Odd", "Odd pages", "Strony nieparzyste"),
 ("Core.Language.pl", "Polski", "Polski"),
 ("Core.Language.en", "English", "English"),
 ("Core.Theme.System", "Use system setting", "Jak w systemie"),
 ("Core.Theme.Light", "Light", "Jasny"),
 ("Core.Theme.Dark", "Dark", "Ciemny"),
]
MODULES = {
 "ZetPDF.Rendering": [("Render.ModuleName", "Rendering", "Renderowanie")],
 "ZetPDF.Viewer": [("Viewer.ModuleName", "Document view", "Widok dokumentu")],
 "ZetPDF.Pages": [("Pages.ModuleName", "Pages", "Strony")],
 "ZetPDF.Annotations": [("Annot.ModuleName", "Comments and forms", "Komentarze i formularze")],
 "ZetPDF.Editing": [("Edit.ModuleName", "Editing and conversion", "Edycja i konwersja")],
 "ZetPDF.Ocr": [("Ocr.ModuleName", "Text recognition (OCR)", "Rozpoznawanie tekstu (OCR)")],
 "ZetPDF.Security": [("Security.ModuleName", "Security and signatures", "Zabezpieczenia i podpisy")],
}
if __name__ == "__main__":
    write("ZetPDF.Core", CORE)
    for p, e in MODULES.items():
        write(p, e)
