#!/usr/bin/env python3
"""Generate small synthetic MPEG-TS probe fixtures (no captured media)."""

import os
import struct
import sys

PACKET_SIZE = 188
NULL_PID = 0x1FFF
VIDEO_PID = 0x0101
PMT_PID = 0x0100
PROGRAM = 1


def mpeg_crc32(data):
    crc = 0xFFFFFFFF
    for byte in data:
        crc ^= byte << 24
        for _ in range(8):
            crc = ((crc << 1) ^ (0x04C11DB7 if crc & 0x80000000 else 0)) & 0xFFFFFFFF
    return crc


def with_crc(section):
    return section + struct.pack(">I", mpeg_crc32(section))


def pat(programs=None):
    programs = programs or ((PROGRAM, PMT_PID),)
    section_length = 9 + 4 * len(programs)
    body = bytearray((0x00, 0xB0 | (section_length >> 8), section_length & 0xFF,
                      0x00, 0x01, 0xC1, 0x00, 0x00))
    for program, pmt_pid in programs:
        body.extend((program >> 8, program & 0xFF,
                     0xE0 | (pmt_pid >> 8), pmt_pid & 0xFF))
    return with_crc(body)


def pmt(video_pid=VIDEO_PID, video_type=0x02, program=PROGRAM, version=0):
    section_length = 18
    body = bytes((0x02, 0xB0 | (section_length >> 8), section_length & 0xFF,
                  program >> 8, program & 0xFF, 0xC1 | ((version & 0x1F) << 1), 0x00, 0x00,
                  0xE0 | (video_pid >> 8), video_pid & 0xFF,
                  0xF0, 0x00, video_type, 0xE0 | (video_pid >> 8), video_pid & 0xFF,
                  0xF0, 0x00))
    return with_crc(body)


def sdt():
    service_descriptor = bytes((0x48, 0x03, 0x01, 0x00, 0x00))
    service = bytes((0x00, PROGRAM, 0xC0, 0x80, len(service_descriptor))) + service_descriptor
    payload = bytes((0x00, 0x01, 0xFF)) + service
    section_length = 9 + len(payload)
    body = bytes((0x42, 0xB0 | (section_length >> 8), section_length & 0xFF,
                  0x00, 0x01, 0xC1, 0x00, 0x00)) + payload
    return with_crc(body)


def tdt():
    return bytes((0x70, 0x70, 0x05, 0xEA, 0x60, 0x12, 0x00, 0x00))


def eit():
    payload = bytes((0x00, 0x01, 0x00, 0x01, 0x00, 0x4E,
                     0x00, 0x01, 0xEA, 0x60, 0x12, 0x00, 0x00,
                     0x01, 0x00, 0x00, 0x80, 0x00))
    section_length = 5 + len(payload) + 4
    body = bytes((0x4E, 0xB0 | (section_length >> 8), section_length & 0xFF,
                  0x00, PROGRAM, 0xC1, 0x00, 0x00)) + payload
    return with_crc(body)


def packet(pid, payload, cc, pusi=False, scrambling=0):
    payload = bytes(payload)
    if len(payload) > 184:
        raise ValueError("packet payload exceeds 184 bytes")
    header = bytearray((0x47, ((0x40 if pusi else 0) | ((pid >> 8) & 0x1F)), pid & 0xFF, (scrambling << 6) | 0x10 | (cc & 0x0F)))
    if len(payload) == 184:
        return bytes(header) + payload
    adaptation_length = 183 - len(payload)
    header[3] = (scrambling << 6) | 0x30 | (cc & 0x0F)
    adaptation = bytes((adaptation_length,))
    if adaptation_length:
        adaptation += b"\x00" + (b"\xff" * (adaptation_length - 1))
    return bytes(header) + adaptation + payload


def packetize(pid, data, continuity, pusi_first=True, scrambling=0):
    packets = []
    offset = 0
    first = True
    while offset < len(data):
        chunk = data[offset:offset + 184]
        packets.append(packet(pid, chunk, continuity, pusi=pusi_first and first, scrambling=scrambling))
        offset += len(chunk)
        continuity += 1
        first = False
    return packets


