using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Security.Actions;
using ZetPDF.Core.Localization;

namespace ZetPDF.Security;

/// <summary>
/// Kreator działań: nazwane sekwencje kroków zapisywane jako JSON w IAppPaths.ActionsDirectory, uruchamiane na
/// wielu plikach z folderem wyjściowym. Kroki wołają inne moduły WYŁĄCZNIE przez kontrakty z DI
/// (ISanitizeService, IStampingService, IOptimizerService, IPageOperations, IOcrService, ISecurityService, IMetadataService, IPdfAService).
/// Oryginały nie są nadpisywane, chyba że <see cref="ActionRunOptions.OverwriteExisting"/> (zgoda w oknie).
/// </summary>
public sealed class ActionWizard : IActionWizard
{
    public const string DistributeId = "builtin.distribute";
    public const string WebId = "builtin.web";
    public const string ArchiveId = "builtin.archive";

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IServiceProvider _services;
    private readonly IAppPaths _paths;

    public ActionWizard(IServiceProvider services, IAppPaths paths)
    {
        _services = services;
        _paths = paths;
    }

    private CultureInfo Culture => _services.GetService<ILocalizationService>()?.CurrentCulture ?? CultureInfo.CurrentUICulture;
    private string Txt(string key) => SecurityModule.Strings.GetString(key, Culture) ?? key;

    public IReadOnlyDictionary<string, string> StepTypes { get; } =
        ActionStepTypes.OptionTypes.Keys.ToDictionary(k => k, k => "Security.Action.Step." + k);

    // ------------------------------------------------------------------ definicje

    public static ActionStep Step<T>(string type, T options) => new(type, JsonSerializer.SerializeToElement(options, Json));

    public static object? ReadOptions(ActionStep step)
    {
        if (!ActionStepTypes.OptionTypes.TryGetValue(step.StepType, out var type)) return null;
        if (step.Options.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return Activator.CreateInstance(type);
        return step.Options.Deserialize(type, Json) ?? Activator.CreateInstance(type);
    }

    public static T ReadOptions<T>(ActionStep step) where T : new() =>
        step.Options.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? new T() : step.Options.Deserialize<T>(Json) ?? new T();

    internal IReadOnlyList<ActionDefinition> BuiltIns()
    {
        return new[]
        {
            new ActionDefinition
            {
                Id = DistributeId, Name = "Security.Action.Distribute", Description = "Security.Action.Distribute.Description", IsBuiltIn = true,
                Steps = new[]
                {
                    Step(ActionStepTypes.Sanitize, new SanitizeOptions()),
                    Step(ActionStepTypes.Watermark, new WatermarkOptions { Text = Txt("Security.Action.Distribute.WatermarkText") }),
                    Step(ActionStepTypes.HeaderFooter, new HeaderFooterOptions { HeaderLeft = "<<file>>", FooterCenter = "<<page>> / <<pages>>", FooterRight = "<<date>>" }),
                },
            },
            new ActionDefinition
            {
                Id = WebId, Name = "Security.Action.Web", Description = "Security.Action.Web.Description", IsBuiltIn = true,
                Steps = new[]
                {
                    Step(ActionStepTypes.Optimize, new OptimizeOptions { RecompressImages = true, JpegQuality = 70, MaxImageDpi = 150, RemoveUnusedObjects = true, CompressStreams = true, RemoveEmbeddedThumbnails = true }),
                },
            },
            new ActionDefinition
            {
                Id = ArchiveId, Name = "Security.Action.Archive", Description = "Security.Action.Archive.Description", IsBuiltIn = true,
                Steps = new[]
                {
                    Step(ActionStepTypes.Metadata, new MetadataStepOptions()),
                    Step(ActionStepTypes.Sanitize, new SanitizeOptions
                    {
                        Metadata = false, Comments = false, Attachments = false, JavaScript = true,
                        HiddenLayers = false, FormData = false, ExternalLinks = false, Thumbnails = false,
                    }),
                    Step(ActionStepTypes.PdfA, new PdfAStepOptions()),
                },
            },
        };
    }

