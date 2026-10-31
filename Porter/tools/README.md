# Porter tools

- `gen_fsb5_vorbis_setups.py <vorbis_headers.py> <out.bin>`: builds `src/DarkSign.Porter/Sounds/fsb5-vorbis-setups.bin`,
  the Vorbis setup headers FMOD strips from FSB5 samples, keyed by their CRC32. The (quality, channels, rate) per CRC
  is python-fsb5's table (https://github.com/HearthSim/python-fsb5, MIT); the headers are made with the system
  libvorbis/libvorbisenc and each is checked against its CRC (all 164 match).
