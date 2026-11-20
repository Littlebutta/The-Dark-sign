// DarkSign porter adapter for @cccode/fxr: reads a DS3 effect, renumbers it and its resources, writes it for Elden Ring.
import { FXR, Game, ProxyNode, GenericNode, NodeType, Action, SFXReference, BasicNode, BillboardEx, NodeSound, BlendMode, LightingMode, PointLight, SpotLight, anyValueMult } from './fxr.js';

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
    ...look(fxr),
  });
}

// What an effect does to the light around it, for the lamps the port places at DS3's flames (FlameLights):
// lights = the point and spot lights it carries itself; glow = the things it draws that add light to the picture
// (the additive blends: Add, and Unk0 and Unk7, which the library finds to work like it; flames, embers, glows.
// Smoke and fog blend normally); glowColour = the mean colour those are
// drawn in (their colour values multiplied; an animated value counts at its last keyframe), or null when none
// could be read.
function look(fxr) {
  let lights = 0, glow = 0, n = 0;
  const sum = [0, 0, 0];
  for (const action of fxr.root.walkActions()) {
    if (action instanceof PointLight || action instanceof SpotLight) { lights++; continue; }
    if (!action || !('blendMode' in action)) continue;
    const mode = typeof action.blendMode === 'number' ? action.blendMode : action.blendMode?.value;
    if (mode !== BlendMode.Add && mode !== BlendMode.Unk0 && mode !== BlendMode.Unk7) continue;
    glow++;
    const colour = [1, 1, 1];
    let read = false;
    for (const name of ['color1', 'color2', 'color3']) {
      const c = colourOf(action[name]);
      if (!c) continue;
      read = true;
      for (let i = 0; i < 3; i++) colour[i] *= c[i];
    }
    if (read) { n++; for (let i = 0; i < 3; i++) sum[i] += colour[i]; }
  }
  return { lights, glow, glowColour: n > 0 ? sum.map(v => v / n) : null };
}

// A colour value of an action as [r, g, b]: a plain value, or the last keyframe (or the value) of an animated one.
function colourOf(value) {
  if (Array.isArray(value)) return value.length >= 3 && value.every(v => typeof v === 'number') ? value : null;
  if (!value || typeof value !== 'object') return null;
  if (Array.isArray(value.keyframes) && value.keyframes.length > 0) return colourOf(value.keyframes[value.keyframes.length - 1].value);
  return colourOf(value.value);
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
  // With map.lights, the point lights DS3's effects carry get Elden Ring's light settings (erLight).
  if (map.lights?.all)
    for (const action of fxr.root.walkActions())
      if (action instanceof PointLight) erLight(action);
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

// A DS3 point light (a weapon's glow, a spell, a candle) set up the way Elden Ring sets up its own. Measured on the
// games' files (2026-11-20): 673 point lights in 638 of DS3's 2,704 common and map effects, 2,979 in 2,621 of Elden
// Ring's 7,157. Converted as read, DS3's lights hardly lit anything in Elden Ring:
// - Strength: colour times multiplier is 1.25 at the median (0.31 to 5.0) for DS3's lights as the library converts
//   them (DS3 colour times 10), 12.8 (3.2 to 32) for Elden Ring's. Elden Ring's lights also have an adaptation
//   exponent of 1 (2,665 of them; 0.5 for 211): where the place is already bright the light is multiplied by 0.1, so
//   12.8 there is 1.28, DS3's own strength, and the full 12.8 lights dark places. DS3's lights have 0 and so were a
//   tenth of Elden Ring's in the dark, where an effect's light shows. Each light now takes 10 times DS3's strength
//   with an exponent of 1: as strong as in DS3 in daylight, as strong as Elden Ring's own at night and indoors.
// - Falloff (unk_ds3_p2_0, which the library documents as the falloff: 0 or less switches the light off, below 1 it
//   fades out sooner): Elden Ring writes a quarter of the radius (median 0.250 of the radius, 0.249 to 0.5). Half
//   of DS3's lights (337) have 0.01, the rest 1 (the candle f839010 0.01 over 2 m, Friede's and the Dancer's
//   weapon lights 0.01 over 3.75 m), so half faded out within a hand's width. Each light now takes a quarter of its
//   radius, as Elden Ring's do.
// - Volume density, the faint glow a light gives the air around it: 0.5 for 2,788 of Elden Ring's lights, 0 for all
//   of DS3's. Now 0.5.
// Colours, radius, flicker, shadows and fade times stay DS3's. Fallback "map-effects" E (DARKSIGN_FXR_LIGHTS=ds3)
// keeps the lights as the library converts them, as builds before 2026-11-20 had them.
function erLight(light) {
  light.diffuseMultiplier = anyValueMult(10, light.diffuseMultiplier);
  light.specularMultiplier = anyValueMult(10, light.specularMultiplier);
  light.adaptationExponent = 1;
  light.unk_ds3_p2_0 = anyValueMult(0.25, light.radius);
  light.volumeDensity = 0.5;
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

// For the self-tests and diagnostics: look() of an Elden Ring effect (a converted one, or one of the game's own).
export function lookOfElden(bytes) {
  return JSON.stringify(look(FXR.read(bytes, Game.EldenRing)));
}

// For the self-tests: a small DS3 effect that draws one additive billboard in a colour and, with light set, carries
// a point light of radius 3 with that falloff (unk_ds3_p2_0; half of DS3's lights have 0.01, the rest 1).
export function flameSample(id, texture, r, g, b, light, falloff = 1) {
  const fxr = new FXR(id);
  // A node draws one thing, so the light gets a node of its own.
  fxr.root.nodes.push(new BasicNode([new BillboardEx({ texture, blendMode: BlendMode.Add, color1: [r, g, b, 1] })]));
  if (light) fxr.root.nodes.push(new BasicNode([new PointLight({ radius: 3, unk_ds3_p2_0: falloff })]));
  return new Uint8Array(fxr.toArrayBuffer(Game.DarkSouls3));
}

// For the self-tests: the point lights of a converted (Elden Ring) effect, with each value at its largest.
export function lightsOfConverted(bytes) {
  return JSON.stringify([...FXR.read(bytes, Game.EldenRing).root.walkActions()].filter(a => a instanceof PointLight).map(a => ({
    strength: largest(a.diffuseMultiplier), adaptation: a.adaptationExponent, falloff: largest(a.unk_ds3_p2_0),
    radius: largest(a.radius), volume: a.volumeDensity,
  })));
}

// The largest a scalar value gets: a number, a property's value or its largest keyframe.
function largest(value) {
  if (typeof value === 'number') return value;
  if (!value || typeof value !== 'object') return NaN;
  if (Array.isArray(value.keyframes) && value.keyframes.length > 0) return Math.max(...value.keyframes.map(k => largest(k.value)));
  return largest(value.value);
}

// For the self-tests: the lighting mode of each billboard of a converted (Elden Ring) effect.
export function lightingOfConverted(bytes) {
  return JSON.stringify([...FXR.read(bytes, Game.EldenRing).root.walkActions()].filter(a => a instanceof BillboardEx).map(a => a.lighting));
}
