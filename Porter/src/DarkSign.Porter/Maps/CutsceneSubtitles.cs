using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Subtitles for the converted DS3 cutscenes: the words a character says show at the bottom of the screen, as in
/// talks with people. Both games put a cutscene's subtitles on its timeline the same way (resource kind 27,
/// parameters talkParamId and dmypolyId; cutscene-survey 2026-11-12), and Elden Ring's TalkParam row names the
/// text (msgId, TalkMsg) and the voice (voiceId). Measured on Elden Ring's own files (2026-11-20): s10_00_0010's
/// subtitle talk 2003000 is a TalkParam row with msgId and voiceId 20030000, and the game's sound index lists
/// Play_v020030000 in that cutscene's own bank (sd\s10_00_0010.bnk). Up to 1.0.549 the port turned each DS3
/// subtitle into a sound event that played the voice and dropped the words.
/// <list type="bullet">
/// <item>Each subtitle event keeps its place on the timeline and points at a TalkParam row of the port's
/// (<see cref="RowBase"/> + the cutscene's number * 1000 + n), whose text is DS3's line (DS3's TalkParam msgId ->
/// DS3's TalkMsg text) under the same number in Elden Ring's TalkMsg.</item>
/// <item>The voice: <see cref="CutsceneSound"/> puts DS3's voice for the line in the cutscene's bank as Play_v&lt;its
/// new ID&gt;, and the row's voiceId names it, as Elden Ring's rows do. A line DS3 has no voice for (or whose voice
/// didn't decode) gets voiceId -1 and a reading time.</item>
/// <item>A subtitle with no DS3 text is left out, so the game never shows an Elden Ring line of that number.</item>
/// </list>
/// The talk port (<see cref="TalkPort"/>) owns the rows from <see cref="TalkPort.TextBase"/> up to RowBase; this
/// range is the cutscenes'.
/// </summary>
public static class CutsceneSubtitles
{
    /// <summary>The cutscenes' TalkParam rows and TalkMsg text: 1,100,000,000 + cutscene number * 1000 + n.</summary>
    public const int RowBase = TalkPort.TextBase + 100_000_000;
    public const int RowRange = 100_000_000;
    /// <summary>Elden Ring's cutscene subtitle row the port's rows copy (s10_00_0010's first line).</summary>
    public const int TemplateRow = 2003000;

    public static bool IsOurs(int id) => id >= RowBase && id < RowBase + RowRange;
    public static int RowId(int cutsceneIndex, int n) => RowBase + cutsceneIndex * CutsceneSound.MaxPerCutscene + n;

    /// <summary>One subtitle row: its number, the DS3 line, the voice (new sound ID, -1 none) and the words.</summary>
    public sealed record Line(int RowId, int Ds3Talk, int VoiceId, string Words);

    /// <summary>DS3's words for a DS3 talk number (its TalkParam row's msgId -> TalkMsg), or null.</summary>
    public static Func<int, string?> LoadText(GameArchives ds3, ParamStore? ds3Params)
    {
        // DS3 names its text files in Japanese: 会話 = conversation.
        var words = TalkPort.LoadDs3Text(ds3, "会話", "Conversation", "TalkMsg");
        var talkParam = ds3Params?.Get("TalkParam");
        return talk =>
        {
            int message = talk;
            if (talkParam is not null)
            {
                if (talkParam[talk] is not { } row)
                    return null;
                message = PeopleArmour.Cell(row, "msgId");
                if (message <= 0)
                    message = PeopleArmour.Cell(row, "PcGenderFemale1");
            }
            return message > 0 && words.TryGetValue(message, out var w) ? w : null;
        };
    }

    /// <summary>
    /// Points every subtitle event of a DS3 timeline at the port's row for its line (one row per line per cutscene)
    /// and leaves out the ones with no DS3 words. <paramref name="voiceOfTalk"/> gives the voice's new ID for a talk.
    /// Returns the rows the cutscene needs.
    /// </summary>
    public static List<Line> Prepare(MQB mqb, int cutsceneIndex, Func<int, string?> textOf, IReadOnlyDictionary<int, int>? voiceOfTalk, out int dropped)
    {
        var rows = new Dictionary<int, Line>();
        int left = 0;
        foreach (var cut in mqb.Cuts)
            foreach (var tl in cut.Timelines)
                left += tl.Events.RemoveAll(ev =>
                {
                    if (ev.ResourceIndex < 0 || ev.ResourceIndex >= mqb.Resources.Count || mqb.Resources[ev.ResourceIndex].Unk48 != CutsceneSound.KindSubtitle)
                        return false;
                    var p = ev.Parameters.FirstOrDefault(x => x.Name == "talkParamId");
                    if (p?.Value is not int talk || talk <= 0 || textOf(talk) is not { } words)
                        return true;
                    if (!rows.TryGetValue(talk, out var line))
                    {
                        if (rows.Count >= CutsceneSound.MaxPerCutscene)
                            return true;
                        rows[talk] = line = new Line(RowId(cutsceneIndex, rows.Count), talk,
                            voiceOfTalk is not null && voiceOfTalk.TryGetValue(talk, out var v) ? v : -1, words);
                    }
                    p.Value = line.RowId;
                    return false;
                });
        dropped = left;
        return [.. rows.Values];
    }

