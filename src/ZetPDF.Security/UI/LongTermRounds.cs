using ZetPDF.Core.Contracts;

namespace ZetPDF.Security.UI;

/// <summary>
/// Rundy połączeń: adres odkryty dopiero w pobranym certyfikacie nie jest wywoływany, dopóki użytkownik go nie zobaczy i nie zatwierdzi w kolejnym oknie.
/// Liczba rund jest ograniczona, bo spreparowany certyfikat mógłby wskazywać kolejne adresy bez końca.
/// </summary>
internal static class LongTermRounds
{
    public const int MaxRounds = 3;
    public const int MaxListedUrls = 32;

    public static Task<LongTermResult?> RunAsync(IDialogService dialogs, ILocalizationService loc, LongTermConnectionPlan plan, Func<LongTermConnectionPlan, CancellationToken, Task<LongTermResult>> work) =>
        RunAsync(plan,
            async step =>
            {
                LongTermResult? result = null;
                return await dialogs.RunWithProgressAsync(loc["Security.LongTerm.Title"], async (_, ct) => result = await step(ct)) ? result : null;
            },
            (pending, allowLocal) => LongTermConfirmDialog.Ask(dialogs, loc, loc.Format("Security.LongTerm.ConfirmMore", string.Join(Environment.NewLine, pending)), allowLocal),
            work);

    /// <summary>Null po anulowaniu. Wynik z niepustym <see cref="LongTermResult.PendingUrls"/>: tych adresów nie wywołano.</summary>
    public static async Task<LongTermResult?> RunAsync(LongTermConnectionPlan plan,
        Func<Func<CancellationToken, Task<LongTermResult>>, Task<LongTermResult?>> runStep,
        Func<IReadOnlyList<string>, bool, bool?> confirm,
        Func<LongTermConnectionPlan, CancellationToken, Task<LongTermResult>> work)
    {
        var approved = new HashSet<string>(plan.Approved, StringComparer.Ordinal);
        bool allowLocal = plan.AllowLocalNetwork;
        for (int round = 1; ; round++)
        {
            var current = plan with { Approved = approved.ToList(), AllowLocalNetwork = allowLocal };
            var result = await runStep(ct => work(current, ct));
            if (result is null) return null;
            if (result.PendingUrls.Count == 0 || round >= MaxRounds) return result;
            var pending = result.PendingUrls.Take(MaxListedUrls).ToList();
            if (confirm(pending, allowLocal) is not { } allow) return result;
            allowLocal = allow;
            approved.UnionWith(pending);
        }
    }

    /// <summary>Tekst wyniku, także adresów, z którymi się nie połączono.</summary>
    public static string Describe(ILocalizationService loc, LongTermResult r)
    {
        var text = loc.Format("Security.LongTerm.Result", r.Certificates, r.OcspResponses, r.Crls, loc["Security.Validate.Detail.Revocation." + r.Status]);
        foreach (var s in r.Signatures) text += Environment.NewLine + s.FieldName + ": " + loc["Security.Validate.Detail.Revocation." + s.Status];
        if (r.PendingUrls.Count > 0) text += Environment.NewLine + Environment.NewLine + loc["Security.LongTerm.NotContacted"] + Environment.NewLine + string.Join(Environment.NewLine, r.PendingUrls.Take(MaxListedUrls));
        if (r.Problems.Count > 0) text += Environment.NewLine + Environment.NewLine + loc["Security.LongTerm.Problems"] + Environment.NewLine + string.Join(Environment.NewLine, r.Problems.Take(16));
        return text;
    }
}
