# Adds the FSB5 Vorbis setup packets vgmstream ships (src/coding/libs/vorbis_codebooks_fsb.h, ISC-style licence,
# packets originally extracted from FMOD by python-fsb5) that fsb5-vorbis-setups.bin lacks.
#
# Why: gen_fsb5_vorbis_setups.py rebuilds setups with libvorbisenc at libvorbis' own block sizes. FMOD encodes every
# FSB5 Vorbis sample with 256/2048 blocks (vgmstream's vorbis_custom_setup_init_fsb), which changes the setup packet
# at low sample rates: DS3's 8000-24000 Hz samples (boss voices and roars: Corrupted Gundyr c511008006, the
# Judicator's c628005500, ...) carry CRCs 0x39E4F39A, 0x6A5436BF, 0x5949B893, 0xD7913109 that no libvorbisenc setting
# reproduces, so the porter could not decode them ("no Vorbis setup header for CRC ...") and those sounds were silent.
#
# Entry layout as gen_fsb5_vorbis_setups.py: crc u32, block short u16, long u16, block size for mode bit 0 u16, for
# mode bit 1 u16, setup length u32, setup. Each packet's CRC32 is checked against its name, and the mode block sizes
# are read back by libvorbis from an identification header with FMOD's 256/2048 blocks.
#
# Usage: merge_vgmstream_fsb_setups.py <vorbis_codebooks_fsb.h> <fsb5-vorbis-setups.bin> <out.bin>
import ctypes, re, struct, sys, zlib
header, table, out = sys.argv[1:4]

src = open(header).read()
packets = {int(m.group(1), 16): bytes(int(x, 16) for x in re.findall(r'0x([0-9a-fA-F]{2})', m.group(2)))
           for m in re.finditer(r'static const uint8_t vcb_([0-9a-fA-F]{8})\[\]\s*=\s*\{(.*?)\};', src, re.S)}
assert packets, 'no setup packets found in ' + header
for crc, packet in packets.items():
    assert zlib.crc32(packet) == crc, f'{crc:08x}: CRC does not match its packet'

data = open(table, 'rb').read()
assert data[:4] == b'FSVS'
count, p, entries, have = struct.unpack('<I', data[4:8])[0], 8, [], set()
for _ in range(count):
    crc, bs, bl, m0, m1, n = struct.unpack('<IHHHHI', data[p:p + 16])
    entries.append(data[p:p + 16 + n]); have.add(crc); p += 16 + n

vorbis = ctypes.CDLL('libvorbis.so.0')
class Info(ctypes.Structure):
    _fields_ = [('version', ctypes.c_int), ('channels', ctypes.c_int), ('rate', ctypes.c_long), ('bu', ctypes.c_long),
                ('bn', ctypes.c_long), ('bl', ctypes.c_long), ('bw', ctypes.c_long), ('cs', ctypes.c_void_p)]
class Comment(ctypes.Structure):
    _fields_ = [('uc', ctypes.c_void_p), ('cl', ctypes.c_void_p), ('c', ctypes.c_int), ('v', ctypes.c_char_p)]
class Packet(ctypes.Structure):
    _fields_ = [('packet', ctypes.POINTER(ctypes.c_char)), ('bytes', ctypes.c_long), ('bos', ctypes.c_long),
                ('eos', ctypes.c_long), ('gp', ctypes.c_longlong), ('pn', ctypes.c_longlong)]

def packet(raw, number):
    buf = ctypes.create_string_buffer(raw, len(raw))
    return Packet(ctypes.cast(buf, ctypes.POINTER(ctypes.c_char)), len(raw), 1 if number == 0 else 0, 0, 0, number), buf

def ident(channels, short, long):
    b = bytearray(30); b[0] = 1; b[1:7] = b'vorbis'; b[11] = channels
    b[12:16] = struct.pack('<I', 44100); b[28] = (short.bit_length() - 1) | ((long.bit_length() - 1) << 4); b[29] = 1
    return bytes(b)

def comment():
    vendor = b'DarkSign'
    return bytes([3]) + b'vorbis' + struct.pack('<I', len(vendor)) + vendor + struct.pack('<I', 0) + b'\x01'

SHORT, LONG = 256, 2048
added = 0
for crc, setup in sorted(packets.items()):
    if crc in have:
        continue
    # The setup's channel count must match the identification header's: try the counts FSB5 uses.
    for channels in (1, 2, 3, 4, 5, 6, 8):
        info, com = Info(), Comment()
        vorbis.vorbis_info_init(ctypes.byref(info)); vorbis.vorbis_comment_init(ctypes.byref(com))
        ok = True
        for i, raw in enumerate((ident(channels, SHORT, LONG), comment(), setup)):
            pk, keep = packet(raw, i)
            if vorbis.vorbis_synthesis_headerin(ctypes.byref(info), ctypes.byref(com), ctypes.byref(pk)) != 0:
                ok = False; break
        if ok:
            break
    assert ok, f'{crc:08x}: libvorbis refuses the setup at 256/2048 for every channel count'
    sizes = []
    for first in (0x00, 0x02):
        pk, keep = packet(bytes([first, 0, 0, 0]), 3)
        sizes.append(vorbis.vorbis_packet_blocksize(ctypes.byref(info), ctypes.byref(pk)))
    assert all(x > 0 for x in sizes), (hex(crc), sizes)
    entries.append(struct.pack('<IHHHHI', crc, SHORT, LONG, sizes[0], sizes[1], len(setup)) + setup)
    added += 1

with open(out, 'wb') as f:
    f.write(struct.pack('<4sI', b'FSVS', len(entries)) + b''.join(entries))
print(f'{added} setups added from vgmstream, {len(entries)} in all')
