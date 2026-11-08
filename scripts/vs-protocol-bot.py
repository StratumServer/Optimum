#!/usr/bin/env python3
"""
Vintage Story protocol bot for load testing.
Implements the minimum handshake to stay connected and generate server-side load.

Protocol: custom protobuf-like (CitoMemoryStream + ProtocolParser).
Framing: 4-byte LE int32 length prefix + payload.
Payload: Packet_Client { Id: varint field 1, sub-packet: field N }.

Handshake sequence:
1. Client sends Packet_Client { Id=1, Identification={...} }
2. Server sends ServerIdentification (accepted/denied)
3. Client sends Packet_Client { Id=11, RequestJoin={} }
4. Server sends level data, chunks, etc.
5. Client sends Packet_Client { Id=26, ClientLoaded={} }
6. Client sends Packet_Client { Id=29, ClientPlaying={} }

After handshake, bot sends:
- Position updates (Id=34) every 50ms
- Chat messages (Id=4) every 5s
- Block interactions (Id=3) every 2s
"""

import socket
import struct
import time
import threading
import random
import sys
import io

# --- Protobuf-like wire format helpers ---

def write_varint(value):
    """Encode unsigned varint."""
    buf = bytearray()
    while value > 0x7F:
        buf.append((value & 0x7F) | 0x80)
        value >>= 7
    buf.append(value & 0x7F)
    return bytes(buf)

def write_field_varint(field_num, value):
    """Write field tag (varint type=0) + varint value."""
    tag = (field_num << 3) | 0  # wire type 0 = varint
    return write_varint(tag) + write_varint(value)

def write_field_bytes(field_num, data):
    """Write field tag (bytes type=2) + length-delimited data."""
    tag = (field_num << 3) | 2  # wire type 2 = length-delimited
    return write_varint(tag) + write_varint(len(data)) + data

def write_field_string(field_num, s):
    """Write string field."""
    return write_field_bytes(field_num, s.encode('utf-8'))

def read_varint(stream):
    """Read unsigned varint from stream."""
    result = 0
    shift = 0
    while True:
        b = stream.read(1)
        if not b:
            return None
        byte = b[0]
        result |= (byte & 0x7F) << shift
        if (byte & 0x80) == 0:
            return result
        shift += 7

# --- Packet builders ---

def build_identification(player_name, player_uid, network_version="1.22.6", game_version="1.22.6"):
    """Build Packet_ClientIdentification sub-message."""
    fields = b""
    fields += write_field_string(1, "2")  # MdProtocolVersion
    fields += write_field_string(2, player_name)
    fields += write_field_string(3, "")  # MpToken (empty for offline)
    fields += write_field_string(4, "")  # ServerPassword
    fields += write_field_string(6, player_uid)
    fields += write_field_varint(7, 12)  # ViewDistance
    fields += write_field_varint(8, 1)   # RenderMetaBlocks
    fields += write_field_string(9, network_version)
    fields += write_field_string(10, game_version)
    return fields

def build_packet_client(packet_id, sub_field_num=None, sub_data=None):
    """Build Packet_Client wrapper: { Id=packet_id, [sub_field=sub_data] }."""
    payload = write_field_varint(1, packet_id)  # field 1 = Id
    if sub_field_num and sub_data:
        payload += write_field_bytes(sub_field_num, sub_data)
    return payload

def frame_packet(payload):
    """Add 4-byte big-endian length prefix (VS wire format). High bit = compression flag (0 = uncompressed)."""
    return struct.pack('>I', len(payload)) + payload

def build_request_join():
    """Packet_Client { Id=11, RequestJoin={Language="en"} }."""
    # Packet_ClientRequestJoin has: field 1 = Language (string)
    # In Packet_Client, RequestJoin is field 5 (wire tag 42 = 5<<3|2).
    request_join_data = write_field_string(1, "en")
    return build_packet_client(11, sub_field_num=5, sub_data=request_join_data)

def build_client_loaded():
    """Packet_Client { Id=26, ClientLoaded={} }."""
    return build_packet_client(26)

def build_client_playing():
    """Packet_Client { Id=29, ClientPlaying={} }."""
    return build_packet_client(29)

