#!/usr/bin/env python3
"""Sanity checks that don't need KSP or Minecraft:
  1. ksp/src/Proto.cs is exactly what tools/gen_proto_cs.py makes from Proto.java.
  2. Every Proto.X used by the C# sources exists.
  3. The shared-memory regions don't overlap and the total size is what the Java side computes.
Run: python3 tools/check_protocol.py"""
import re, glob, subprocess, sys, pathlib
root = pathlib.Path(__file__).resolve().parent.parent
ok = True
r = subprocess.run([sys.executable, str(root / "tools/gen_proto_cs.py"), "--check"])
ok &= r.returncode == 0

cs = (root / "ksp/src/Proto.cs").read_text()
defined = dict(re.findall(r"const (?:int|long|double) (\w+) = ([^;]+);", cs))
used = {}
for f in glob.glob(str(root / "ksp/src/*.cs")):
    if f.endswith("Proto.cs"): continue
    for m in re.findall(r"Proto\.([A-Z_0-9]+)", open(f, encoding="utf-8").read()):
        used.setdefault(m, f)
missing = [k for k in used if k not in defined and k != "MAPPING_NAME"]
print("C# uses", len(used), "protocol constants;", "all defined" if not missing else f"MISSING {missing}")
ok &= not missing

def ev(name, cache={}):
    if name in cache: return cache[name]
    e = defined[name].strip()
    e = re.sub(r"\(long\)", "", e)
    e = re.sub(r"[A-Za-z_][A-Za-z_0-9]*", lambda m: str(ev(m.group(0))) if m.group(0) in defined else m.group(0), e)
    e = e.replace("L", "") if not e.startswith("0x") else e
    v = eval(re.sub(r"(\d)L\b", r"\1", e))
    cache[name] = v
    return v

regions = [("header", 0, 0x100), ("host", ev("OFF_HOST_STATE"), 0x50), ("mc", ev("OFF_MC_STATE"), 0x100),
           ("overlay ctl", ev("OFF_OVERLAY_CTL"), 0x100), ("water", ev("OFF_WATER_GRID"), 0x10 + 4 * ev("WATER_GRID_SIZE") ** 2),
           ("input", ev("OFF_INPUT_RING"), ev("IR_DATA") + 16 * ev("INPUT_RING_ENTRIES")),
           ("actors", ev("OFF_ACTOR_TABLE"), ev("AT_RECORDS") + ev("ACTOR_RECORD_BYTES") * ev("MAX_ACTORS")),
           ("events", ev("OFF_EVENT_RING"), ev("ER_DATA") + ev("EVENT_BYTES") * ev("EVENT_RING_ENTRIES")),
           ("entities", ev("OFF_WORLD_ENTITIES"), ev("WE_RECORDS") + ev("WORLD_ENTITY_BYTES") * ev("MAX_WORLD_ENTITIES")),
           ("collision", ev("OFF_COLLISION_RING"), ev("COLLISION_RING_BYTES")),
           ("overlay pixels", ev("OFF_OVERLAY_PIXELS"), ev("OVERLAY_SLOT_BYTES") * ev("OVERLAY_SLOTS")),
           ("render", ev("OFF_RENDER_RING"), ev("RENDER_RING_BYTES"))]
regions.sort(key=lambda r: r[1])
for a, b in zip(regions, regions[1:]):
    if a[1] + a[2] > b[1]:
        print(f"OVERLAP: {a[0]} [{a[1]:#x}..{a[1]+a[2]:#x}) runs into {b[0]} at {b[1]:#x}"); ok = False
end = regions[-1][1] + regions[-1][2]
print(f"mapping size {ev('MAPPING_BYTES') >> 20} MB, last region ends at {end:#x}", "OK" if end == ev("MAPPING_BYTES") else "MISMATCH")
ok &= end == ev("MAPPING_BYTES")
print("PROTOCOL CHECK", "PASSED" if ok else "FAILED")
sys.exit(0 if ok else 1)
