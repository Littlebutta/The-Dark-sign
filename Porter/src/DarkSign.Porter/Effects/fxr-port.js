// DarkSign porter adapter for @cccode/fxr: reads a DS3 effect, renumbers it and its resources, writes it for Elden Ring.
import { FXR, Game, ProxyNode, GenericNode, NodeType, Action, SFXReference, BasicNode, BillboardEx, NodeSound, BlendMode, LightingMode } from './fxr.js';

export function inspect(bytes) {
  const fxr = FXR.read(bytes, Game.DarkSouls3);
  const r = fxr.getResources();
  const nodeSounds = [...fxr.root.walk()].flatMap(audios).filter(a => a.sound > 0);
  const heldSounds = nodeSounds.filter(a => a.unk_ds3_f1_2 === true).map(a => a.sound);
  // Sounds the effect plays without holding them: a node's sound with the flag clear, and every sound it plays any
  // other way (on each emission). Nothing ends these when the effect ends.
  const unheldSounds = [...new Set([...nodeSounds.filter(a => a.unk_ds3_f1_2 !== true).map(a => a.sound),
    ...r.sounds.filter(s => s > 0 && !heldSounds.includes(s))])];
  return JSON.stringify({
    id: fxr.id, sfx: fxr.getReferences().sfx,
    textures: [...new Set(r.textures.map(t => t.resource))], models: r.models, anibnds: r.anibnds, sounds: r.sounds,
    heldSounds, unheldSounds,
  });
}

// The sound actions of a node: the library keeps them on the node's configs (root.nodes[..].configs[..].nodeAudio).
function audios(node) {
  return [node, ...(Array.isArray(node.configs) ? node.configs : [])].map(c => c?.nodeAudio).filter(a => a && typeof a === 'object');
}

// map: { "sfx": {old: new}, "0": textures, "1": models, "2": anibnds, "3": sounds, "once": held sounds play once,
//        "held": DS3 sounds that stay held although "once" is set }
export function convert(bytes, newId, mapJson) {
  const map = JSON.parse(mapJson);
  const fxr = FXR.read(bytes, Game.DarkSouls3);
  fxr.id = newId;
  // A node's sound (action 75: sound, a number, a flag). With the flag set the sound runs for as long as the node
  // lives; Elden Ring ends it with the sound's Stop event when the node is gone (inferred: see FxrPort's
  // StoppableSounds). A ported sound without a Stop event can't be ended and kept playing after the effect was gone
  // (the ice blade of Friede's art), so with map.once the flag is cleared and the sound plays through once. The
  // sounds in map.held (DS3 numbers) have a Stop event: they keep the flag. This runs before the sounds are renumbered.
  const held = map.held ?? {};
  if (map.once?.all)
    for (const node of fxr.root.walk())
      for (const audio of audios(node))
        if (audio.unk_ds3_f1_2 === true && !held[audio.sound]) audio.unk_ds3_f1_2 = false;
  // DS3's billboards have no lighting field; Elden Ring's do, and an effect that doesn't set it is drawn unlit: at
  // its full colour whatever light it stands in. DS3's smoke and fog cards then glowed white in dark places (the
  // white blob in map 2's courtyards: m30's fog cards f830050/f830202, 5 m wide, drawn at 0.88 white). Elden Ring
  // lights most of its own billboards that blend like smoke (Normal and Unk6: 3,754 of 4,881 in its common effects,
  // 3,431 of them with lighting -2) and leaves the additive ones (glows, sparks, fire) unlit (3,835 of 4,090). With
  // map.lit, DS3's Normal and Unk6 billboards get -2 the same way; additive ones stay unlit.
  if (map.lit?.all)
    for (const action of fxr.root.walkActions())
      if (action instanceof BillboardEx && smokeBlend(action.blendMode)) action.lighting = LightingMode.UnkMinus2;
  fxr.root.remapResources((type, resource) => map[type]?.[resource] ?? resource);
  const sfx = map.sfx ?? {};
  for (const node of fxr.root.walk()) {
    if (node instanceof ProxyNode) {
      node.sfx = sfx[node.sfx] ?? node.sfx;
    } else if (node instanceof GenericNode && node.type === NodeType.Proxy && node.actions.length > 0) {
      const a = node.actions[0];
      if (a instanceof SFXReference) a.sfx = sfx[a.sfx] ?? a.sfx;
      else if (a instanceof Action) a.fields1[0].value = sfx[a.fields1[0].value] ?? a.fields1[0].value;
    }
  }
  return new Uint8Array(fxr.toArrayBuffer(Game.EldenRing));
}

// A billboard blend mode that layers like smoke (Normal, or Unk6, which looks the same) rather than adding light.
function smokeBlend(blendMode) {
  const mode = typeof blendMode === 'number' ? blendMode : blendMode?.value;
  return mode === BlendMode.Normal || mode === BlendMode.Unk6;
}

// For the self-tests: a small DS3 effect that draws one billboard with a texture and, with child > 0, plays another
// effect. With sound > 0 the billboard's node holds that sound (action 75 with its flag set).
export function sample(id, child, texture, sound = 0, blendMode = BlendMode.Normal) {
  const fxr = new FXR(id);
  const actions = [new BillboardEx({ texture, blendMode })];
  if (sound > 0) actions.push(new NodeSound({ sound, unk_ds3_f1_2: true }));
  fxr.root.nodes.push(new BasicNode(actions));
  if (child > 0) fxr.root.nodes.push(new ProxyNode(child));
  return new Uint8Array(fxr.toArrayBuffer(Game.DarkSouls3));
}

// For the self-tests: the sounds a converted (Elden Ring) effect holds and all the sounds it plays.
export function soundsOfConverted(bytes) {
  const all = [...FXR.read(bytes, Game.EldenRing).root.walk()].flatMap(audios).filter(a => a.sound > 0);
  return JSON.stringify({ held: all.filter(a => a.unk_ds3_f1_2 === true).map(a => a.sound), sounds: all.map(a => a.sound) });
}

// For the self-tests: the lighting mode of each billboard of a converted (Elden Ring) effect.
export function lightingOfConverted(bytes) {
  return JSON.stringify([...FXR.read(bytes, Game.EldenRing).root.walkActions()].filter(a => a instanceof BillboardEx).map(a => a.lighting));
}
