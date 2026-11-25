using SoulsFormats;

namespace DarkSign.Porter.Maps;

public static partial class MapPortChecks
{
    private static IEnumerable<(string Name, Action Test)> SceneWeaponChecks()
    {
        yield return ("player scene weapons: animations a script plays on the player by number are found; other entities and passed-in lines aren't read", SceneWeaponAnims);
        yield return ("player scene weapons: DS3's weapon hides go into Elden Ring's entry (Gundyr's sword pull, 60750); others and reruns left alone", SceneWeaponCopy);
    }

    private const string Ds3AnimDefs = """
    { "main_classes": [
      { "index": 2003, "instrs": [
        { "name": "Request Animation Playback", "index": 1, "args": [
          { "name": "Entity ID", "type": 5 }, { "name": "Animation ID", "type": 5 }, { "name": "Should Loop", "type": 0 },
          { "name": "Wait For Completion", "type": 0 }, { "name": "Ignore Wait For Transition", "type": 0 }, { "name": "Comparison Type", "type": 0 }, { "name": "Number Of Target Characters", "type": 6 } ] } ] }
    ] }
    """;

    private static void SceneWeaponAnims()
    {
        var script = new EMEVD();
        var e = new EMEVD.Event(14005811, EMEVD.Event.RestBehaviorType.Default);
        EMEVD.Instruction Play(int entity, int anim) => RunEvents.Make(2003, 1, entity, anim, (byte)0, (byte)0, (byte)0, (byte)0, 1f);
        e.Instructions.Add(Play(10000, 60750));   // the sword pull
        e.Instructions.Add(Play(4000800, 20003)); // Gundyr's own
        e.Instructions.Add(Play(10000, 0));       // anim passed in
        e.Parameters.Add(new EMEVD.Parameter(2, 4, 0, 4));
        script.Events.Add(e);
        var found = PlayerSceneWeapons.ScriptedPlayerAnims([EMEVD.Read(script.Write())], Emedf.Parse(Ds3AnimDefs), out int passedIn);
        Expect(found.SetEquals([60750]) && passedIn == 1, $"found {string.Join(" ", found)}, {passedIn} passed in");
    }

    private static void SceneWeaponCopy()
    {
        TAE Tae(params (int Id, (int Type, float Start, float End, byte[] Bytes)[] Events)[] anims)
        {
            var tae = new TAE { Format = TAE.TAEFormat.DS3, Animations = [] };
            foreach (var (id, events) in anims)
            {
                var a = new TAE.Animation(id, new TAE.Animation.AnimMiniHeader.Standard(), $"a000_{id:D6}.hkt");
                foreach (var ev in events)
                    a.Events.Add(new TAE.Event(ev.Start, ev.End, ev.Type, 0, ev.Bytes, false));
                tae.Animations.Add(a);
            }
            return tae;
        }
        byte[] Hide(byte left, byte right) => [left, right, 0, 0, 0, 0, 0, 0];
        var ds3 = Tae((60750, [(710, 0.333f, 6.0f, Hide(1, 0)), (710, 0.333f, 6.367f, Hide(0, 1)), (0, 0, 7.7f, new byte[16])]),
            (60760, [(0, 0, 1, new byte[16])]));
        var er = Tae((60750, [(0, 0, 6.7f, new byte[16])]), (60760, []), (61000, [(710, 0, 1, Hide(1, 1))]));
        var lines = PlayerSceneWeapons.Apply(ds3, er, [60750, 60760, 61000, 69999]);
        var pull = er.Animations.Single(a => a.ID == 60750).Events.Where(x => x.Type == PlayerSceneWeapons.HideWeaponEvent).ToList();
        Expect(pull.Count == 2 && pull[0].GetParameterBytes(false)[0] == 1 && pull[1].GetParameterBytes(false)[1] == 1
               && Math.Abs(pull[1].EndTime - 6.367f) < 1e-4 && pull.All(x => x.Group is not null), "60750: " + lines[0]);
        Expect(er.Animations.Single(a => a.ID == 60760).Events.Count == 0 && er.Animations.Single(a => a.ID == 61000).Events.Count == 1
               && lines[3].Contains("no entry"), string.Join(" | ", lines));
        var again = PlayerSceneWeapons.Apply(ds3, er, [60750]);
        Expect(again[0].Contains("already") && er.Animations.Single(a => a.ID == 60750).Events.Count(x => x.Type == 710) == 2, "rerun: " + again[0]);
    }
}
