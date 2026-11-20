# Porter tools

- `gen_fsb5_vorbis_setups.py --all|<vorbis_headers.py> <out.bin>`: builds `src/DarkSign.Porter/Sounds/fsb5-vorbis-setups.bin`,
  the Vorbis setup headers FMOD strips from FSB5 samples, keyed by their CRC32. The (quality, channels, rate) per CRC
  is python-fsb5's table (https://github.com/HearthSim/python-fsb5, MIT); the headers are made with the system
  libvorbis/libvorbisenc and each is checked against its CRC (all 164 match).
  The shipped table is made with `--all` (every FMOD quality x channels x common rate, 306 setups, a superset of the
  164), since DS3 has samples whose setup CRC python-fsb5's table lacks.
- `merge_vgmstream_fsb_setups.py <vorbis_codebooks_fsb.h> <in.bin> <out.bin>`: adds the FMOD Vorbis setup packets
  vgmstream ships (https://github.com/vgmstream/vgmstream, `src/coding/libs/vorbis_codebooks_fsb.h`, ISC-style; the
  packets come from python-fsb5) that the table lacks, at FMOD's 256/2048 blocks, each checked against its CRC. The
  libvorbisenc rebuild above cannot make them: FMOD forces 256/2048 blocks, which changes the setup at low sample
  rates, so DS3's 8-24 kHz samples (boss voices) had "no Vorbis setup header". The shipped table is `--all` plus
  these 54 (360 setups).
- `parse_minidump.py <eldenring.exe.PID.dmp> ...`: reads the game's crash dumps (`%LOCALAPPDATA%\CrashDumps`) with
  plain Python: exception code and the address it touched, registers, every stack slot pointing into a module (the
  callers, as eldenring.exe+RVA for Ghidra), code bytes around RIP. The game writes a dump on every crash; the dumps
  explained the 1.0.230 map-load crash (+0x18C038A) and the 1.0.238 boot crash (+0xAED000, `docs/maps/own-map-ids.md`).
