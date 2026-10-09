using System.Text;

namespace Myra.Core;

/// UI-independent helpers for the AI workspace (title picker, recap controls, Ask button, result cards).
public static class AiWorkspace
{
    public const int MaximumSelection = 10;
    public const long SubtitleSizeLimit = 2_000_000;
    public static readonly IReadOnlyList<string> SubtitleExtensions = ["srt", "ass", "ssa", "vtt", "txt"];

    /// True when the Ask button can run: provider and feature enabled, a catalogue exists, the selection
    /// fits the feature, and a recap has an episode and subtitles.
    public static bool CanAsk(AiService ai, AiFeature feature, int catalogueCount, int selectedCount, string? recapVersionId, bool hasSubtitles)
    {
        var settings = ai.Settings;
        if (ai.IsBusy || ai.CurrentProvider is null || !settings.IsEnabled(feature) || catalogueCount == 0) return false;
        if (feature.RequiresTitles() && selectedCount == 0) return false;
        if (feature == AiFeature.Compare && selectedCount < 2) return false;
        if (feature == AiFeature.Recap && (string.IsNullOrEmpty(recapVersionId) || !hasSubtitles)) return false;
        return true;
    }

    /// True when the request sends data to a cloud provider (show "may incur charges").
    public static bool IsCloud(AiService ai) => ai.CurrentProvider?.IsCloud == true;

    /// Title picker: display-name match, at most limit results in catalogue order.
    public static List<EntertainmentTitle> MatchingTitles(IReadOnlyList<EntertainmentTitle> catalogue, string query, int limit = 60) =>
        catalogue.Where(t => query.Length == 0 || t.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Take(limit).ToList();

    /// Episodes of a series that count as completed. These are the only episodes a recap may use, and
    /// AiService accepts exactly these (same rule: IsCompletedEpisode).
    public static List<EntertainmentVersion> CompletedEpisodes(EntertainmentTitle title, EntertainmentPersonalData personal)
    {
        if (title.Kind != EntertainmentKind.Series) return [];
        return title.Versions
            .Where(v => IsCompletedEpisode(title, v, personal))
            .OrderBy(v => v.Season ?? 0).ThenBy(v => v.Episode ?? 0).ToList();
    }

    /// The macOS store rule: the file has a season and an episode number, and the title is marked watched
    /// or the file's saved progress is within 10 s of its end.
    public static bool IsCompletedEpisode(EntertainmentTitle title, EntertainmentVersion version, EntertainmentPersonalData personal) =>
        version is { Season: not null, Episode: not null }
        && (personal.Watched.Contains(title.Id)
            || (version.Duration > 0 && version.ProgressSeconds >= version.Duration - 10)
            || (personal.History.TryGetValue(version.Id, out var record) && record.Duration > 0 && record.Seconds >= record.Duration - 10));

    /// Adds the plan budget and recap episode to the user's text, as macOS does before ExecuteAsync.
    public static string ComposePrompt(AiFeature feature, string prompt, int? availableMinutes = null, EntertainmentVersion? recapEpisode = null, string? subtitleName = null)
    {
        var request = prompt;
        if (feature == AiFeature.Plan && availableMinutes is { } minutes) request += $"\nAvailable time: {minutes} minutes.";
        if (feature == AiFeature.Recap && recapEpisode is not null)
            request += $"\nRecap only Season {recapEpisode.Season ?? 0}, Episode {recapEpisode.Episode ?? 0}. Source: {subtitleName}.";
        return request;
    }

    /// Keeps a timed-plan recommendation attached to its validated file: a copy of the title with only
    /// that version, so Play starts the planned episode. Returns the title unchanged without a version.
    public static EntertainmentTitle PlaybackTitle(EntertainmentTitle title, string? versionId) =>
        versionId is not null && title.Versions.FirstOrDefault(v => v.Id == versionId) is { } version
            ? title with { Versions = [version] }
            : title;

    /// Reads a user-chosen subtitle file for one recap request. Accepts SRT/ASS/SSA/VTT/TXT up to 2 MB
    /// in UTF-8 or UTF-16. The text is never stored. Throws AiException(MissingContext) with a user message.
    public static string ReadSubtitleFile(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (!SubtitleExtensions.Contains(extension))
            throw new AiException(AiErrorKind.MissingContext, "Choose an SRT, ASS, SSA, VTT, or text subtitle file.");
        byte[] data;
        try
        {
            if (new FileInfo(path).Length > SubtitleSizeLimit)
                throw new AiException(AiErrorKind.MissingContext, "Choose a subtitle file smaller than 2 MB.");
            data = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new AiException(AiErrorKind.MissingContext, error.Message, inner: error);
        }
        return DecodeSubtitle(data) is { Length: > 0 } text
            ? text
            : throw new AiException(AiErrorKind.MissingContext, "The subtitle file must contain readable UTF-8 or UTF-16 text.");
    }

    internal static string? DecodeSubtitle(byte[] data)
    {
        if (data.Length > SubtitleSizeLimit) return null;
        try
        {
            if (data is [0xFF, 0xFE, ..]) return new UnicodeEncoding(false, true, true).GetString(data, 2, data.Length - 2);
            if (data is [0xFE, 0xFF, ..]) return new UnicodeEncoding(true, true, true).GetString(data, 2, data.Length - 2);
            var offset = data is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
            return new UTF8Encoding(false, true).GetString(data, offset, data.Length - offset);
        }
        catch (DecoderFallbackException)
        {
        }
        try
        {
            return data.Length % 2 == 0 ? new UnicodeEncoding(false, false, true).GetString(data) : null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
