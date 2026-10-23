"""Generate tiny synthetic clips; ffmpeg is needed only to regenerate fixtures."""
from pathlib import Path
import struct
import subprocess

root = Path(__file__).parent / "Fixtures"
root.mkdir(exist_ok=True)
plain = root / "untagged.mp4"
subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i",
                "color=c=blue:s=64x32:d=0.1", "-c:v", "libx264", "-an", str(plain)], check=True)

def box(kind, data):
    return struct.pack(">I", len(data) + 8) + kind.encode("ascii") + data

def inject(data, path, payload):
    pos = 0
    while pos + 8 <= len(data):
        size = struct.unpack(">I", data[pos:pos + 4])[0]
        kind = data[pos + 4:pos + 8].decode("ascii")
        assert size >= 8 and pos + size <= len(data)
        if kind == path[0]:
            body = data[pos + 8:pos + size]
            if len(path) == 1:
                body += payload
            else:
                skip = 8 if kind == "stsd" else 0
                body = body[:skip] + inject(body[skip:], path[1:], payload)
            return data[:pos] + box(kind, body) + data[pos + size:]
        pos += size
    raise ValueError(path)

data = plain.read_bytes()
assert data.find(b"mdat") < data.find(b"moov"), "moov must follow media to preserve offsets"
xml = b'''<?xml version="1.0"?><rdf:SphericalVideo
xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
xmlns:GSpherical="http://ns.google.com/videos/1.0/spherical/">
<GSpherical:Spherical>true</GSpherical:Spherical>
<GSpherical:Stitched>true</GSpherical:Stitched>
<GSpherical:StitchingSoftware>MediaFlattener fixture</GSpherical:StitchingSoftware>
<GSpherical:ProjectionType>equirectangular</GSpherical:ProjectionType>
</rdf:SphericalVideo>'''
uuid = bytes.fromhex("ffcc8263f8554a938814587a02521fdd")
(root / "spherical-v1.mp4").write_bytes(inject(data, ["moov", "trak"], box("uuid", uuid + xml)))
projection = box("proj", box("prhd", bytes(16)) + box("equi", bytes(20)))
spherical = box("sv3d", box("svhd", bytes(4) + b"MediaFlattener fixture\0") + projection)
sample_path = ["moov", "trak", "mdia", "minf", "stbl", "stsd", "avc1"]
(root / "spherical-v2.mp4").write_bytes(inject(data, sample_path, spherical))
(root / "flat-stereo.mp4").write_bytes(inject(data, sample_path, box("st3d", bytes(4) + b"\2")))
subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(root / "spherical-v2.mp4"),
                "-c", "copy", str(root / "spherical.mkv")], check=True)
