// DarkSign porter adapter for @cccode/fxr: reads a DS3 effect, renumbers it and its resources, writes it for Elden Ring.
import { FXR, Game, ProxyNode, GenericNode, NodeType, Action, SFXReference } from './fxr.js';

export function inspect(bytes) {
  const fxr = FXR.read(bytes, Game.DarkSouls3);
  const r = fxr.getResources();
  return JSON.stringify({
    id: fxr.id, sfx: fxr.getReferences().sfx,
    textures: [...new Set(r.textures.map(t => t.resource))], models: r.models, anibnds: r.anibnds, sounds: r.sounds,
    heldSounds: [...fxr.root.walk()].flatMap(audios).filter(a => a.unk_ds3_f1_2 === true && a.sound > 0).map(a => a.sound),
  });
}

// The sound actions of a node: the library keeps them on the node's configs (root.nodes[..].configs[..].nodeAudio).
function audios(node) {
  return [node, ...(Array.isArray(node.configs) ? node.configs : [])].map(c => c?.nodeAudio).filter(a => a && typeof a === 'object');
}

// map: { "sfx": {old: new}, "0": textures, "1": models, "2": anibnds, "3": sounds, "once": held sounds play once }
export function convert(bytes, newId, mapJson) {
  const map = JSON.parse(mapJson);
  const fxr = FXR.read(bytes, Game.DarkSouls3);
  fxr.id = newId;
  fxr.root.remapResources((type, resource) => map[type]?.[resource] ?? resource);
  const sfx = map.sfx ?? {};
  for (const node of fxr.root.walk()) {
    // A node's sound (action 75: sound, a number, a flag). With the flag set the sound runs for as long as the node
    // lives. The ported sound is a one-shot Elden Ring has no way to end, and it kept playing after the effect was
    // gone (the ice blade of Friede's art), so with map.once the flag is cleared and the sound plays through once.
    if (map.once?.all)
      for (const audio of audios(node))
        if (audio.unk_ds3_f1_2 === true) audio.unk_ds3_f1_2 = false;
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