    public Task<IReadOnlyList<ActionDefinition>> GetActionsAsync(CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<ActionDefinition>>(() =>
        {
            var list = new List<ActionDefinition>(BuiltIns());
            if (Directory.Exists(_paths.ActionsDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(_paths.ActionsDirectory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var def = JsonSerializer.Deserialize<ActionDefinition>(File.ReadAllText(file), Json);
                        if (def is not null && !def.IsBuiltIn && list.All(a => a.Id != def.Id)) list.Add(def);
                    }
                    catch (JsonException) { /* uszkodzony plik – pomijamy */ }
                    catch (IOException) { }
                }
            }
            return list;
        }, ct);
    }

    public async Task SaveActionAsync(ActionDefinition action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.IsBuiltIn || action.Id.StartsWith("builtin.", StringComparison.Ordinal))
            throw new InvalidOperationException(Txt("Security.Action.Error.BuiltInReadOnly"));
        var safe = action with { Steps = action.Steps.Select(StripSecrets).ToArray() };
        Directory.CreateDirectory(_paths.ActionsDirectory);
        var path = FilePath(action.Id);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(safe, Json), ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Hasła kroku „Szyfruj” nigdy nie trafiają na dysk.</summary>
    internal static ActionStep StripSecrets(ActionStep step)
    {
        if (step.StepType != ActionStepTypes.Encrypt) return step;
        var o = ReadOptions<EncryptStepOptions>(step) with { UserPassword = null, OwnerPassword = null };
        return Step(step.StepType, o);
    }

    public Task DeleteActionAsync(string actionId, CancellationToken ct = default)
    {
        if (actionId.StartsWith("builtin.", StringComparison.Ordinal)) throw new InvalidOperationException(Txt("Security.Action.Error.BuiltInReadOnly"));
        var path = FilePath(actionId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string FilePath(string id)
    {
        var safe = string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
        return Path.Combine(_paths.ActionsDirectory, safe + ".json");
    }

    public static string NewId() => "user." + Guid.NewGuid().ToString("N")[..12];

    // ------------------------------------------------------------------ uruchamianie

    public async Task<ActionRunReport> RunAsync(ActionDefinition action, IReadOnlyList<string> inputFiles, ActionRunOptions options,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(inputFiles);
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.OutputDirectory);
        var results = new List<ActionFileResult>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < inputFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var input = Path.GetFullPath(inputFiles[i]);
            var name = Path.GetFileName(input);
            progress?.Report(new OperationProgress((double)i / Math.Max(1, inputFiles.Count), name, i + 1, inputFiles.Count));
            try
            {
                var bytes = await PdfFileProbes.ReadVerifiedAsync(input, null, ct).ConfigureAwait(false);
                if (PdfSecurityHelper.IsEncrypted(bytes)) throw new ActionStepException(Txt("Security.Action.Error.Encrypted"));
                int s = 0;
                string? warning = null;
                foreach (var step in action.Steps)
                {
                    ct.ThrowIfCancellationRequested();
                    var stepProgress = progress is null ? null : new Progress<OperationProgress>(p =>
                        progress.Report(new OperationProgress((i + (s + (p.Fraction ?? 0)) / Math.Max(1, action.Steps.Count)) / Math.Max(1, inputFiles.Count), name, i + 1, inputFiles.Count)));
                    if (step.StepType == ActionStepTypes.PdfA)
                    {
                        var (converted, note) = await ApplyPdfAAsync(step, bytes, stepProgress, ct).ConfigureAwait(false);
                        bytes = converted;
                        warning = note ?? warning;
                    }
                    else bytes = await ApplyStepAsync(step, bytes, Path.GetFileNameWithoutExtension(input), stepProgress, ct).ConfigureAwait(false);
                    s++;
                }
                var output = ResolveOutput(input, options, reserved);
                await WriteSafeAsync(output, bytes, ct).ConfigureAwait(false);
                results.Add(new ActionFileResult(input, output, true, null, warning));
            }
            catch (OperationCanceledException) { throw; }
            catch (NotImplementedException)
            {
                results.Add(new ActionFileResult(input, null, false, Txt("Security.Action.Error.NotAvailable")));
            }
            catch (Exception ex)
            {
                results.Add(new ActionFileResult(input, null, false, _services.GetService<ILocalizationService>() is { } loc ? loc.Describe(ex, name) : ex.Message));
            }
        }
        progress?.Report(new OperationProgress(1, null, inputFiles.Count, inputFiles.Count));
        return new ActionRunReport(results);
    }

