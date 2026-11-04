# Dark Souls III world data (from FogMod)

`fog.txt` and `locations.txt` are copied unchanged from thefifthmatt's
[FogMod](https://github.com/thefifthmatt/FogMod) (DS3 Fog Gate Randomizer), `fogdist/` at commit
`28711c3f8b1f7f8c1f329ef9a7f4d9c1e55ce526`. They are licensed under the Apache License 2.0; the full license text
is in `LICENSE-FogMod.txt`.

Copyright thefifthmatt. The Dark Sign reads these files with its own loader
(`Randomizer/src/DarkSign.Randomizer/Data/Ds3FogData.cs`) and does not modify them. Any change we need is
applied as an override in code or in `ds3-overrides.yaml`, so the original files stay byte-identical to upstream.

What they hold:

- `fog.txt`: Dark Souls III's areas, the connections between them and what each needs (keys, bosses), every fog gate
  and pre-existing warp with both sides, key items and their vanilla areas, and an area difficulty scale.
- `locations.txt`: every item location that can hold a key item, with the area it is in and its item lots or
  shop rows, and every enemy placement with its area.