def psi_packets(section, pid, cc=0):
    return packetize(pid, b"\x00" + section, cc, True)


def null_packet(cc=0):
    return packet(NULL_PID, b"\xff" * 184, cc)


def pts_bytes(value=90000):
    return bytes((0x21 | (((value >> 30) & 0x07) << 1),
                  (value >> 22) & 0xFF,
                  (((value >> 15) & 0x7F) << 1) | 1,
                  (value >> 7) & 0xFF,
                  ((value & 0x7F) << 1) | 1))


def video_pes(m2v):
    return b"\x00\x00\x01\xe0\x00\x00\x80\x80\x05" + pts_bytes() + m2v


def write_packets(path, packets, minimum_bytes=0, fill_null=True):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    target_size = max(minimum_bytes, len(packets) * PACKET_SIZE)
    target_size += (-target_size) % PACKET_SIZE
    with open(path, "wb") as output:
        output.write(b"".join(packets))
        if fill_null:
            remaining = (target_size - output.tell()) // PACKET_SIZE
            nulls = null_packet()
            block_count = min(remaining, 4096)
            block = nulls * block_count
            while remaining:
                count = min(remaining, block_count)
                output.write(block[:count * PACKET_SIZE])
                remaining -= count
        else:
            output.truncate(target_size)
    return target_size