def build_chat_message(msg):
    """Packet_Client { Id=4, ChatLine={message} }."""
    chat_data = write_field_string(1, msg)
    return build_packet_client(4, sub_field_num=4, sub_data=chat_data)

# --- Real position updates (UDP-fallback tunnel) ---
#
# The join/level/chat traffic above all rides the reliable TCP channel via
# Packet_Client.Id dispatch (field 1). Per-tick position updates are a
# different message family: Packet_Client.Id=35 (Packet_ClientIdEnum.UdpPacket)
# wraps a Packet_UdpPacket (Packet_Client field 34), which in turn wraps a
# Packet_EntityPosition (Packet_UdpPacket field 2, Id=2 for
# Packet_UdpPacketIdEnum.Position). Real clients send this over an actual UDP
# socket; a client that never opens one is auto-detected by the server after
# ~10s (20 x 500ms polls with no UDP received) and flipped to
# ConnectedClient.FallBackToTcp, after which server->client UDP-format
# traffic is tunneled back over the same TCP socket as
# Packet_Server{Id=79, UdpPacket=...}. Inbound dispatch (Id=35) works over
# TCP from the first packet; only the OUTBOUND (server->client) direction
# needs that ~10s wait before corrections become visible on this socket.
#
# Field tags are all (field_num << 3 | wiretype); wiretype 0 is a raw
# (non-zigzag) unsigned varint of the value's full-width two's-complement bit
# pattern, wiretype 2 is length-delimited bytes. Confirmed against
# Packet_EntityPositionSerializer.cs / ProtocolParser.WriteUInt64/WriteUInt32.

FIXED_POINT_POS = 16384.0   # CollectibleNet.SerializeDoublePrecise scale (X/Y/Z, MotionX/Y/Z)
FIXED_POINT_ANGLE = 1024.0  # CollectibleNet.SerializeFloatPrecise scale (Yaw/Pitch/etc)

def write_field_signed_varint(field_num, value, bits):
    """Field tag (wiretype 0) + the value's raw two's-complement bit pattern as an
    unsigned varint (no zigzag) -- matches ProtocolParser.WriteUInt64/WriteUInt32."""
    mask = (1 << bits) - 1
    return write_field_varint(field_num, int(value) & mask)

def decode_signed_varint(raw, bits):
    """Reverse of write_field_signed_varint: reinterpret an unsigned varint as signed."""
    sign_bit = 1 << (bits - 1)
    if raw & sign_bit:
        return raw - (1 << bits)
    return raw

def build_entity_position(entity_id, x, y, z, yaw=0.0, pitch=0.0, tick=0,
                           position_version=0, motion=(0.0, 0.0, 0.0)):
    """Packet_EntityPosition, field tags per Packet_EntityPositionSerializer.cs."""
    data = b""
    if entity_id:
        data += write_field_varint(1, entity_id)
    data += write_field_signed_varint(2, round(x * FIXED_POINT_POS), 64)
    data += write_field_signed_varint(3, round(y * FIXED_POINT_POS), 64)
    data += write_field_signed_varint(4, round(z * FIXED_POINT_POS), 64)
    if yaw:
        data += write_field_signed_varint(5, round(yaw * FIXED_POINT_ANGLE), 32)
    if pitch:
        data += write_field_signed_varint(6, round(pitch * FIXED_POINT_ANGLE), 32)
    if tick:
        data += write_field_varint(12, tick)
    if position_version:
        data += write_field_varint(13, position_version)
    mx, my, mz = motion
    if mx:
        data += write_field_signed_varint(14, round(mx * FIXED_POINT_POS), 64)
    if my:
        data += write_field_signed_varint(15, round(my * FIXED_POINT_POS), 64)
    if mz:
        data += write_field_signed_varint(16, round(mz * FIXED_POINT_POS), 64)
    return data

def build_position_update_packet(entity_id, x, y, z, **kwargs):
    """Packet_Client { Id=35, UdpPacket={ Id=2, EntityPosition={...} } }."""
    entity_pos = build_entity_position(entity_id, x, y, z, **kwargs)
    udp_packet = write_field_varint(1, 2) + write_field_bytes(2, entity_pos)
    return build_packet_client(35, sub_field_num=34, sub_data=udp_packet)

