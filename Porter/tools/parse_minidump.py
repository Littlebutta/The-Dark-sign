#!/usr/bin/env python3
"""Reads an Elden Ring minidump (%LOCALAPPDATA%\\CrashDumps\\eldenring.exe.<pid>.dmp) without a debugger:
the module list, the exception record (code, fault address, read/write/execute and the address touched), the
registers of the faulting thread, every stack slot that points into a module (the return-address candidates, read
from RSP upward, so the crash site's callers come first), the code bytes around RIP and the memory the pointer
registers point at. Offsets inside eldenring.exe are RVAs (eldenring.exe+0x...), the numbers Ghidra and the
Windows event log use. Needs only Python 3. Usage: parse_minidump.py <dump> [<dump> ...]

Minidump layout from Microsoft's minidumpapiset.h (MINIDUMP_HEADER, MINIDUMP_DIRECTORY, ModuleListStream 4,
MemoryListStream 5, ExceptionStream 6, Memory64ListStream 9) and the x64 CONTEXT register offsets.
Written for The Dark Sign on 2026-11-09, when it found the 1.0.238 boot crash (eldenring.exe+0xAED000, a null
per-map entry for a map ID the mod had registered) and the 1.0.230 map-load crash (+0x18C038A, motion properties)."""
import struct, sys
def rd(b,o,f): return struct.unpack_from(f,b,o)
def mstr(b,rva):
    n=rd(b,rva,'<I')[0]; return b[rva+4:rva+4+n].decode('utf-16le','replace')
def main(p):
    b=open(p,'rb').read()
    sig,ver,nstreams,dirrva=rd(b,0,'<4sIII')
    streams={}
    for i in range(nstreams):
        t,sz,rva=rd(b,dirrva+12*i,'<III'); streams.setdefault(t,[]).append((sz,rva))
    mods=[]
    for sz,rva in streams.get(4,[]):
        n=rd(b,rva,'<I')[0]
        for i in range(n):
            base,size,cs,ts,nrva=rd(b,rva+4+108*i,'<QIIII')
            mods.append((base,size,mstr(b,nrva)))
    er=[m for m in mods if 'eldenring' in m[2].lower()]
    print("modules:",len(mods)); 
    for m in mods[:40]: print(f"  {m[0]:x} {m[1]:x} {m[2]}")
    erbase=er[0][0] if er else 0
    def rvaof(a):
        for base,size,name in mods:
            if base<=a<base+size: return f"{name.split(chr(92))[-1]}+0x{a-base:x}"
        return None
    for sz,rva in streams.get(6,[]):
        tid,_=rd(b,rva,'<II'); code,flags,rec,addr=rd(b,rva+8,'<IIQQ')
        nparams=rd(b,rva+8+24,'<I')[0]; params=rd(b,rva+8+32,'<15Q')
        csz,crva=rd(b,rva+8+152,'<II')
        kind={0:"read",1:"write",8:"execute"}.get(params[0],"?") if code==0xC0000005 and nparams>=2 else ""
        print(f"exception tid={tid} code={code:x} addr={addr:x} ({rvaof(addr)}) params={[hex(x) for x in params[:nparams]]}"
              + (f"  -> {kind} of address 0x{params[1]:x}" if kind else ""))
        ctx=b[crva:crva+csz]
        regs={n:rd(ctx,o,'<Q')[0] for n,o in [('rax',0x78),('rcx',0x80),('rdx',0x88),('rbx',0x90),('rsp',0x98),('rbp',0xa0),('rsi',0xa8),('rdi',0xb0),('r8',0xb8),('r9',0xc0),('r10',0xc8),('r11',0xd0),('r12',0xd8),('r13',0xe0),('r14',0xe8),('r15',0xf0),('rip',0xf8)]}
        for k,v in regs.items(): print(f"  {k}={v:x} {rvaof(v) or ''}")
        rsp=regs['rsp']
        # memory
        ranges=[]
        for sz2,rva2 in streams.get(9,[]):
            n,baserva=rd(b,rva2,'<QQ'); off=baserva
            for i in range(n):
                st,ds=rd(b,rva2+16+16*i,'<QQ'); ranges.append((st,ds,off)); off+=ds
        for sz2,rva2 in streams.get(5,[]):
            n=rd(b,rva2,'<I')[0]
            for i in range(n):
                st,ds,drva=rd(b,rva2+4+16*i,'<QIi'); ranges.append((st,ds,drva))
        def read(a,n):
            for st,ds,off in ranges:
                if st<=a<st+ds:
                    o=off+(a-st); return b[o:o+min(n,st+ds-a)]
            return None
        stk=read(rsp,0x3000)
        print("stack candidates (rsp upward):")
        if stk:
            for i in range(0,len(stk)-7,8):
                v=rd(stk,i,'<Q')[0]; r=rvaof(v)
                if r and ('eldenring' in r or '.dll' in r): print(f"  rsp+{i:x}: {v:x} {r}")
        # bytes at rip
        code_bytes=read(regs['rip']-32,64)
        if code_bytes: print("bytes around rip:",code_bytes.hex())
        # rcx/rdx/r8 memory peek
        for r in ['rcx','rdx','r8','r9','rbx','rsi','rdi','rbp','r12','r13','r14','r15']:
            m=read(regs[r],64)
            if m: print(f"  [{r}]:",m.hex())
for path in sys.argv[1:]:
    print('=====', path)
    main(path)