    /// <summary>
    /// Writes the rows into the mod's TalkParam (copies of Elden Ring's <see cref="TemplateRow"/>) and their words into
    /// the DLC02 menu binder's TalkMsg. <paramref name="silent"/> are voices the banks couldn't hold (voiceId -1).
    /// </summary>
    public static string Write(PorterContext ctx, GameArchives er, ParamStore regulation, IReadOnlyList<Line> lines, IReadOnlySet<int> silent)
    {
        var talkParam = regulation.Get("TalkParam") ?? throw new InvalidDataException("Elden Ring has no TalkParam");
        talkParam.Rows.RemoveAll(r => IsOurs(r.ID));
        var template = talkParam[TemplateRow]
                       ?? talkParam.Rows.Where(r => r.ID < TalkPort.TextBase && PeopleArmour.Cell(r, "msgId") > 0 && PeopleArmour.Cell(r, "voiceId") > 0).OrderBy(r => r.ID).FirstOrDefault()
                       ?? throw new InvalidDataException("Elden Ring's TalkParam has no row with a line and a voice");

        var modMenuPath = ctx.ModPath(MapPorter.ModParts(ActionButtons.ModMenu));
        var (vanillaMenu, compression) = er.ReadWithCompression(ActionButtons.ModMenu);
        var menu = BND4.Read(File.Exists(modMenuPath) ? DCX.Decompress(File.ReadAllBytes(modMenuPath)) : vanillaMenu);
        var talkFile = menu.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).StartsWith("TalkMsg", StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidDataException($"{ItemText.Leaf(ActionButtons.ModMenu)} has no TalkMsg file");
        var talkMsg = FMG.Read(talkFile.Bytes);
        talkMsg.Entries.RemoveAll(e => IsOurs(e.ID));

        int voiced = 0, voiceless = 0;
        foreach (var line in lines.DistinctBy(l => l.RowId))
        {
            var row = new PARAM.Row(line.RowId, "", talkParam.AppliedParamdef);
            for (int i = 0; i < row.Cells.Count; i++)
                row.Cells[i].Value = template.Cells[i].Value is byte[] b ? b.Clone() : template.Cells[i].Value;
            int voice = line.VoiceId >= 0 && !silent.Contains(line.VoiceId) ? line.VoiceId : -1;
            PeopleArmour.SetCell(row, "msgId", line.RowId);
            PeopleArmour.SetCell(row, "msgId_female", line.RowId);
            PeopleArmour.SetCell(row, "voiceId", voice);
            PeopleArmour.SetCell(row, "voiceId_female", voice);
            PeopleArmour.SetCell(row, "disableParam_NT", 0);
            // Shown like the talk port's lines (the people's words show up for Devin), whatever the template says.
            PeopleArmour.SetCell(row, "isForceDisp", 1);
            if (row.Cells.FirstOrDefault(c => c.Def.InternalName == "timeout") is { } timeout)
                timeout.Value = voice >= 0 ? template.Cells.First(c => c.Def.InternalName == "timeout").Value : TalkPort.ReadingTime(line.Words);
            talkParam.Rows.Add(row);
            talkMsg.Entries.Add(new FMG.Entry(line.RowId, line.Words));
            if (voice >= 0) voiced++; else voiceless++;
        }
        talkParam.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        talkFile.Bytes = talkMsg.Write();
        Directory.CreateDirectory(Path.GetDirectoryName(modMenuPath)!);
        File.WriteAllBytes(modMenuPath, DCX.Compress(menu.Write(), compression));
        return $"subtitles: {voiced + voiceless} lines (TalkParam and TalkMsg {RowBase}+, copies of {template.ID}), {voiced} spoken from the cutscene's bank, {voiceless} without a voice";
    }
}