def read_field_tag(buf, offset):
    """Read one (field_num, wiretype) tag varint starting at offset. Returns (field, wiretype, new_offset)."""
    value = 0
    shift = 0
    while True:
        if offset >= len(buf):
            return None
        b = buf[offset]
        offset += 1
        value |= (b & 0x7F) << shift
        if (b & 0x80) == 0:
            break
        shift += 7
    return (value >> 3, value & 0x7, offset)

def read_varint_at(buf, offset):
    """Read a raw unsigned varint starting at offset. Returns (value, new_offset)."""
    value = 0
    shift = 0
    while True:
        if offset >= len(buf):
            return None
        b = buf[offset]
        offset += 1
        value |= (b & 0x7F) << shift
        if (b & 0x80) == 0:
            return (value, offset)
        shift += 7

def parse_message_fields(buf):
    """Flat (non-recursive) parse of one protobuf-like message body.
    Returns {field_num: raw_unsigned_int} for wiretype 0, {field_num: bytes} for wiretype 2.
    Later occurrences of the same field win (matches the deserializers' overwrite behavior)."""
    fields = {}
    offset = 0
    while offset < len(buf):
        tag = read_field_tag(buf, offset)
        if tag is None:
            break
        field_num, wiretype, offset = tag
        if wiretype == 0:
            result = read_varint_at(buf, offset)
            if result is None:
                break
            fields[field_num], offset = result
        elif wiretype == 2:
            length_result = read_varint_at(buf, offset)
            if length_result is None:
                break
            length, offset = length_result
            if offset + length > len(buf):
                break
            fields[field_num] = buf[offset:offset + length]
            offset += length
        else:
            # Fixed32/Fixed64 wiretypes are not used by this protocol's game-state
            # messages; bail rather than mis-parse the rest of the buffer.
            break
    return fields

def parse_entity_position(buf):
    """Decode a Packet_EntityPosition body into a dict of real-world values."""
    fields = parse_message_fields(buf)
    out = {}
    if 2 in fields:
        out["x"] = decode_signed_varint(fields[2], 64) / FIXED_POINT_POS
    if 3 in fields:
        out["y"] = decode_signed_varint(fields[3], 64) / FIXED_POINT_POS
    if 4 in fields:
        out["z"] = decode_signed_varint(fields[4], 64) / FIXED_POINT_POS
    if 5 in fields:
        out["yaw"] = decode_signed_varint(fields[5], 32) / FIXED_POINT_ANGLE
    if 6 in fields:
        out["pitch"] = decode_signed_varint(fields[6], 32) / FIXED_POINT_ANGLE
    return out

class FrameReader:
    """Buffers raw TCP bytes and yields complete Packet_Server frames.
    Frame = 4-byte big-endian length (bit31 = compression flag, unused for our
    tiny payloads) + payload. Mirrors TcpNetConnection.Send/ReceiveData."""

    def __init__(self, sock):
        self.sock = sock
        self.buf = bytearray()

    def _fill(self, timeout):
        self.sock.settimeout(timeout)
        try:
            chunk = self.sock.recv(65536)
        except socket.timeout:
            return False
        except Exception:
            return False
        if not chunk:
            return False
        self.buf.extend(chunk)
        return True

    def poll_frames(self, duration):
        """Drain the socket for `duration` seconds, yielding (packet_id, payload_fields) for
        every complete Packet_Server frame seen (outer field 90 = Id, field 78 = UdpPacket bytes)."""
        deadline = time.time() + duration
        frames = []
        while time.time() < deadline:
            remaining = max(0.05, deadline - time.time())
            if not self._fill(min(0.5, remaining)):
                if remaining <= 0.05:
                    break
                continue
            while True:
                if len(self.buf) < 4:
                    break
                length = struct.unpack('>I', bytes(self.buf[0:4]))[0] & 0x7FFFFFFF
                if len(self.buf) < 4 + length:
                    break
                payload = bytes(self.buf[4:4 + length])
                del self.buf[0:4 + length]
                fields = parse_message_fields(payload)
                packet_id = fields.get(90)
                frames.append((packet_id, fields))
        return frames

