# Entry: crc u32, blocksize short u16, long u16, block size for mode bit 0 u16, for mode bit 1 u16, setup length u32, setup.
# Builds the FSB5 Vorbis setup-header table: FMOD strips each sample's Vorbis setup packet and stores its CRC32.
# The (quality, channels, rate) per CRC comes from python-fsb5's vorbis_headers.py (MIT); the packets are made by
# libvorbisenc with the same settings FMOD used, and each one is checked against its CRC.
import ctypes, re, struct, sys, zlib
src, out = sys.argv[1], sys.argv[2]
table = {int(k): tuple(int(x) for x in v.split(',')) for k, v in re.findall(r'(\d+):\s*\(([^)]*)\)', open(src).read())}
vorbis = ctypes.CDLL('libvorbis.so.0'); enc = ctypes.CDLL('libvorbisenc.so.2')
class Info(ctypes.Structure):
    _fields_ = [('version', ctypes.c_int), ('channels', ctypes.c_int), ('rate', ctypes.c_long), ('bu', ctypes.c_long),
                ('bn', ctypes.c_long), ('bl', ctypes.c_long), ('bw', ctypes.c_long), ('cs', ctypes.c_void_p)]
class Comment(ctypes.Structure):
    _fields_ = [('uc', ctypes.c_void_p), ('cl', ctypes.c_void_p), ('c', ctypes.c_int), ('v', ctypes.c_char_p)]
class Packet(ctypes.Structure):
    _fields_ = [('packet', ctypes.POINTER(ctypes.c_char)), ('bytes', ctypes.c_long), ('bos', ctypes.c_long),
                ('eos', ctypes.c_long), ('gp', ctypes.c_longlong), ('pn', ctypes.c_longlong)]
enc.vorbis_encode_setup_vbr.argtypes = [ctypes.POINTER(Info), ctypes.c_long, ctypes.c_long, ctypes.c_float]
entries, bad = [], 0
for crc, (q, ch, rate) in sorted(table.items()):
    info, com = Info(), Comment()
    vorbis.vorbis_info_init(ctypes.byref(info)); vorbis.vorbis_comment_init(ctypes.byref(com))
    state = ctypes.create_string_buffer(1024)
    assert enc.vorbis_encode_setup_vbr(ctypes.byref(info), ch, rate, ctypes.c_float(-0.1 + 1.1 * (q - 1) / 99)) == 0
    one = ctypes.c_int(1)
    enc.vorbis_encode_ctl(ctypes.byref(info), 0x41, ctypes.byref(one))
    assert enc.vorbis_encode_setup_init(ctypes.byref(info)) == 0
    assert vorbis.vorbis_analysis_init(state, ctypes.byref(info)) == 0
    p1, p2, p3 = Packet(), Packet(), Packet()
    assert vorbis.vorbis_analysis_headerout(state, ctypes.byref(com), ctypes.byref(p1), ctypes.byref(p2), ctypes.byref(p3)) == 0
    setup = bytes(p3.packet[:p3.bytes])
    if zlib.crc32(setup) != crc:
        bad += 1
    # Block size of an audio packet whose mode bit is 0 or 1, read back by the decoder from these headers.
    dec, dcom = Info(), Comment()
    vorbis.vorbis_info_init(ctypes.byref(dec)); vorbis.vorbis_comment_init(ctypes.byref(dcom))
    for pk in (p1, p2, p3):
        assert vorbis.vorbis_synthesis_headerin(ctypes.byref(dec), ctypes.byref(dcom), ctypes.byref(pk)) == 0
    sizes = []
    for first in (0x00, 0x02):
        buf = ctypes.create_string_buffer(bytes([first, 0, 0, 0]), 4)
        pk = Packet(ctypes.cast(buf, ctypes.POINTER(ctypes.c_char)), 4, 0, 0, 0, 3)
        sizes.append(vorbis.vorbis_packet_blocksize(ctypes.byref(dec), ctypes.byref(pk)))
    assert all(x > 0 for x in sizes), sizes
    bs, bl = vorbis.vorbis_info_blocksize(ctypes.byref(info), 0), vorbis.vorbis_info_blocksize(ctypes.byref(info), 1)
    entries.append(struct.pack('<IHHHHI', crc, bs, bl, sizes[0], sizes[1], len(setup)) + setup)
with open(out, 'wb') as f:
    f.write(struct.pack('<4sI', b'FSVS', len(entries)) + b''.join(entries))
print(len(entries), 'entries,', bad, 'CRC mismatches,', sum(map(len, entries)), 'bytes')
