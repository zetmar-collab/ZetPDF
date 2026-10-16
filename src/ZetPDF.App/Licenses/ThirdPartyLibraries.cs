using System.Reflection;

namespace ZetPDF.App.Licenses;

/// <summary>
/// Biblioteki open source dołączone do ZetPDF (okna "O programie" i "Licencje bibliotek").
/// Źródło danych: pliki .nuspec pakietów i docs/LICENCJE.md. Teksty licencji – zasoby osadzone Licenses/*.txt
/// (dane prawne w oryginalnym brzmieniu angielskim, nie teksty interfejsu).
/// </summary>
public static class ThirdPartyLibraries
{
    public sealed record Library(string Name, string Version, string License, string Copyright, string Url, params string[] LicenseFiles)
    {
        /// <summary>Pełny tekst do wyświetlenia: nazwa, prawa autorskie, adres i treść licencji.</summary>
        public string FullText
        {
            get
            {
                var header = $"{Name} {Version}\n{Copyright}\n{Url}\n{License}\n\n";
                return header + string.Join("\n\n----------------------------------------------------------------\n\n", LicenseFiles.Select(ReadLicense));
            }
        }
    }

    public static IReadOnlyList<Library> All { get; } = new Library[]
    {
        new("PDFium", "chromium/8100", "BSD-3-Clause",
            "Copyright 2014 The PDFium Authors. Original code copyright 2014 Foxit Software Inc.",
            "https://pdfium.googlesource.com/pdfium/", "BSD-3-Clause.txt", "PDFium-ThirdParty.txt"),
        new("pdfium-binaries (bblanchon.PDFium.Win32)", "157.0.8100", "Apache-2.0",
            "Copyright © Benoît Blanchon 2017-2025", "https://github.com/bblanchon/pdfium-binaries", "Apache-2.0.txt"),
        new("PDFiumCore", "157.0.8100", "Apache-2.0",
            "Copyright © DJGosnell (Dtronix) and contributors", "https://github.com/Dtronix/PDFiumCore", "Apache-2.0.txt"),
        new("PDFsharp", "6.2.4", "MIT",
            "Copyright © empira Software GmbH (PDFsharp Team)", "https://docs.pdfsharp.net/", "MIT.txt"),
        new("PdfPig", "0.1.16", "Apache-2.0",
            "Copyright © UglyToad and PdfPig contributors", "https://github.com/UglyToad/PdfPig", "Apache-2.0.txt"),
        new("Tesseract (.NET wrapper)", "5.2.0", "Apache-2.0",
            "Copyright 2012-2020 Charles Weld", "https://github.com/charlesw/tesseract/", "Apache-2.0.txt"),
        new("Tesseract OCR", "5", "Apache-2.0",
            "Copyright © Google Inc. and Tesseract OCR contributors", "https://github.com/tesseract-ocr/tesseract", "Apache-2.0.txt"),
        new("tessdata_fast (pol, eng)", "main (2026-10-16)", "Apache-2.0",
            "Copyright © Google Inc. and Tesseract OCR contributors", "https://github.com/tesseract-ocr/tessdata_fast", "Apache-2.0.txt"),
        new("Leptonica", "1.82.0", "BSD-2-Clause",
            "Copyright © 2001-2022 Leptonica (Dan S. Bloomberg)", "http://www.leptonica.org/", "BSD-2-Clause.txt"),
        new("Fluent.Ribbon", "11.0.2", "MIT",
            "Copyright (c) 2012-2025 Bastian Schmidt; Copyright (c) 2009-2012 Degtyarev Daniel, Rikker Serg", "https://github.com/fluentribbon/Fluent.Ribbon", "MIT.txt"),
        new("ControlzEx", "7.0.3", "MIT",
            "Copyright © 2015 - 2026 Jan Karger, Bastian Schmidt, James Willock", "https://github.com/ControlzEx/ControlzEx", "MIT.txt"),
        new("Microsoft.Xaml.Behaviors.Wpf", "1.1.135", "MIT",
            "© Microsoft Corporation. All rights reserved.", "https://github.com/Microsoft/XamlBehaviorsWpf", "MIT.txt"),
        new("CommunityToolkit.Mvvm", "8.4.2", "MIT",
            "Copyright (c) .NET Foundation and Contributors", "https://github.com/CommunityToolkit/dotnet", "MIT.txt"),
        new("Microsoft.Extensions.DependencyInjection / Logging", "8.0", "MIT",
            "Copyright (c) .NET Foundation and Contributors", "https://github.com/dotnet/runtime", "MIT.txt"),
        new("System.Security.Cryptography.Pkcs / ProtectedData", "8.0", "MIT",
            "Copyright (c) .NET Foundation and Contributors", "https://github.com/dotnet/runtime", "MIT.txt"),
        new("DocumentFormat.OpenXml", "3.5.1", "MIT",
            "© Microsoft Corporation. All rights reserved.", "https://github.com/dotnet/Open-XML-SDK", "MIT.txt"),
        new("BouncyCastle.Cryptography", "2.6.2", "MIT",
            "Copyright © Legion of the Bouncy Castle Inc. 2000-2025", "https://www.bouncycastle.org/", "MIT.txt"),
        new(".NET 8 Runtime / WPF", "8.0", "MIT",
            "Copyright (c) .NET Foundation and Contributors", "https://github.com/dotnet/wpf", "MIT.txt"),
    };

    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tekst licencji z zasobu osadzonego (Licenses/&lt;plik&gt;).</summary>
    public static string ReadLicense(string file)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(file, out var cached)) return cached;
            var asm = typeof(ThirdPartyLibraries).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + file, StringComparison.OrdinalIgnoreCase));
            string text;
            if (name is null) text = file;
            else
            {
                using var s = asm.GetManifestResourceStream(name)!;
                using var r = new StreamReader(s);
                text = r.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
            }
            Cache[file] = text;
            return text;
        }
    }

    /// <summary>Wersja aplikacji do wyświetlenia ("0.1.0-test").</summary>
    public static string AppVersion =>
        typeof(ThirdPartyLibraries).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0-test";
}
