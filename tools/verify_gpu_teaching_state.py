#!/usr/bin/env python3
"""Audit captured real GPU state against the independent ISA model (tests only)."""
import argparse,json
from pathlib import Path
from teaching_cpu_assembler import load_programs, CpuReference
p=argparse.ArgumentParser(description=__doc__);p.add_argument('report',type=Path);o=p.parse_args()
r=json.loads(o.report.read_text());program=next(p for p in load_programs() if p.id==r['id'])
cpu=CpuReference(program)
for _ in range(r['cycles']): cpu.step(False)
expected={k:getattr(cpu,k) for k in ('pc','instruction','phase','acc','x','sp','out','zero','carry','halt','bank')}
expected['terminal_sequence']=cpu.terminal_sequence
for k,v in expected.items():
    if r['registers'][k]!=int(v): raise AssertionError(f"{r['id']}: {k} GPU={r['registers'][k]} reference={v}")
if r['ram']!=cpu.ram:
    bad=next(i for i,(a,b) in enumerate(zip(r['ram'],cpu.ram)) if a!=b);raise AssertionError(f"RAM 0x{bad:03x}: {r['ram'][bad]} != {cpu.ram[bad]}")
print(json.dumps({'passed':True,'id':r['id'],'cycles':r['cycles'],'ramBytes':len(r['ram']),'registers':r['registers']}))