    /// <summary>Konwersja przez kontrakt z DI. Gdy się nie da, bajty zostają bez zmian, a plik nie udaje PDF/A: powód trafia do raportu.</summary>
    private async Task<(byte[] Pdf, string? Warning)> ApplyPdfAAsync(ActionStep step, byte[] pdf, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        if (_services.GetService<IPdfAService>() is not { } service) return (pdf, Txt("Security.Action.PdfA.NotAvailable"));
        var o = ReadOptions<PdfAStepOptions>(step);
        var result = await service.ConvertAsync(pdf, new PdfAConvertOptions { AllowRasterize = o.AllowRasterize, AllowContentRemoval = o.AllowContentRemoval }, progress, ct).ConfigureAwait(false);
        if (result.Pdf is { } converted) return (converted, null);
        return (pdf, Txt(result.Reason switch
        {
            PdfANotConverted.NeedsRasterize => "Security.Action.PdfA.NeedsRasterize",
            PdfANotConverted.RemovesContent => "Security.Action.PdfA.RemovesContent",
            _ => "Security.Action.PdfA.RulesFailing",
        }));
    }

    /// <summary>Ścieżka wyniku: folder wyjściowy + nazwa + przyrostek; nigdy oryginał (chyba że zgoda) ani istniejący plik.</summary>
    internal static string ResolveOutput(string input, ActionRunOptions options, HashSet<string> reserved)
    {
        var dir = Path.GetFullPath(options.OutputDirectory);
        var baseName = Path.GetFileNameWithoutExtension(input) + options.FileNameSuffix;
        var candidate = Path.Combine(dir, baseName + ".pdf");
        bool IsOriginal(string p) => string.Equals(p, input, StringComparison.OrdinalIgnoreCase);
        bool Taken(string p) => reserved.Contains(p) || (!options.OverwriteExisting && (File.Exists(p) || IsOriginal(p)));
        for (int n = 1; Taken(candidate); n++) candidate = Path.Combine(dir, $"{baseName} ({n}).pdf");
        reserved.Add(candidate);
        return candidate;
    }

