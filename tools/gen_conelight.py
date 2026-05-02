"""
Generate veh_conelight_w.png — 32-facing directional cone light for vehicle headlights.
Output: 128x128 per frame, 32 frames stacked vertically, additive-blend style RGBA.
Facing 0 = SW (WAngle 0), clockwise rotation.
"""

import numpy as np
from PIL import Image
import struct
import zlib
import io
import os

FRAME_W = 256
FRAME_H = 256
NUM_FACINGS = 32

# Elliptical ground-light parameters (beam patch on terrain)
BEAM_FORWARD = 55         # how far forward the ellipse center sits
BEAM_SIGMA_ALONG = 28     # Gaussian sigma along facing direction
BEAM_SIGMA_PERP = 13      # Gaussian sigma perpendicular to facing

# Central source glow (at headlight position)
SOURCE_RADIUS = 8.0

# Slight isometric squash for the cone (isometric y-compression)
ISO_Y_SCALE = 1.0

# Warm white color
COLOR_R = 255
COLOR_G = 235
COLOR_B = 195

OUT_PATH = os.path.join(os.path.dirname(__file__),
    '..', '.modsdk', 'mods', 'cn', 'bits', 'anims', 'veh_conelight_w.png')


def make_frame(facing_index: int) -> Image.Image:
    """
    Build one 128x128 RGBA frame for a given facing index.
    facing_index 0 = SW (screen angle 135°, y-down coords),
    each step = 360/32 = 11.25° clockwise.
    """
    # Screen-space angle: frame 0 = N = 270° (y-down, 0=right, CW)
    screen_angle_deg = 270.0 + facing_index * (360.0 / NUM_FACINGS)
    angle_rad = np.radians(screen_angle_deg)

    # Unit direction vector (screen y-down)
    dx = np.cos(angle_rad)
    dy = np.sin(angle_rad)

    # Pixel grid relative to frame center
    ys, xs = np.mgrid[0:FRAME_H, 0:FRAME_W]
    cx, cy = FRAME_W / 2.0, FRAME_H / 2.0
    px = xs - cx
    py = (ys - cy) / ISO_Y_SCALE   # undo isometric squash for angle math

    dist = np.sqrt(px ** 2 + py ** 2)

    # Project pixel onto facing axis (along) and perpendicular axis (perp)
    along = px * dx + py * dy          # signed: positive = in front
    perp  = px * (-dy) + py * dx      # signed: perpendicular distance

    # Shift ellipse center forward
    along_rel = along - BEAM_FORWARD

    # Elongated Gaussian beam patch
    gauss = np.exp(
        -(along_rel ** 2) / (2 * BEAM_SIGMA_ALONG ** 2)
        -(perp ** 2)       / (2 * BEAM_SIGMA_PERP  ** 2)
    )
    # Soft rolloff behind origin — avoids hard cutoff edge
    front_fade = np.clip(along / 12.0, 0.0, 1.0)
    cone_intensity = gauss * front_fade

    # Small point glow at the headlight source
    source_glow = np.exp(-(dist ** 2) / (SOURCE_RADIUS ** 2))

    intensity = np.maximum(cone_intensity, source_glow)
    intensity = np.clip(intensity, 0.0, 1.0)

    alpha = (intensity * 255).astype(np.uint8)
    r = np.full((FRAME_H, FRAME_W), COLOR_R, dtype=np.uint8)
    g = np.full((FRAME_H, FRAME_W), COLOR_G, dtype=np.uint8)
    b = np.full((FRAME_H, FRAME_W), COLOR_B, dtype=np.uint8)

    return Image.fromarray(np.stack([r, g, b, alpha], axis=-1), 'RGBA')


def write_png_with_text(img: Image.Image, path: str, text_chunks: dict):
    """Save PNG and inject tEXt metadata chunks (FrameSize, FrameAmount)."""
    buf = io.BytesIO()
    img.save(buf, format='PNG', optimize=False)
    raw = buf.getvalue()

    # Split at first IDAT chunk — insert tEXt chunks before it
    signature = raw[:8]
    i = 8
    pre_idat = bytearray()
    post_idat = bytearray()
    past_idat = False

    while i < len(raw):
        length = struct.unpack('>I', raw[i:i+4])[0]
        chunk_type = raw[i+4:i+8]
        chunk_data = raw[i+8:i+8+length]
        crc = raw[i+8+length:i+12+length]
        full_chunk = raw[i:i+12+length]

        if not past_idat:
            if chunk_type == b'IDAT':
                past_idat = True
                # Inject tEXt chunks here
                for key, val in text_chunks.items():
                    kv = key.encode('latin-1') + b'\x00' + val.encode('latin-1')
                    c = zlib.crc32(b'tEXt' + kv) & 0xFFFFFFFF
                    pre_idat += struct.pack('>I', len(kv))
                    pre_idat += b'tEXt'
                    pre_idat += kv
                    pre_idat += struct.pack('>I', c)
            pre_idat += full_chunk
        else:
            post_idat += full_chunk

        i += 12 + length

    with open(path, 'wb') as f:
        f.write(signature)
        f.write(pre_idat)
        f.write(post_idat)


def main():
    print(f'Generating {NUM_FACINGS} facing frames ({FRAME_W}x{FRAME_H}px each)...')
    frames = [make_frame(i) for i in range(NUM_FACINGS)]

    sheet = Image.new('RGBA', (FRAME_W, FRAME_H * NUM_FACINGS), (0, 0, 0, 0))
    for i, frame in enumerate(frames):
        sheet.paste(frame, (0, i * FRAME_H))

    out = os.path.normpath(os.path.join(os.path.dirname(__file__), OUT_PATH
                                        if not os.path.isabs(OUT_PATH) else OUT_PATH))
    out = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                        '..', '.modsdk', 'mods', 'cn', 'bits', 'anims',
                                        'veh_conelight_w.png'))

    write_png_with_text(sheet, out, {
        'FrameSize': f'{FRAME_W},{FRAME_H}',
        'FrameAmount': str(NUM_FACINGS),
    })
    print(f'Saved: {out}')
    print(f'Sheet size: {FRAME_W}x{FRAME_H * NUM_FACINGS}px')


if __name__ == '__main__':
    main()
