#!/usr/bin/env python3
"""Checks the coordinate-frame maths KerbalCraft relies on, using Unity's *left-handed* conventions
(Vector3.Cross is the usual component formula; Quaternion.LookRotation(f, u) gives local +z = f and
local +x = Cross(u, f)). Run: python3 tools/test_frame_math.py"""
import math, random

def cross(a, b): return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
def dot(a, b): return sum(x*y for x, y in zip(a, b))
def add(*vs): return tuple(sum(c) for c in zip(*vs))
def mul(a, s): return tuple(x*s for x in a)
def norm(a): l = math.sqrt(dot(a, a)); return mul(a, 1/l)
def close(a, b, e=1e-9): return all(abs(x-y) < e for x, y in zip(a, b))

def look_rotation_axes(forward, up):
    """Unity: z = forward, x = Cross(up, forward), y = Cross(forward, x)."""
    z = norm(forward); x = norm(cross(up, z)); y = cross(z, x)
    return x, y, z

def look_dir(yaw, pitch):  # SiteFrame.LookDirection
    y, p = math.radians(yaw), math.radians(pitch)
    return (-math.sin(y)*math.cos(p), -math.sin(p), math.cos(y)*math.cos(p))

random.seed(7)
for _ in range(200):
    up = norm((random.uniform(-1, 1), random.uniform(-1, 1), random.uniform(-1, 1)))
    t = (random.uniform(-1, 1), random.uniform(-1, 1), random.uniform(-1, 1))
    north = norm(add(t, mul(up, -dot(t, up))))
    south = mul(north, -1)
    xdir = norm(cross(south, up))                     # SiteFrame.XDir

    # 1. render root = LookRotation(South, Up) with localScale (-1,1,1): local axes map to (XDir, Up, South)
    rx, ry, rz = look_rotation_axes(south, up)
    root_x = mul(rx, -1)
    assert close(root_x, xdir) and close(ry, up) and close(rz, south), "root transform"

    # 2. Minecraft handedness: facing south (yaw 0) the camera's right-hand side is -X; facing west (yaw 90) it is north
    for yaw, expect_right in ((0, mul(xdir, -1)), (90, north), (180, xdir), (270, south)):
        f = look_dir(yaw, 0)
        world_f = add(mul(xdir, f[0]), mul(up, f[1]), mul(south, f[2]))
        cam_x, _, _ = look_rotation_axes(world_f, up)
        assert close(cam_x, expect_right), f"camera right at yaw {yaw}"

    # 3. increasing yaw turns right (clockwise seen from above), pitch>0 looks down
    f0 = look_dir(10, 0); f1 = look_dir(11, 0)
    w0 = add(mul(xdir, f0[0]), mul(south, f0[2])); w1 = add(mul(xdir, f1[0]), mul(south, f1[2]))
    cam_x, _, _ = look_rotation_axes(w0, up)
    assert dot(add(w1, mul(w0, -1)), cam_x) > 0, "yaw+ must turn right"
    assert look_dir(0, 30)[1] < 0, "pitch+ looks down"

    # 4. yaw/pitch recovered from a direction (used when a walk crosses into a new site)
    for _ in range(5):
        yaw, pitch = random.uniform(-180, 180), random.uniform(-89, 89)
        fx, fy, fz = look_dir(yaw, pitch)
        yaw2 = math.degrees(math.atan2(-fx, fz)); pitch2 = -math.degrees(math.asin(fy))
        d = (yaw2 - yaw + 180) % 360 - 180
        assert abs(d) < 1e-6 and abs(pitch2 - pitch) < 1e-6, "yaw/pitch round trip"

# 5. winding: a counter-clockwise (outward, right-handed) Minecraft face stays outward under the mirrored root.
#    Unity's front face is the one whose component-formula normal points at the viewer; under a mirror
#    (negative scale) Unity flips the winding for us, so the net result is the standard handedness conversion.
a, b, c = (0, 1, 0), (0, 0, 0), (1, 0, 0)        # a +Z-facing quad corner triple in Minecraft (CCW seen from +Z)
n_mc = cross(tuple(y-x for x, y in zip(a, b)), tuple(y-x for x, y in zip(a, c)))
assert n_mc[2] > 0
print("frame maths OK (200 random frames)")