def extract_position_correction(frames):
    """From frames collected by FrameReader.poll_frames, find the most recent
    Packet_Server{Id=79, UdpPacket={EntityPosition={...}}} and decode its position."""
    best = None
    for packet_id, fields in frames:
        if packet_id != 79 or 78 not in fields:
            continue
        udp_fields = parse_message_fields(fields[78])
        if 2 not in udp_fields:
            continue
        pos = parse_entity_position(udp_fields[2])
        if "x" in pos and "y" in pos and "z" in pos:
            best = pos
    return best

# --- Bot class ---

class VSBot:
    def __init__(self, port, bot_id, duration):
        self.port = port
        self.bot_id = bot_id
        self.duration = duration
        self.name = f"LoadBot{bot_id:04d}"
        self.uid = f"loadbot-{bot_id:04d}-uid"
        self.sock = None
        self.connected = False
        self.authenticated = False
        self.fully_joined = False
        self.packets_sent = 0
        self.packets_recv = 0
        self.error = None

    def connect(self):
        try:
            self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            self.sock.settimeout(15)
            self.sock.connect(('127.0.0.1', self.port))
            self.connected = True
            return True
        except Exception as e:
            self.error = str(e)
            return False

    def send_packet(self, payload):
        try:
            data = frame_packet(payload)
            self.sock.sendall(data)
            self.packets_sent += 1
            return True
        except Exception as e:
            self.error = str(e)
            return False

    def recv_any(self, timeout=5):
        """Try to receive any data (we don't parse server responses fully)."""
        try:
            self.sock.settimeout(timeout)
            data = self.sock.recv(8192)
            if data:
                self.packets_recv += 1
            return data
        except socket.timeout:
            return None
        except Exception:
            return None

    def handshake(self):
        """Attempt the VS protocol handshake (full join sequence)."""
        # Step 1: Send identification
        ident = build_identification(self.name, self.uid)
        pkt = build_packet_client(1, sub_field_num=2, sub_data=ident)
        if not self.send_packet(pkt):
            return False

        # Identification begins an asynchronous spawn search. A response here is not enough:
        # RequestJoin dereferences the player entity, and vanilla only creates that entity
        # immediately before Packet_Server{Id=73, ServerReady}. Sending RequestJoin after any
        # earlier packet races the spawn search and disconnects the raw client with a null
        # inventory/entity error.
        reader = FrameReader(self.sock)
        identification_frames = reader.poll_frames(10)
        self.packets_recv += len(identification_frames)
        if not identification_frames:
            self.error = "no server response to identification"
            return False

        self.authenticated = True

        ready_deadline = time.time() + 45
        server_ready = any(packet_id == 73 for packet_id, _ in identification_frames)
        while not server_ready and time.time() < ready_deadline:
            frames = reader.poll_frames(0.5)
            self.packets_recv += len(frames)
            server_ready = any(packet_id == 73 for packet_id, _ in frames)
        if not server_ready:
            self.error = "timed out waiting for ServerReady before RequestJoin"
            return False

        # Step 2: Send RequestJoin (Id=11)
        if not self.send_packet(build_request_join()):
            return False

        # Step 3: Wait for level data (server sends chunks, entity data, etc.). Keep complete
        # frames buffered so TCP packet boundaries cannot make the following messages malformed.
        level_frames = reader.poll_frames(5)
        self.packets_recv += len(level_frames)

        # Step 4: Send ClientLoaded (Id=26)
        if not self.send_packet(build_client_loaded()):
            return False

        # Small delay for server to process
        time.sleep(0.1)

        # Step 5: Send ClientPlaying (Id=29)
        if not self.send_packet(build_client_playing()):
            return False

        self.fully_joined = True
        return True

    def simulate_activity(self):
        """Send game-like packets for self.duration seconds."""
        start = time.time()
        x = random.uniform(-500, 500)
        z = random.uniform(-500, 500)
        y = 70.0

        while (time.time() - start) < self.duration:
            elapsed = time.time() - start

            # Real per-tick position traffic (build_position_update_packet, the UDP-fallback
            # tunnel) is exercised by run_movement_test below, not here; this load-test path
            # stays connection/join/chunk-load focused.
            x += random.uniform(-0.5, 0.5)
            z += random.uniform(-0.5, 0.5)

            # Chat every 5s
            if int(elapsed) % 5 == 0 and random.random() < 0.02:
                self.send_packet(build_chat_message(f"bot{self.bot_id} test"))

            # Drain incoming (don't let TCP buffer fill up)
            self.recv_any(timeout=0.01)

            time.sleep(0.05)

    def disconnect(self):
        if self.sock:
            try:
                self.sock.close()
            except:
                pass

    def run(self):
        """Full bot lifecycle."""
        if not self.connect():
            return

        if not self.handshake():
            self.disconnect()
            return

        try:
            self.simulate_activity()
        finally:
            self.disconnect()


