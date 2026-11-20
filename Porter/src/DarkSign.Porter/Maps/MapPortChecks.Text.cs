using SoulsFormats;

namespace DarkSign.Porter.Maps;

// Offline checks for the text DS3's maps show by number (MapText): event messages and area names.
public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> TextChecks()
    {
        yield return ("map text: a script's dialog, status and message lines name DS3's event text at 1,000,000,000 + the DS3 number, written in the line or passed in", TextInScripts);
        yield return ("map text: an area welcome line and a collision's area name point at DS3's place name; DARKSIGN_MAP_TEXT turns each off", TextPlaces);
        yield return ("map text: a door's 'can't do that' message names DS3's event text and the prompt message is Elden Ring's", TextObjAct);
        yield return ("map text: DS3's text is merged into an Elden Ring text file, replacing an earlier run's and keeping Elden Ring's", TextMerge);
    }

    // DarkScript3's names and layouts (ds3-common / er-common) for the lines that show text.
    private const string Ds3TextDefs = """
    { "main_classes": [
      { "index": 2000, "instrs": [
        { "name": "Initialize Event", "index": 0, "args": [ { "name": "Event Slot ID", "type": 5 }, { "name": "Event ID", "type": 2 }, { "name": "Parameters", "type": 2 } ] } ] },
      { "index": 3, "instrs": [
        { "name": "IF Action Button", "index": 5, "args": [
          { "name": "Result Condition Group", "type": 3 }, { "name": "Target Entity Type", "type": 5 },
          { "name": "Target Entity ID", "type": 5 }, { "name": "Reaction Angle (deg)", "type": 6 },
          { "name": "Dummypoly ID", "type": 4 }, { "name": "Reaction Distance", "type": 6 },
          { "name": "Help Message ID", "type": 5 }, { "name": "Reaction Type", "type": 0 }, { "name": "Pad ID", "type": 5 } ] } ] },
      { "index": 2007, "instrs": [
        { "name": "Display Generic Dialog", "index": 1, "args": [
          { "name": "Message ID", "type": 5 }, { "name": "Dialog Type", "type": 4 }, { "name": "Number of Options", "type": 4 },
          { "name": "Entity ID", "type": 5 }, { "name": "Display Distance", "type": 6 } ] },
        { "name": "Display Status Message", "index": 3, "args": [ { "name": "Message ID", "type": 5 }, { "name": "Pad State", "type": 0 } ] },
        { "name": "Display Message", "index": 4, "args": [ { "name": "Message ID", "type": 5 }, { "name": "Screen Location Index", "type": 0 } ] },
        { "name": "Display Area Welcome Message", "index": 11, "args": [ { "name": "Area Name Message ID", "type": 5 } ] } ] }
    ] }
    """;

    private const string ErTextDefs = """
    { "main_classes": [
      { "index": 2000, "instrs": [
        { "name": "Initialize Event", "index": 0, "args": [ { "name": "Event Slot ID", "type": 5 }, { "name": "Event ID", "type": 2 }, { "name": "Parameters", "type": 2 } ] } ] },
      { "index": 2007, "instrs": [
        { "name": "Display Generic Dialog", "index": 1, "args": [
          { "name": "Message ID", "type": 5 }, { "name": "Dialog Type", "type": 4 }, { "name": "Number of Options", "type": 4 },
          { "name": "Entity ID", "type": 2 }, { "name": "Display Distance", "type": 6 } ] },
        { "name": "Display Status Message", "index": 3, "args": [ { "name": "Message ID", "type": 5 }, { "name": "Pad State", "type": 0 } ] },
        { "name": "Display Blinking Message", "index": 4, "args": [ { "name": "Message ID", "type": 5 } ] },
        { "name": "Display Area Welcome Message", "index": 14, "args": [ { "name": "Message ID", "type": 5 } ] } ] }
    ] }
    """;

    private static EventPort TextPort(MapText.Method mode, MapText.Used? used = null) =>
        new(Emedf.Parse(Ds3TextDefs), Emedf.Parse(ErTextDefs), [(Ds3Id, ErId)], new FlagMap()) { Texts = used, TextMode = mode };

    private static void TextInScripts()
    {
        var er = Emedf.Parse(ErTextDefs);
        // Event 0 starts event 14000200 and passes it a message number; that event shows it as a status message.
        var script = OneEvent(0,
            Ins(2000, 0, 0, 14000200u, 10010161),
            Ins(2007, 1, 10010160, (short)0, (short)1, 4001500, 3f),    // "Door is locked", written in the line
            Ins(2007, 4, 10010700, (byte)0),                            // DS3's "Display Message" is the blinking one
            Ins(2007, 1, -1, (short)0, (short)1, 4001500, 3f));         // no message stays none
        var shown = new EMEVD.Event(14000200, EMEVD.Event.RestBehaviorType.Default);
        shown.Instructions.Add(Ins(2007, 3, 0, (byte)1));
        shown.Parameters.Add(new EMEVD.Parameter(0, 0, 0, 4));
        script.Events.Add(shown);

        var used = new MapText.Used();
        var port = TextPort(MapText.Method.All, used);
        port.Learn(script);
        var result = port.Convert(script, EMEVD.Game.Sekiro, MoveToSlot);
        var main = result.Events[0].Instructions;
        Expect(BitConverter.ToInt32(main[0].ArgData, 8) == MapText.Base + 10010161, $"a message passed to an event moves where the event is started ({BitConverter.ToInt32(main[0].ArgData, 8)})");
        Expect(Arg(main[1], "Message ID", er) == MapText.Base + 10010160, $"a dialog's message moves ({Arg(main[1], "Message ID", er)})");
        Expect(main[2].Bank == 2007 && main[2].ID == 4 && Arg(main[2], "Message ID", er) == MapText.Base + 10010700, "Display Message becomes Display Blinking Message with DS3's words");
        Expect(Arg(main[3], "Message ID", er) == -1, "no message (-1) stays none");
        var passed = result.Events[1].Instructions[0];
        Expect(Arg(passed, "Message ID", er) == 0 && result.Events[1].Parameters.Count == 1, "the line that takes the passed-in number keeps its placeholder and its parameter");
        Expect(used.Messages.SetEquals([10010160, 10010161, 10010700]) && used.Places.Count == 0, $"the numbers named are collected ({string.Join(" ", used.Messages)})");
        Expect(port.Stats.TextsMoved == 3 && port.Stats.ToString().Contains("3 messages"), port.Stats.ToString());
        result.Write();

        // An action button's "Help Message ID" is not event text handled here (ActionButtons gives it a row).
        Expect(EventPort.KindOf("Help Message ID") == EventPort.Kind.Other && EventPort.KindOf("Event Message ID") == EventPort.Kind.Other
               && EventPort.KindOf("Message Entity ID") == EventPort.Kind.Entity && EventPort.KindOf("Subarea Name Popup Message ID") == EventPort.Kind.Other,
            "only a plain Message ID is event text");

        // Off: DS3's numbers stay, as before.
        var off = TextPort(MapText.Method.Off);
        off.Learn(script);
        var kept = off.Convert(script, EMEVD.Game.Sekiro, MoveToSlot).Events[0].Instructions;
        Expect(Arg(kept[1], "Message ID", er) == 10010160 && BitConverter.ToInt32(kept[0].ArgData, 8) == 10010161 && off.Stats.TextsMoved == 0, "DARKSIGN_MAP_TEXT=0 keeps DS3's numbers");
    }

    private static void TextPlaces()
    {
        var er = Emedf.Parse(ErTextDefs);
        var script = OneEvent(0, Ins(2007, 11, 3000));
        var used = new MapText.Used();
        var port = TextPort(MapText.Method.All, used);
        port.Learn(script);
        var line = port.Convert(script, EMEVD.Game.Sekiro, MoveToSlot).Events[0].Instructions[0];
        Expect(line.Bank == 2007 && line.ID == 14 && Arg(line, "Message ID", er) == MapText.Base + 3000, $"the welcome line names DS3's area name ({line.Bank}[{line.ID}] {Arg(line, "Message ID", er)})");
        Expect(used.Places.SetEquals([3000]) && used.Messages.Count == 0, "an area name is a place name, not an event message");

        var noPlaces = TextPort(MapText.Method.NoPlaces);
        noPlaces.Learn(script);
        Expect(Arg(noPlaces.Convert(script, EMEVD.Game.Sekiro, MoveToSlot).Events[0].Instructions[0], "Message ID", er) == 3000, "noplaces keeps the area name's DS3 number");

        Expect(MapText.CollisionPlace(3000, MapText.Method.All) == MapText.Base + 3000, "a collision's area name moves");
        Expect(MapText.CollisionPlace(-1, MapText.Method.All) == -1 && MapText.CollisionPlace(0, MapText.Method.All) == 0, "a collision with no area name keeps none");
        Expect(MapText.CollisionPlace(-3000, MapText.Method.All) == -(MapText.Base + 3000), "a negative area name keeps its sign");
        Expect(MapText.CollisionPlace(3000, MapText.Method.NoPlaces) == -1 && MapText.CollisionPlace(3000, MapText.Method.Off) == -1, "area names off: collisions name none, as before");
        Expect(MapText.ModeOf(null) == MapText.Method.All && MapText.ModeOf("noplaces") == MapText.Method.NoPlaces && MapText.ModeOf("0") == MapText.Method.Off, "DARKSIGN_MAP_TEXT is read");
        Expect(MapText.Message(10010160, MapText.Method.NoPlaces) == MapText.Base + 10010160 && MapText.Message(MapText.Base + 5, MapText.Method.All) == MapText.Base + 5,
            "messages still move with area names off, and a moved number doesn't move twice");
    }

    private static void TextObjAct()
    {
        PARAMDEF Def(params (PARAMDEF.DefType Type, string Name)[] fields)
        {
            var def = new PARAMDEF { ParamType = "OBJ_ACT_PARAM_ST", DataVersion = 1 };
            foreach (var (t, n) in fields)
                def.Fields.Add(new PARAMDEF.Field(def, t, n));
            return def;
        }
        var ds3Def = Def((PARAMDEF.DefType.s32, "actionEnableMsgId"), (PARAMDEF.DefType.s32, "actionFailedMsgId"), (PARAMDEF.DefType.s32, "spQualifiedPassEventFlag"),
            (PARAMDEF.DefType.u8, "spQualifiedType"), (PARAMDEF.DefType.s32, "actionButtonParamId"));
        var erDef = Def((PARAMDEF.DefType.s32, "actionEnableMsgId"), (PARAMDEF.DefType.s32, "actionFailedMsgId"), (PARAMDEF.DefType.u32, "spQualifiedPassEventFlag"),
            (PARAMDEF.DefType.u8, "spQualifiedType"), (PARAMDEF.DefType.s32, "actionButtonParamId"));
        var ds3 = new PARAM { ParamType = ds3Def.ParamType, Rows = [] };
        void Ds3Row(int id, int failed)
        {
            var row = new PARAM.Row(id, "", ds3Def);
            row["actionEnableMsgId"].Value = 10010400;
            row["actionFailedMsgId"].Value = failed;
            row["spQualifiedPassEventFlag"].Value = -1;
            row["actionButtonParamId"].Value = -1;
            ds3.Rows.Add(row);
        }
        Ds3Row(300310, 10010161); // "Does not open from this side"
        Ds3Row(300320, -1);
        PARAM Er()
        {
            var er = new PARAM { ParamType = erDef.ParamType, Rows = [] };
            var template = new PARAM.Row(ObjectPort.ObjActTemplateRow, "door", erDef);
            template["actionEnableMsgId"].Value = -1;
            template["actionFailedMsgId"].Value = 4000;
            er.Rows.Add(template);
            return er;
        }

        var notes = new List<string>();
        var used = new HashSet<int>();
        var er = Er();
        ObjectPort.AddObjActRows(er, ds3, null, notes, MapText.Method.All, used);
        Expect((int)er[70300310]["actionFailedMsgId"].Value == MapText.Base + 10010161, $"the failed message names DS3's text ({er[70300310]["actionFailedMsgId"].Value})");
        Expect((int)er[70300320]["actionFailedMsgId"].Value == -1, "a row with no failed message keeps none");
        Expect((int)er[70300310]["actionEnableMsgId"].Value == -1, "the prompt message is the template's (Elden Ring prompts come from the action button row)");
        Expect(used.SetEquals([10010161]) && notes.Single().Contains("1 \"can't do that\" messages"), notes[0]);

        var off = Er();
        ObjectPort.AddObjActRows(off, ds3, null, null, MapText.Method.Off);
        Expect((int)off[70300310]["actionFailedMsgId"].Value == 10010161 && (int)off[70300310]["actionEnableMsgId"].Value == 10010400, "DARKSIGN_MAP_TEXT=0 keeps DS3's numbers");
    }

    private static void TextMerge()
    {
        var fmg = new FMG { Version = FMG.FMGVersion.DarkSouls3 };
        fmg.Entries.Add(new FMG.Entry(4000, "Elden Ring's own line"));
        fmg.Entries.Add(new FMG.Entry(MapText.Base + 7, "from an earlier run"));
        var own = new Dictionary<int, string> { [10010160] = "Door is locked", [10010161] = "Does not open from this side", [0] = "zero" };
        var other = new Dictionary<int, string> { [9000] = "Lever does not move", [10010160] = "a conversation line with the same number" };
        var (added, fromOther, missing) = MapText.Merge(fmg, own, other, [10010160, 9000, 9001, 9000, -1]);
        Expect(added == 2 && fromOther == 1 && missing.SequenceEqual([9001]), $"{added} of DS3's own, {fromOther} from other files, missing {string.Join(" ", missing)}");
        Expect(fmg.Entries.Select(e => e.ID).SequenceEqual([4000, MapText.Base + 9000, MapText.Base + 10010160, MapText.Base + 10010161]),
            $"Elden Ring's text stays, the earlier run's goes, in order: {string.Join(" ", fmg.Entries.Select(e => e.ID))}");
        Expect(fmg.Entries.Single(e => e.ID == MapText.Base + 10010160).Text == "Door is locked", "the event text file's words win over another file's under the same number");
        var again = FMG.Read(fmg.Write());
        Expect(again.Entries.Count == 4 && again.Entries[^1].Text == "Does not open from this side", "the file writes and reads back");
        var goods = new Dictionary<int, string> { [2001] = "Tower Key " };
        Expect(MapText.GoodsTags("Used <?gdsparam@2001?>", goods) == "Used Tower Key" && MapText.GoodsTags("Used <?gdsparam@7?> and <?hostName?>", goods) == "Used <?gdsparam@7?> and <?hostName?>",
            "an item tag becomes DS3's item name; a tag with no name, and other tags, stay");
        Expect(MapText.IsOurs(MapText.Base + 1) && MapText.IsOurs(-(MapText.Base + 1)) && !MapText.IsOurs(99_999_999), "our numbers are told from Elden Ring's");
    }
}