    private async Task WriteSafeAsync(string path, byte[] data, CancellationToken ct)
    {
        var files = _services.GetService<IFileService>();
        if (files is not null)
        {
            await files.WriteAllBytesSafeAsync(path, data, keepBackup: false, ct).ConfigureAwait(false);
            return;
        }
        var tmp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    private T Require<T>() where T : class =>
        _services.GetService<T>() ?? throw new ActionStepException(Txt("Security.Action.Error.NotAvailable"));

    internal async Task<byte[]> ApplyStepAsync(ActionStep step, byte[] pdf, string fileName, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        switch (step.StepType)
        {
            case ActionStepTypes.Sanitize:
                return (await Require<ISanitizeService>().SanitizeAsync(pdf, ReadOptions<SanitizeOptions>(step), progress, ct).ConfigureAwait(false)).Pdf;
            case ActionStepTypes.Watermark:
                return await Require<IStampingService>().AddWatermarkAsync(pdf, ReadOptions<WatermarkOptions>(step), progress, ct).ConfigureAwait(false);
            case ActionStepTypes.HeaderFooter:
                {
                    var o = ReadOptions<HeaderFooterOptions>(step);
                    string F(string s) => s.Replace("<<file>>", fileName, StringComparison.OrdinalIgnoreCase).Replace("<<plik>>", fileName, StringComparison.OrdinalIgnoreCase);
                    o = o with
                    {
                        HeaderLeft = F(o.HeaderLeft), HeaderCenter = F(o.HeaderCenter), HeaderRight = F(o.HeaderRight),
                        FooterLeft = F(o.FooterLeft), FooterCenter = F(o.FooterCenter), FooterRight = F(o.FooterRight),
                    };
                    return await Require<IStampingService>().AddHeaderFooterAsync(pdf, o, progress, ct).ConfigureAwait(false);
                }
            case ActionStepTypes.PageNumbers:
                return await Require<IStampingService>().AddPageNumbersAsync(pdf, ReadOptions<PageNumberOptions>(step), ct).ConfigureAwait(false);
            case ActionStepTypes.Optimize:
                return (await Require<IOptimizerService>().OptimizeAsync(pdf, ReadOptions<OptimizeOptions>(step), progress, ct).ConfigureAwait(false)).Pdf;
            case ActionStepTypes.Flatten:
                return await Require<IPageOperations>().FlattenAsync(pdf, ReadOptions<FlattenOptions>(step), progress, ct).ConfigureAwait(false);
            case ActionStepTypes.Ocr:
                return await Require<IOcrService>().OcrDocumentAsync(pdf, ReadOptions<OcrOptions>(step) with { Pages = null }, progress, ct).ConfigureAwait(false);
            case ActionStepTypes.Encrypt:
                {
                    var o = ReadOptions<EncryptStepOptions>(step);
                    if (string.IsNullOrEmpty(o.OwnerPassword) && string.IsNullOrEmpty(o.UserPassword))
                        throw new ActionStepException(Txt("Security.Action.Error.NoPassword"));
                    var owner = string.IsNullOrEmpty(o.OwnerPassword) ? o.UserPassword! : o.OwnerPassword;
                    return await Require<ISecurityService>().EncryptAsync(pdf, new PdfEncryptionSettings(o.UserPassword, owner, o.Method, o.Permissions), ct).ConfigureAwait(false);
                }
            case ActionStepTypes.Metadata:
                {
                    var o = ReadOptions<MetadataStepOptions>(step);
                    var meta = Require<IMetadataService>();
                    var cur = await meta.GetAsync(pdf, ct).ConfigureAwait(false);
                    string Pick(string current, string wanted)
                    {
                        wanted = wanted.Replace("{file}", fileName, StringComparison.OrdinalIgnoreCase).Replace("{plik}", fileName, StringComparison.OrdinalIgnoreCase);
                        if (wanted.Length == 0) return current;
                        return o.OnlyIfEmpty && current.Length > 0 ? current : wanted;
                    }
                    var updated = cur with
                    {
                        Title = Pick(cur.Title, o.Title), Author = Pick(cur.Author, o.Author), Subject = Pick(cur.Subject, o.Subject),
                        Keywords = Pick(cur.Keywords, o.Keywords), Creator = Pick(cur.Creator, o.Creator),
                    };
                    return updated == cur ? pdf : await meta.SetAsync(pdf, updated, ct).ConfigureAwait(false);
                }
            default:
                throw new ActionStepException(string.Format(Culture, Txt("Security.Action.Error.UnknownStep"), step.StepType));
        }
    }

    /// <summary>Czy działanie ma krok wymagający podania hasła przed uruchomieniem.</summary>
    public static bool NeedsPasswords(ActionDefinition action) => action.Steps.Any(s => s.StepType == ActionStepTypes.Encrypt);

    /// <summary>Kopia działania z hasłami wstawionymi do kroków „Szyfruj” (tylko w pamięci, na czas uruchomienia).</summary>
    public static ActionDefinition WithPasswords(ActionDefinition action, string? user, string? owner) => action with
    {
        Steps = action.Steps.Select(s => s.StepType != ActionStepTypes.Encrypt ? s
            : Step(s.StepType, ReadOptions<EncryptStepOptions>(s) with { UserPassword = user, OwnerPassword = owner })).ToArray(),
    };
}

/// <summary>Błąd kroku działania (komunikat już przetłumaczony).</summary>
public sealed class ActionStepException : Exception
{
    public ActionStepException(string message) : base(message) => this.MarkLocalized();
}