# --- Movement test (real position packets, for StratumMovementGuard testing) ---

def run_movement_test(port, mode="flight", duration=6.0, bot_id=9000):
    """Join one bot and drive real position packets over the UDP-fallback tunnel.

    mode="flight": climbs into open air above spawn and holds altitude past
    FlightMinAirborneSeconds (default 1.75s) with no descent, the exact
    pattern StratumMovementGuard's flight check rubberbands. Requires
    Anticheat.Enabled=true server-side to see a correction; check
    <DataPath>/Logs/server-audit.log for "flight: airborne without descent"
    either way, since that line only comes from the guard actually firing.

    mode="walk": sends small legitimate-looking horizontal steps for
    `duration`s and watches for any UNSOLICITED position correction, to test
    the competing "position sync echo" hypothesis independent of the
    movement guard (works with Anticheat.Enabled=false too, since the sanity
    checks in EntityPosExtensions.SetFromPacket aren't gated by Anticheat).

    Returns a dict report. Prints progress as it runs.
    """
    name = f"LoadBot{bot_id:04d}"
    uid = f"loadbot-{bot_id:04d}-uid"
    report = {"name": name, "mode": mode, "joined": False, "baseline": None, "corrections": []}

    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.settimeout(15)
    print(f"  [{name}] Connecting to port {port}...")
    sock.connect(('127.0.0.1', port))

    ident = build_identification(name, uid)
    sock.sendall(frame_packet(build_packet_client(1, sub_field_num=2, sub_data=ident)))
    sock.settimeout(10)
    if not sock.recv(8192):
        report["error"] = "no response to identification"
        return report

    sock.sendall(frame_packet(build_request_join()))
    drain_start = time.time()
    while time.time() - drain_start < 5:
        sock.settimeout(0.5)
        try:
            if not sock.recv(65536):
                break
        except socket.timeout:
            break

    sock.sendall(frame_packet(build_client_loaded()))
    time.sleep(0.1)
    sock.sendall(frame_packet(build_client_playing()))
    report["joined"] = True
    print(f"  [{name}] Joined. Waiting ~11s for server to flip this connection to FallBackToTcp...")

    reader = FrameReader(sock)
    reader.poll_frames(11.0)  # ServerMain waits 20x500ms polls before marking FallBackToTcp

    print(f"  [{name}] Sending calibration packet (deliberately far off) to learn real spawn position...")
    sock.sendall(frame_packet(build_position_update_packet(1, 0.0, -2000.0, 0.0, tick=1)))
    frames = reader.poll_frames(3.0)
    baseline = extract_position_correction(frames)
    if baseline is None:
        report["error"] = "no position correction received; calibration failed"
        sock.close()
        return report

    bx, by, bz = baseline["x"], baseline["y"], baseline["z"]
    report["baseline"] = baseline
    print(f"  [{name}] Real position: x={bx:.2f} y={by:.2f} z={bz:.2f}")

    if mode == "flight":
        print(f"  [{name}] Ascending 3 blocks and holding altitude for {duration:.1f}s (no descent)...")
        sock.sendall(frame_packet(build_position_update_packet(1, bx, by + 3.0, bz, tick=2)))
        reader.poll_frames(0.25)

        start = time.time()
        tick = 3
        while time.time() - start < duration:
            sock.sendall(frame_packet(build_position_update_packet(1, bx, by + 3.0, bz, tick=tick)))
            frames = reader.poll_frames(0.25)
            correction = extract_position_correction(frames)
            if correction is not None:
                report["corrections"].append(correction)
                print(f"  [{name}] Correction received: {correction}")
            tick += 1
    elif mode == "walk":
        print(f"  [{name}] Walking in small legitimate steps for {duration:.1f}s...")
        start = time.time()
        tick = 2
        wx, wz = bx, bz
        while time.time() - start < duration:
            wx += 0.15
            sock.sendall(frame_packet(build_position_update_packet(1, wx, by, wz, tick=tick)))
            frames = reader.poll_frames(0.25)
            correction = extract_position_correction(frames)
            if correction is not None:
                report["corrections"].append(correction)
                print(f"  [{name}] Unsolicited correction: {correction}")
            tick += 1
    else:
        raise ValueError(f"unknown mode {mode!r}")

    sock.close()
    print(f"  [{name}] Done. {len(report['corrections'])} correction(s) observed during {mode} phase.")
    return report


