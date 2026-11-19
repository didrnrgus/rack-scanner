#!/usr/bin/env python3
"""점군 포맷 변환: PLY(binary/ascii) ↔ PCD(ascii/binary) ↔ XYZ(텍스트). 표준 라이브러리만 사용.

  python3 tools/pcd_convert.py sessions/2026-11-18_001/points.ply out.pcd
  python3 tools/pcd_convert.py in.pcd out.ply
  python3 tools/pcd_convert.py in.ply out.xyz

좌표는 그대로 (rack-scanner 는 Unity 좌표: Y 위, 미터). 다른 도구(CloudCompare 등)가 Z 위를 기대하면
--z-up 으로 (x, y, z) → (x, z, y) 로 바꾼다 (오른손/왼손 차이로 한 축 부호가 뒤집히는 점 주의).
"""
import argparse, struct, sys

PLY_TYPES = {"char": "b", "uchar": "B", "int8": "b", "uint8": "B", "short": "h", "ushort": "H", "int16": "h",
             "uint16": "H", "int": "i", "uint": "I", "int32": "i", "uint32": "I", "float": "f", "float32": "f",
             "double": "d", "float64": "d"}


def read_ply(path):
    with open(path, "rb") as f:
        if f.readline().strip() != b"ply":
            raise ValueError("not a PLY file")
        fmt, count, props, in_vertex = None, 0, [], False
        while True:
            line = f.readline().decode("ascii").strip()
            t = line.split()
            if not t:
                continue
            if t[0] == "end_header":
                break
            if t[0] == "format":
                fmt = t[1]
            elif t[0] == "element":
                in_vertex = t[1] == "vertex"
                if in_vertex:
                    count = int(t[2])
            elif t[0] == "property" and in_vertex:
                props.append((t[2], t[1]))
        names = [p[0] for p in props]
        pts = []
        if fmt == "ascii":
            for _ in range(count):
                vals = [float(v) for v in f.readline().split()]
                pts.append(dict(zip(names, vals)))
        elif fmt == "binary_little_endian":
            st = struct.Struct("<" + "".join(PLY_TYPES[p[1]] for p in props))
            for _ in range(count):
                pts.append(dict(zip(names, st.unpack(f.read(st.size)))))
        else:
            raise ValueError("unsupported PLY format " + str(fmt))
    return [(p["x"], p["y"], p["z"], int(p.get("red", 255)), int(p.get("green", 255)), int(p.get("blue", 255))) for p in pts]


def write_ply(path, pts):
    with open(path, "wb") as f:
        f.write(("ply\nformat binary_little_endian 1.0\nelement vertex %d\nproperty float x\nproperty float y\n"
                 "property float z\nproperty uchar red\nproperty uchar green\nproperty uchar blue\nend_header\n" % len(pts)).encode())
        st = struct.Struct("<fffBBB")
        for p in pts:
            f.write(st.pack(*p))


def read_pcd(path):
    with open(path, "rb") as f:
        hdr = {}
        while True:
            line = f.readline().decode("ascii").strip()
            if not line or line.startswith("#"):
                continue
            k, *v = line.split()
            hdr[k.upper()] = v
            if k.upper() == "DATA":
                break
        fields, sizes, types = hdr["FIELDS"], [int(s) for s in hdr["SIZE"]], hdr["TYPE"]
        counts = [int(c) for c in hdr.get("COUNT", ["1"] * len(fields))]
        n = int(hdr["POINTS"][0])
        mode = hdr["DATA"][0]
        rows = []
        if mode == "ascii":
            for _ in range(n):
                rows.append([float(v) for v in f.readline().split()])
        elif mode == "binary":
            code = {("F", 4): "f", ("F", 8): "d", ("U", 1): "B", ("U", 2): "H", ("U", 4): "I", ("I", 1): "b", ("I", 2): "h", ("I", 4): "i"}
            st = struct.Struct("<" + "".join(code[(t, s)] * c for t, s, c in zip(types, sizes, counts)))
            for _ in range(n):
                rows.append(list(st.unpack(f.read(st.size))))
        else:
            raise ValueError("unsupported PCD DATA " + mode)
    idx = {name: i for i, name in enumerate(fields)}
    pts = []
    for r in rows:
        rgb = (255, 255, 255)
        if "rgb" in idx:
            v = r[idx["rgb"]]
            packed = struct.unpack("<I", struct.pack("<f", v))[0] if isinstance(v, float) else int(v)
            rgb = ((packed >> 16) & 255, (packed >> 8) & 255, packed & 255)
        pts.append((r[idx["x"]], r[idx["y"]], r[idx["z"]]) + rgb)
    return pts


def write_pcd(path, pts):
    with open(path, "wb") as f:
        f.write(("# .PCD v0.7\nVERSION 0.7\nFIELDS x y z rgb\nSIZE 4 4 4 4\nTYPE F F F U\nCOUNT 1 1 1 1\n"
                 "WIDTH %d\nHEIGHT 1\nVIEWPOINT 0 0 0 1 0 0 0\nPOINTS %d\nDATA binary\n" % (len(pts), len(pts))).encode())
        st = struct.Struct("<fffI")
        for x, y, z, r, g, b in pts:
            f.write(st.pack(x, y, z, (r << 16) | (g << 8) | b))


def read_xyz(path):
    pts = []
    for line in open(path, encoding="utf-8"):
        t = line.split()
        if len(t) >= 3:
            c = [int(float(v)) for v in t[3:6]] if len(t) >= 6 else [255, 255, 255]
            pts.append((float(t[0]), float(t[1]), float(t[2]), *c))
    return pts


def write_xyz(path, pts):
    with open(path, "w", encoding="utf-8") as f:
        for p in pts:
            f.write("%.5f %.5f %.5f %d %d %d\n" % p)


READERS = {"ply": read_ply, "pcd": read_pcd, "xyz": read_xyz}
WRITERS = {"ply": write_ply, "pcd": write_pcd, "xyz": write_xyz}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("input")
    ap.add_argument("output")
    ap.add_argument("--z-up", action="store_true", help="(x,y,z) → (x,z,y)")
    a = ap.parse_args()
    ext_in, ext_out = a.input.rsplit(".", 1)[-1].lower(), a.output.rsplit(".", 1)[-1].lower()
    if ext_in not in READERS or ext_out not in WRITERS:
        sys.exit("supported: .ply .pcd .xyz")
    pts = READERS[ext_in](a.input)
    if a.z_up:
        pts = [(x, z, y, r, g, b) for x, y, z, r, g, b in pts]
    WRITERS[ext_out](a.output, pts)
    print(f"{len(pts)} points: {a.input} -> {a.output}")


if __name__ == "__main__":
    main()
