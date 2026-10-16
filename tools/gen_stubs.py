# -*- coding: utf-8 -*-
"""Jednorazowo generuje klasy-zaślepki (NotImplementedException) implementujące kontrakty w modułach.
Po wygenerowaniu pliki należą do właścicieli modułów (zastępują zaślepki implementacją)."""
import os, re, sys
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
CONTRACTS = os.path.join(ROOT, "src", "ZetPDF.Core", "Contracts")

src = ""
for f in os.listdir(CONTRACTS):
    src += open(os.path.join(CONTRACTS, f), encoding="utf-8").read() + "\n"

def members(iface):
    m = re.search(r"public interface " + iface + r"\b[^{]*\{(.*?)\n\}", src, re.S)
    if not m:
        raise SystemExit("brak " + iface)
    body = m.group(1)
    out = []
    for line in body.splitlines():
        s = line.strip()
        if not s or s.startswith("//") or s.startswith("///") or s.startswith("["):
            continue
        out.append(s)
    return out

def stub_member(s):
    if s.startswith("event "):
        decl = s[:-1]
        return f"    public {decl} {{ add {{ }} remove {{ }} }}"
    if "{ get; set; }" in s:
        decl = s.replace("{ get; set; }", "").strip()
        return f"    public {decl} {{ get => throw new NotImplementedException(); set => throw new NotImplementedException(); }}"
    if "{ get; }" in s:
        decl = s.replace("{ get; }", "").strip()
        return f"    public {decl} => throw new NotImplementedException();"
    if s.endswith(";") and "(" in s:
        return f"    public {s[:-1]} => throw new NotImplementedException();"
    raise SystemExit("nie rozumiem: " + s)

def gen(project, ns, cls, ifaces, usings=()):
    lines = ["using ZetPDF.Core.Contracts;"] + [f"using {u};" for u in usings] + ["", f"namespace {ns};", "",
             f"/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>",
             f"public sealed class {cls} : {', '.join(ifaces)}", "{"]
    seen = set()
    for i in ifaces:
        lines.append(f"    // ---- {i}")
        for s in members(i):
            st = stub_member(s)
            if st in seen:
                continue
            seen.add(st)
            lines.append(st)
    lines.append("}")
    path = os.path.join(ROOT, "src", project, cls + ".cs")
    if os.path.exists(path) and "--force" not in sys.argv:
        print("pomijam (istnieje)", path); return
    with open(path, "w", encoding="utf-8", newline="\r\n") as f:
        f.write("\n".join(lines) + "\n")
    print("zapisano", path)

SEC_USINGS = ("System.Security.Cryptography.X509Certificates",)
gen("ZetPDF.Rendering", "ZetPDF.Rendering", "PdfiumRenderer", ["IPdfRenderer"])
gen("ZetPDF.Viewer", "ZetPDF.Viewer", "BookmarkService", ["IBookmarkService"])
gen("ZetPDF.Viewer", "ZetPDF.Viewer", "PrintService", ["IPrintService"])
gen("ZetPDF.Pages", "ZetPDF.Pages", "PageOperations", ["IPageOperations"])
gen("ZetPDF.Pages", "ZetPDF.Pages", "StampingService", ["IStampingService"])
gen("ZetPDF.Pages", "ZetPDF.Pages", "OptimizerService", ["IOptimizerService"])
gen("ZetPDF.Pages", "ZetPDF.Pages", "SanitizeService", ["ISanitizeService"])
gen("ZetPDF.Annotations", "ZetPDF.Annotations", "AnnotationService", ["IAnnotationService"])
gen("ZetPDF.Annotations", "ZetPDF.Annotations", "FormService", ["IFormService"])
gen("ZetPDF.Annotations", "ZetPDF.Annotations", "AttachmentService", ["IAttachmentService"])
gen("ZetPDF.Editing", "ZetPDF.Editing", "TextEditService", ["ITextEditService"])
gen("ZetPDF.Editing", "ZetPDF.Editing", "ObjectEditService", ["IObjectEditService"])
gen("ZetPDF.Editing", "ZetPDF.Editing", "LinkService", ["ILinkService"])
gen("ZetPDF.Editing", "ZetPDF.Editing", "ConversionService", ["IConversionService"])
gen("ZetPDF.Editing", "ZetPDF.Editing", "RedactionService", ["IRedactionService"])
gen("ZetPDF.Ocr", "ZetPDF.Ocr", "TesseractOcrService", ["IOcrService"])
gen("ZetPDF.Security", "ZetPDF.Security", "SecurityService", ["ISecurityService"])
gen("ZetPDF.Security", "ZetPDF.Security", "SignatureService", ["ISignatureService"], SEC_USINGS)
gen("ZetPDF.Security", "ZetPDF.Security", "CompareService", ["ICompareService"])
gen("ZetPDF.Security", "ZetPDF.Security", "ActionWizard", ["IActionWizard"])