# --- Main ---

def run_load_test(port, bot_count, duration, label):
    bots = []
    threads = []
    ramp_delay = 0.02  # 20ms between bot connections

    print(f"  [{label}] Ramping {bot_count} bots...")
    for i in range(bot_count):
        bot = VSBot(port, i, duration)
        bots.append(bot)
        t = threading.Thread(target=bot.run, daemon=True)
        t.start()
        threads.append(t)
        time.sleep(ramp_delay)

    print(f"  [{label}] All bots launched. Waiting {duration}s...")
    for t in threads:
        t.join(timeout=duration + 30)

    # Stats
    connected = sum(1 for b in bots if b.connected)
    authenticated = sum(1 for b in bots if b.authenticated)
    fully_joined = sum(1 for b in bots if b.fully_joined)
    total_sent = sum(b.packets_sent for b in bots)
    total_recv = sum(b.packets_recv for b in bots)
    errors = sum(1 for b in bots if b.error)

    print(f"  [{label}] Results:")
    print(f"    Connected:     {connected}/{bot_count}")
    print(f"    Authenticated: {authenticated}/{bot_count}")
    print(f"    Fully joined:  {fully_joined}/{bot_count}")
    print(f"    Packets sent:  {total_sent}")
    print(f"    Packets recv:  {total_recv}")
    print(f"    Errors:        {errors}")

    # Sample errors
    error_samples = [b.error for b in bots if b.error][:5]
    if error_samples:
        print(f"    Error samples: {error_samples}")

    return {
        "connected": connected,
        "authenticated": authenticated,
        "fully_joined": fully_joined,
        "sent": total_sent,
        "recv": total_recv,
        "errors": errors
    }


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(f"Usage: {sys.argv[0]} <port> <bot_count> [duration_sec]")
        print(f"       {sys.argv[0]} <port> flight-test [duration_sec]")
        print(f"       {sys.argv[0]} <port> walk-test [duration_sec]")
        sys.exit(1)

    port = int(sys.argv[1])

    if sys.argv[2] in ("flight-test", "walk-test"):
        mode = "flight" if sys.argv[2] == "flight-test" else "walk"
        duration = float(sys.argv[3]) if len(sys.argv) > 3 else 6.0
        print(f"VS Protocol Movement Test: mode={mode} duration={duration}s on port {port}")
        result = run_movement_test(port, mode=mode, duration=duration)
        print(f"\nResult: {result}")
    else:
        bot_count = int(sys.argv[2])
        duration = int(sys.argv[3]) if len(sys.argv) > 3 else 120

        print(f"VS Protocol Load Test: {bot_count} bots × {duration}s on port {port}")
        result = run_load_test(port, bot_count, duration, "TEST")
        print(f"\nDone. {result['fully_joined']}/{bot_count} bots fully joined.")
        if result["fully_joined"] != bot_count or result["errors"] != 0:
            sys.exit(1)