def make_budget_fixture(path):
    """Sparse 210 MiB file with valid PMT just after the head-window seek."""
    size = 210 * 1024 * 1024
    with open(path, "wb") as output:
        output.truncate(size)
        start = (size // 30 // PACKET_SIZE + 16) * PACKET_SIZE
        output.seek(start)
        packets = psi_packets(pat(), 0) + psi_packets(pmt(), PMT_PID)
        packets.extend(null_packet(i) for i in range(16))
        output.write(b"".join(packets))


def make_short_first_window_fixture(path, video_bytes):
    """最初の領域がEOFになっても、次の100MiBを延長しないための境界fixture。"""
    size = 120 * 1024 * 1024
    with open(path, "wb") as output:
        output.truncate(size)
        metadata_start = (size // 30 // PACKET_SIZE + 16) * PACKET_SIZE
        output.seek(metadata_start)
        tables = psi_packets(pat(), 0) + psi_packets(pmt(), PMT_PID)
        output.write(b"".join(tables + [null_packet()] * 16))
        # midpoint側はPAT/PMTを読めず、head側の100MiB上限より先に映像を置く。
        output.seek((110 * 1024 * 1024 // PACKET_SIZE) * PACKET_SIZE)
        video = packetize(VIDEO_PID, video_pes(video_bytes), 0, True)
        video += packetize(VIDEO_PID, video_pes(b"\x00\x00\x01\xb7"), len(video), True)
        output.write(b"".join(video + [null_packet()] * 16))


def patch_dimensions(m2v, width, height):
    patched = bytearray(m2v)
    search = 0
    found = False
    while True:
        start = patched.find(b"\x00\x00\x01\xb3", search)
        if start < 0:
            break
        dimensions = ((width & 0xFFF) << 12) | (height & 0xFFF)
        patched[start + 4:start + 7] = dimensions.to_bytes(3, "big")
        search = start + 7
        found = True
    if not found:
        raise ValueError("synthetic MPEG2 stream has no sequence header")
    return bytes(patched)


def make_window_fixture(path, target_bytes, video_bytes=None, video_byte_offset=5 * 1024 * 1024,
                        scrambling=0, audio_only=False, pmt_change=None, multi_service=False,
                        full_metadata=False):
    packet_count = target_bytes // PACKET_SIZE
    packets = [null_packet(i) for i in range(packet_count)]
    metadata_index = (target_bytes // 30 // PACKET_SIZE) + 16
    services = ((1, PMT_PID), (2, 0x0110)) if multi_service else ((PROGRAM, PMT_PID),)
    tables = psi_packets(pat(services), 0)
    for program, pmt_pid in services:
        stream_type = 0x0F if audio_only else 0x02
        tables.extend(psi_packets(pmt(VIDEO_PID, stream_type, program), pmt_pid))
    if full_metadata:
        tables.extend(psi_packets(sdt(), 0x0011))
        tables.extend(psi_packets(tdt(), 0x0014))
        tables.extend(psi_packets(eit(), 0x0012))
    for offset, item in enumerate(tables):
        packets[metadata_index + offset] = item
    if video_bytes is not None:
        video_index = video_byte_offset // PACKET_SIZE
        first_video = packetize(VIDEO_PID, video_pes(video_bytes), 0, True, scrambling)
        # A following PES start flushes the preceding unbounded-length PES in
        # the production PesParser, matching normal elementary-stream framing.
        second_video = packetize(VIDEO_PID, video_pes(b"\x00\x00\x01\xb7"),
                                 len(first_video), True, scrambling)
        for offset, item in enumerate(first_video + second_video):
            packets[video_index + offset] = item
    if pmt_change is not None:
        change_index = (3 * 1024 * 1024) // PACKET_SIZE
        update = psi_packets(pmt(pmt_change, 0x02, PROGRAM, version=1), PMT_PID, 1)
        for offset, item in enumerate(update):
            packets[change_index + offset] = item
    return write_packets(path, packets, target_bytes)


def main(directory):
    here = os.path.dirname(os.path.abspath(__file__))
    m2v_path = os.path.join(here, "fixtures", "synthetic-hd.m2v")
    with open(m2v_path, "rb") as source:
        m2v = source.read()

    # Twelve MiB makes the midpoint pass miss both early metadata and the PES.
    # The fallback starts near 0.4 MiB, sees metadata, and reaches video only
    # after its first 4 MiB input buffer.
    make_window_fixture(os.path.join(directory, "hd-delayed.ts"), 12 * 1024 * 1024, m2v)
    make_window_fixture(os.path.join(directory, "metadata-first-delayed-hd.ts"),
                        12 * 1024 * 1024, m2v, full_metadata=True)

    # The PES begins two packets before the 4 MiB buffer boundary and continues
    # in the next input call, exercising preserved PES assembly across chunks.
    boundary_start = (12 * 1024 * 1024 // 30) + (4 * 1024 * 1024) - 2 * PACKET_SIZE
    make_window_fixture(os.path.join(directory, "hd-window-edge.ts"), 12 * 1024 * 1024,
                        m2v, video_byte_offset=boundary_start)

    make_window_fixture(os.path.join(directory, "low-resolution.ts"), 12 * 1024 * 1024,
                        patch_dimensions(m2v, 320, 1080), video_byte_offset=1024 * 1024)
    make_window_fixture(os.path.join(directory, "zero-height.ts"), 12 * 1024 * 1024,
                        patch_dimensions(m2v, 1440, 0), video_byte_offset=1024 * 1024)
    make_window_fixture(os.path.join(directory, "scrambled-video.ts"), 12 * 1024 * 1024,
                        m2v, video_byte_offset=1024 * 1024, scrambling=2)
    make_window_fixture(os.path.join(directory, "pmt-pid-change.ts"), 12 * 1024 * 1024,
                        m2v, video_byte_offset=1024 * 1024, pmt_change=0x0102)
    make_window_fixture(os.path.join(directory, "multiple-services.ts"), 12 * 1024 * 1024,
                        m2v, video_byte_offset=1024 * 1024, multi_service=True)
    make_window_fixture(os.path.join(directory, "audio-only-no-eit.ts"), 12 * 1024 * 1024,
                        audio_only=True)

    # PMT advertises a video PID, but the fixture contains only null packets.
    make_window_fixture(os.path.join(directory, "advertised-video-unknown.ts"), 12 * 1024 * 1024)
    make_budget_fixture(os.path.join(directory, "budget-exhausted.ts"))
    make_short_first_window_fixture(os.path.join(directory, "short-first-window.ts"), m2v)


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: generate_fixtures.py output-directory")
    main(sys.argv[1])
