# RTP/UDP output

RTP output sends the H.264 video and AAC audio of selected published streams as RTP over UDP, to unicast receivers or
to IPv4/IPv6 multicast groups. It is a stage of the existing media pipeline. It packetises the encoded samples that
media processing already delivers, without decoding, transcoding or depending on FFmpeg, and it leaves HLS packaging
unchanged. It is off unless outputs are configured.

## Architecture

```
RtmpSession ─► MediaPublishSessionHandler ─► RtpOutputHandler ─► HlsPackagingHandler
                                                   │
                                     per output and publication: RtpOutputSession
                                     packetiser ─► RtpPacketRing ─► sender task ─► UDP socket
```

- `RtpOutputHandler` is an `IMediaSessionHandler` decorator. For each sample it packetises into every matching
  output's ring, then forwards the same sample, unchanged and undisposed, to the decorated handler (HLS packaging in
  the service). Ownership is the pipeline's: the decorated handler disposes the sample. Packetisation copies each NAL
  unit or access unit once, from the sample's pooled payload into a pre-allocated datagram buffer, before the sample
  is forwarded.
- `AddRtpOutput()` (in `IngestServiceHost`, after `AddHlsPackaging()` and before `AddMediaProcessing()`) captures the
  previous `IMediaSessionHandler` registration and decides when it is first resolved. With no outputs configured,
  that handler itself is the `IMediaSessionHandler`, so the composition and its behaviour are exactly as without RTP.
- An output selects one published stream by `Application` and `Stream` (the publisher credential's non-secret
  `Name`). Several outputs may select the same stream; each has its own session, sockets, SSRCs and queue.
- `RtpOutputSession` exists per output and publication. It is created when the publication starts (after the
  decorated handler accepted it) and stopped when it ends, or at host shutdown.
  - The pipeline thread packetises into a single-producer, single-consumer ring of pre-allocated slots (`RtpPacketRing`).
  - One sender task per session drains the ring with `Socket.SendToAsync` on one socket, which is opened once per
    session and never per packet. It is woken through an allocation-free signal.
  - The sender sends RTCP.
- Media calls never wait for the network. A slow or failed output cannot slow the publisher, HLS or other outputs.
  Its queue fills and its media is dropped, as described in [Loss policy](#loss-policy-and-backpressure).

| Type | Responsibility |
|------|----------------|
| `Configuration/RtpOptions`, `RtpOutputOptions` | Validated settings (`Rtp` section) |
| `Rtp/RtpOutputServiceCollectionExtensions` | `AddRtpOutput()`: the resolve-time decorator registration |
| `Rtp/RtpOutputHandler` | Decorator; per-publication sessions; failure containment; management view |
| `Rtp/RtpOutputSession` | One output for one publication: drop policy, sender loop, RTCP, statistics, lifecycle |
| `Rtp/H264RtpPacketizer` | RFC 6184 packetisation |
| `Rtp/AacRtpPacketizer` | RFC 3640 packetisation and AAC RTP timestamps |
| `Rtp/RtpStream`, `RtpHeader` | RFC 3550 header, SSRC, sequence number and timestamp state of one RTP session |
| `Rtp/RtcpPackets` | Sender Report, SDES CNAME and BYE |
| `Rtp/RtpPacketRing`, `RtpSenderSignal` | Bounded hand-off from the pipeline to the sender |
| `Rtp/RtpUdpSocket` | Socket creation and options; transient versus terminal error classification |
| `Rtp/RtpSdp` | SDP for receivers |
| `Rtp/RtpMetrics` | Meter `RTSP.IngestService.Rtp` |

### Session lifecycle

| State | Meaning |
|-------|---------|
| `Starting` | Created for a publication; socket not open yet |
| `Active` | Socket open; media is packetised and sent |
| `Stopping` | The publication ended; queued packets are drained |
| `Stopped` | Drained (or the drain timed out); RTCP BYE sent; socket closed |
| `Failed` | The socket could not be opened, a terminal send error occurred, or packetisation failed unexpectedly; the output sends nothing more for this publication |

Stopping drains the queue and sends BYE, bounded by 2 seconds. A sender that cannot finish (for example a send that
does not complete) is cancelled and its socket closed (event 9406); the packets still queued are counted as dropped. A
failed output recovers at the next publication of its stream.

## Codecs

Only the codecs media processing delivers are supported: H.264 (AVC) and AAC. Other codecs never reach this stage.

### H.264 (RFC 6184)

`packetization-mode=1` (non-interleaved), clock rate 90 kHz, dynamic payload type `VideoPayloadType` (default 96).

- **Single NAL unit packets:** used for a NAL unit that fits in a packet but does not fit together with its neighbour.
- **STAP-A (type 24):** used for consecutive NAL units of an access unit that fit in one packet together. The STAP-A
  header carries the highest NRI and the F bit of its units.
- **FU-A (type 28):** used for a NAL unit larger than the payload limit. The S bit is set on the first fragment and the
  E bit on the last; the FU indicator keeps the NAL unit's F and NRI.
- **Marker bit:** set on the last packet of each access unit.
- **Timestamp:** the presentation time (DTS + composition offset) at 90 kHz. Packets of B-frame streams are therefore
  sent in decode order with non-monotonic timestamps, as RFC 6184 requires.
- **Parameter sets:** FLV carries SPS and PPS out of band. Before each access unit with an IDR slice, the configured
  SPS and PPS that the access unit does not contain are sent in band. They follow an access unit delimiter if there
  is one, usually in the same STAP-A. A receiver can therefore start at any IDR, with or without the SDP. The SDP
  also carries them in `sprop-parameter-sets`.
- **Not sent:** NAL unit types 0 and 24–31 (unspecified, and reserved for RTP packet types).

### AAC (RFC 3640, mpeg4-generic AAC-hbr)

`mode=AAC-hbr; sizelength=13; indexlength=3; indexdeltalength=3`, dynamic payload type `AudioPayloadType` (default 97).

- **AU headers:** every packet starts with `AU-headers-length` = 16 bits, followed by one AU header holding a 13-bit
  AU-size and a 3-bit AU-Index of 0.
- **One access unit per packet:** AUs arrive one at a time, and aggregating them would add latency.
- **Fragmentation:** an AU larger than one payload is split across packets (RFC 3640 section 3.2.3). Every fragment
  repeats the AU header with the whole AU's size, and the marker bit is set only on the last fragment.
- **Size limit:** an AU above 8191 bytes cannot be described with `sizelength=13` and is dropped (`too_large`). AAC-LC
  access units are far smaller.
- **Clock rate:** the AAC sampling rate by default, or `AudioClockRate` if set (for example 90000).
- **Timestamps:** each AU is stamped one frame (1024 or 960 samples, scaled to the clock rate) after the previous one
  while the millisecond source timestamps agree within half a frame. Otherwise (the first AU, a gap or a regression)
  the AU is stamped with its source timestamp. Millisecond rounding therefore never accumulates.
- **Configuration changes:** an audio configuration change keeps the RTP session. A change of clock rate starts a new
  one with a new SSRC.
- **SDP:** `config` carries the AudioSpecificConfig.

### RTP header (RFC 3550)

- **Fixed fields:** version 2, no padding, no extension, no CSRCs.
- **Per track:** each track is its own RTP session with its own SSRC, initial sequence number and timestamp offset, all
  random (`RandomNumberGenerator`). The audio SSRC differs from the video SSRC.
- **Wraparound:** sequence numbers wrap at 2^16 and timestamps at 2^32 (offset + media ticks, unchecked).
- **No sequence gaps for dropped AUs:** an access unit that is not queued does not consume sequence numbers.
- **Packet size:** at most `Mtu − 20 − 8` bytes (IPv4) or `Mtu − 40 − 8` (IPv6), RTP header included, so no datagram
  is IP-fragmented. With the default MTU of 1500 the limit is 1472 or 1452 bytes.

### RTCP and synchronisation

- **Ports:** RTCP goes to each track's RTP port + 1 (RTP ports must be even).
- **Compound packet:** a Sender Report (NTP wall clock, RTP timestamp, packet and octet counts) followed by SDES with
  a CNAME that the output's tracks share. On stop, BYE is appended.
- **When reports are sent:** for each track as soon as it has sent its first packets, and then every 5 s. On stop, a
  final report with BYE is sent.
- **A/V synchronisation:** the Sender Reports of both tracks map one media clock to the wall clock. That media clock
  is the shared decode timeline, taken from audio when the stream has audio, so receivers can synchronise the tracks.
  FFmpeg cannot timestamp a track until its first report, which is why the first report is not delayed to the interval.
- **Disabling:** `RtcpEnabled=false` disables RTCP; receivers then cannot synchronise the tracks.
- **Received RTCP:** RTCP sent by receivers (for example Receiver Reports) is not read.

## Transport

- **Addresses:** literal IPv4 and IPv6 addresses only; no DNS, so starting an output never waits on name resolution.
- **Unicast:** the destination is any unicast address; TTL or hop limit is `TimeToLive`, default 64.
- **Multicast:** the destination must be an IPv4 group in `224.0.0.0/4` or an IPv6 group in `ff00::/8`.
  - TTL or hop limit is `TimeToLive`, default 1, so packets stay on the local network.
  - The sender does not join the group. Joining is the receivers' role, and their IGMP or MLD membership
    is what network equipment acts on.
  - `MulticastLoopback` (default off) also delivers the packets to receivers on the sending host.
- **Interface selection:** `SourceAddress` binds the socket to a local address. For multicast it also selects the
  outgoing interface: on IPv4, the interface owning that address (`IP_MULTICAST_IF`); on IPv6, the interface index
  of the address (from its scope ID, or looked up). Without it, the operating system's routing table chooses, which
  for multicast is usually the default-route interface. Set it explicitly on multi-homed hosts.
- **Sockets:** one socket per output session, bound to `SourceAddress` (or the family's any-address) on an ephemeral
  port. RTP and RTCP of both tracks are sent from it. `SendBufferSize` sets `SO_SNDBUF`. On Windows, ICMP
  port-unreachable reports are disabled on the socket (`SIO_UDP_CONNRESET`) so they cannot fail later sends. QoS
  marking, when enabled, is the first option set on it (see [QoS marking](#qos-marking-dscp-and-ecn)).
- **Multiple destinations:** configure several outputs for the same stream. A multicast group is itself
  one-to-many.

### Errors

| Kind | Socket errors | Effect |
|------|---------------|--------|
| Transient | No buffer space, would block, try again, interrupted, host/network unreachable or down, connection refused or reset, timed out, network reset | That packet is dropped and counted (`send_failed`, `rtp.send.errors{kind=transient}`); logged once per session (9404); sending continues |
| Terminal | Any other socket error, a closed socket | The session fails (9403); its queue is discarded (`output_unavailable`); other outputs and the pipeline continue |
| Start | Socket creation, binding or option failure, such as a `SourceAddress` that no interface has | The session fails (9402) and sends nothing for this publication |
| Packetisation | An unexpected exception while packetising | The output fails for the publication (9408); the sample still reaches HLS |

## QoS marking (DSCP and ECN)

An output can mark its packets so that network equipment can classify them. Marking sets the DS field of every
datagram the output sends: the IPv4 Type of Service byte or the IPv6 Traffic Class byte.

```
  bit   7   6   5   4   3   2   1   0
      +---+---+---+---+---+---+---+---+
      |          DSCP         |  ECN  |      DS field = (DSCP << 2) | ECN
      +---+---+---+---+---+---+---+---+
```

**Marking is a classification, not a guarantee.** Packets get better treatment only where every device on the path
trusts the marking and has a queue configured for it. Many networks, and most of the internet, ignore DSCP, re-mark
it to 0 at their edge, or bleach ECN. Measure delivery on the real path.

### What is marked

- **Per output, not per packet.** The value is set once on the output session's socket, before its first send. RTP
  and RTCP of both tracks share that socket, so they carry the same value. Video and audio cannot be marked
  differently within one output; use two outputs (one per port) for that.
- **One implementation for unicast and multicast.** The same code marks both, for both families: `IP_TOS` for IPv4,
  and `IPV6_TCLASS` for IPv6.
- **Verified before use.** After setting the option, the service reads it back. A different value is a failure;
  the service never assumes that marking took effect.
- **No leakage between sessions.** Every output session (one output, one publication) opens its own socket with
  its own output's policy, and closes it when the session ends. There is no shared or pooled socket that could carry
  another output's or an earlier publication's marking.
- **Disabled means untouched.** With marking disabled (the default), the option is never set, so the operating
  system's default applies. That is 0 on Windows and Linux unless host policy changes it.
- **Cost.** One socket option and one read-back per session start. Nothing happens per packet, and the send path is
  unchanged (see [Performance](#performance)).

### Configuration

`Rtp:Transport:Qos` is the default for every output. `Rtp:Outputs:N:Qos` overrides it one field at a time, so an
output can, for example, inherit `Dscp` and set only `Ecn`, or set only `Enabled=false`.

| Setting (`Rtp:Transport:Qos:` or `Rtp:Outputs:N:Qos:`) | Default | Valid values |
|-------------------------------------------------------|---------|--------------|
| `Enabled` | `false` | `true` or `false`; with `false` no option is set, whatever the other values |
| `Dscp` | `0` | 0–63, any code point, standard or local |
| `Ecn` | `0` (Not-ECT) | 0–3: 0 Not-ECT, 1 ECT(1), 2 ECT(0), 3 CE |

Values are numbers. Binding is strict: unknown keys, out-of-range values and names such as `"AF41"` are
configuration errors (exit code 78), reported with the setting's path. Setting DSCP never changes ECN, and the
reverse. The DS field is always composed as `(Dscp << 2) | Ecn`.

Common DSCP values (RFC 2474, RFC 2597, RFC 3246, RFC 4594):

| Name | DSCP | DS field with ECN 0 | Typical use |
|------|------|---------------------|-------------|
| CS0 / default | 0 | `0x00` | Best effort |
| AF11 | 10 | `0x28` | High-throughput data |
| AF21 | 18 | `0x48` | Low-latency data |
| AF31 | 26 | `0x68` | Multimedia streaming |
| CS3 | 24 | `0x60` | Broadcast video (RFC 4594) |
| AF41 | 34 | `0x88` | Multimedia conferencing, interactive video |
| EF | 46 | `0xB8` | Telephony; strict-priority queues |
| CS6 | 48 | `0xC0` | Network control; normally not for media |

The code exposes the same names as constants (`RtpDscp.AF41` and so on). They are conveniences, not a restriction.

#### ECN

ECT(0) or ECT(1) tells ECN-capable routers that they may mark the packet CE instead of dropping it under congestion.
**This service does not react to congestion.** It reads no RTCP feedback, negotiates no ECN for RTP (RFC 6679), and
does not change its sending rate. ECT therefore does not provide congestion control or guarantee that congestion is
reported to anyone. Leave `Ecn` at 0 unless the receivers and network are known to use ECN for this traffic. CE (3)
is never set automatically. Configuring it is allowed, for testing, but logs a warning (9411): RFC 3168 reserves CE
for routers.

#### Examples

Every output marked AF41:

```json
"Rtp": {
  "Transport": { "Qos": { "Enabled": true, "Dscp": 34 } },
  "Outputs": [
    { "Name": "decoder-1", "Application": "live", "Stream": "camera1",
      "Destination": "192.0.2.50", "VideoPort": 5004, "AudioPort": 5006 },
    { "Name": "lan-camera1", "Application": "live", "Stream": "camera1", "Mode": "Multicast",
      "Destination": "239.255.10.1", "VideoPort": 5004, "SourceAddress": "192.0.2.10" }
  ]
}
```

Per-output overrides: one output EF, one inheriting AF41 with ECT(0) (Linux only, see below), one unmarked:

```json
"Rtp": {
  "Transport": { "Qos": { "Enabled": true, "Dscp": 34 } },
  "Outputs": [
    { "Name": "contribution", "Application": "live", "Stream": "camera1",
      "Destination": "192.0.2.60", "VideoPort": 5004, "Qos": { "Dscp": 46 } },
    { "Name": "ecn-lab", "Application": "live", "Stream": "camera1",
      "Destination": "192.0.2.61", "VideoPort": 5004, "Qos": { "Ecn": 2 } },
    { "Name": "internet", "Application": "live", "Stream": "camera1",
      "Destination": "198.51.100.7", "VideoPort": 5004, "Qos": { "Enabled": false } }
  ]
}
```

Environment variables: `Rtp__Transport__Qos__Enabled=true`, `Rtp__Transport__Qos__Dscp=34`,
`Rtp__Outputs__0__Qos__Ecn=0`.

Policies to choose from:

| Situation | Suggested marking |
|-----------|-------------------|
| Managed LAN or WAN with a video class | AF41 (34), or the code point the network team assigns |
| Broadcast-style distribution per RFC 4594 | CS3 (24) |
| Low-bitrate contribution over a strict-priority queue sized for it | EF (46); an unpoliced EF flood starves other priority traffic |
| Path you do not control (internet, third-party carrier) | Disabled; marking is usually cleared or ignored |

### Operating system behaviour

The table summarises behaviour verified on the development hosts by test and capture: unprivileged processes, and
the byte the receiving kernel reported for each datagram. Evidence is in `.artifacts/rtp-qos/`.

| | Windows (build 26100) | Linux (6.18, WSL2) |
|---|---|---|
| IPv4 option | `IP_TOS` (level `IPPROTO_IP`) | `IP_TOS` (level `IPPROTO_IP`) |
| IPv6 option | `IPV6_TCLASS` = 39 (level 41) | `IPV6_TCLASS` = 67 (level 41) |
| DSCP 0–63, ECN 0 | Accepted, read back and sent as set: unicast, IPv4 multicast and IPv6 multicast | Accepted, read back and sent as set: unicast, IPv4 multicast and IPv6 multicast |
| ECN bits non-zero | **Rejected**: the option fails with `WSAEINVAL` | Accepted and sent as set, including CE |
| Privileges | None needed (DSCP 63 included) | None needed (DSCP 63 included) |

- **Windows and ECN.** Windows does not let applications set ECN bits through these options. A policy with
  `Ecn` other than 0 therefore fails on Windows. The startup check logs this (9410), and every session of that output
  fails to start (9402), with the message "Windows does not accept ECN bits through this socket option. Set Ecn to 0
  for this output on Windows; DSCP marking alone is supported." The output sends nothing rather than sending
  unmarked packets. Other outputs are unaffected.
- **Windows policy-based QoS.** Group Policy QoS policies (and qWAVE) act below the socket and can override or
  clear the value the socket sets. A read-back from the socket cannot detect that. If marking does not appear on the
  wire, check `Get-NetQosPolicy` and Group Policy. This interaction was not tested.
- **Linux.** Every tested combination, ECN bits included, was accepted for both families. For IPv6, the kernel uses
  the socket's traffic class for unicast and multicast alike. `tc`, nftables or iptables `DSCP` targets can still rewrite it on egress.
- **Other operating systems.** The service supports Windows and Linux. Elsewhere, IPv6 marking fails with a clear
  error, because the option number is platform-specific.
- **Interfaces and virtual switches.** Marking is per socket, so it applies on whichever interface the packet
  leaves. The value was preserved across the Hyper-V virtual switch between a Windows host and a WSL2 VM, for IPv4
  unicast and IPv6 link-local unicast and multicast. Other virtual switches, tunnels and VPNs may copy, clear or
  re-mark the inner value. IPsec and most tunnels copy DSCP to the outer header by default; check yours.

### Diagnostics

- **At startup,** for every enabled output, the service applies the policy to a probe socket of the output's family.
  Success logs 9409 with the family, DSCP, ECN and verified byte. Failure logs 9410 with the reason. CE logs 9411.
  The startup summary (9407) counts the outputs with marking.
- **Per session,** 9400 states the policy and the byte the socket reports, for example "QoS DSCP 34 (AF41), ECN 0
  (Not-ECT), traffic class 0x88, applied (socket reports 0x88)", or "QoS off (operating system default)". A failure
  to apply it fails the session with 9402 and the reason.
- **Management.** `/health/rtp` reports each output's resolved policy as `qos`, for example
  `{"enabled":true,"dscp":34,"ecn":0,"trafficClass":136}` or `{"enabled":false}`. Each session reports the byte its
  socket holds as `trafficClass`, which is absent when marking is off.
- **Metrics.** No QoS metrics are added. The policy is static configuration, and the per-output state is in the
  management inventory.
- **Checking the wire:** `tcpdump -ni eth0 -v udp port 5004` prints `tos 0x88` (IPv4) or `class 0x88` (IPv6). In
  Wireshark, use the column `ip.dsfield.dscp` or `ipv6.tclass.dscp`.

### Network requirements

For marking to change anything, every hop that may queue the stream must:
1. **Trust** the marking on its ingress port, rather than re-marking it to 0 (the default on many access switches).
2. **Classify** the code point into a queue.
3. **Schedule** that queue with priority or a guaranteed share, sized for the stream's bitrate, including bursts:
   an IDR at line rate is far above the average bitrate.

The following are illustrative starting points; adapt names, interfaces and rates.

MikroTik RouterOS 7. Mark packets by DSCP, then prioritise them in a queue tree under a limited parent. Priority has
effect only where the parent has a `max-limit`.

```
/ip firewall mangle
add chain=postrouting out-interface=ether1 protocol=udp dscp=34 action=mark-packet new-packet-mark=rtp-video passthrough=no
/queue tree
add name=wan-out parent=ether1 max-limit=95M
add name=rtp-video parent=wan-out packet-mark=rtp-video priority=1 limit-at=20M max-limit=95M
add name=other parent=wan-out packet-mark=no-mark priority=8 max-limit=95M
```

RouterOS does not re-mark DSCP unless configured to. To clear marking arriving from untrusted sources, add a
`prerouting` rule with `action=change-dscp new-dscp=0` on that ingress interface.

Linux `tc`. Send the marked stream into the highest-priority band of a `prio` qdisc on the egress interface:

```
tc qdisc add dev eth0 root handle 1: prio bands 3
tc filter add dev eth0 parent 1: protocol ip   prio 1 u32 match ip dsfield 0x88 0xfc flowid 1:1
tc filter add dev eth0 parent 1: protocol ipv6 prio 2 u32 match ip6 priority 0x88 0xfc flowid 1:1
```

The mask `0xfc` matches DSCP and ignores ECN. For a rate-limited link, use `htb` with a guaranteed class for the
stream, or `cake diffserv4`, which classifies by DSCP itself.

Cisco IOS / IOS XE (MQC). Classify by DSCP and give the class a share, or priority, on the egress interface:

```
class-map match-any RTP-MEDIA
 match dscp af41 cs3
class-map match-any RTP-PRIORITY
 match dscp ef
policy-map WAN-EGRESS
 class RTP-PRIORITY
  priority percent 20
 class RTP-MEDIA
  bandwidth percent 40
 class class-default
  fair-queue
interface GigabitEthernet0/1
 service-policy output WAN-EGRESS
```

Catalyst access switches must also trust DSCP on the service's port. On older platforms with `mls qos` enabled, use
`mls qos trust dscp` on the port, otherwise the switch re-marks to 0. Catalyst 9000 (IOS XE) trusts DSCP by default.

## Loss policy and backpressure

Each session's ring holds `QueueCapacity` packets (default 2048, about 3 MB at a 1472-byte limit), allocated once.

- **Whole access units:** an access unit is queued whole or not at all. If its packets do not fit, it is dropped
  (`queue_full`), and so are its sequence numbers, so the stream has no holes inside an access unit.
- **Video after a loss:** video waits for the next IDR access unit (`awaiting_keyframe`), so receivers never decode
  from a missing reference. Video also starts at the first IDR of a publication.
- **Audio:** audio continues independently.
- **The pipeline never waits for RTP.** The RTMP connection's backpressure comes only from HLS packaging, exactly as
  before. Size `QueueCapacity` for bursts: an IDR at 1472 bytes per packet needs about one slot per 1.4 KB.

## Configuration

`Rtp` section. Binding is strict, values are validated at start and invalid values exit with code 78. Options are
read once at startup.

| Setting (`Rtp:Outputs:N:`) | Default | Valid values |
|----------------------------|---------|--------------|
| `Name` | Required | Letter or digit followed by up to 63 of `A-Z a-z 0-9 . _ -`; unique |
| `Application` | Required | Same pattern |
| `Stream` | Required | Same pattern; the publisher credential's `Name` |
| `Mode` | `Unicast` | `Unicast` or `Multicast` |
| `Destination` | Required | Literal IP; unicast (not multicast, broadcast or unspecified) or, for `Multicast`, a group in `224.0.0.0/4` or `ff00::/8` |
| `VideoPort` | Unset (no video) | Even, 2–65534 |
| `AudioPort` | Unset (no audio) | Even, 2–65534, different from `VideoPort` |
| `VideoPayloadType` | `96` | 96–127 |
| `AudioPayloadType` | `97` | 96–127 |
| `AudioClockRate` | AAC sampling rate | 8000–192000 |
| `Mtu` | `1500` | 576–9216 |
| `TimeToLive` | 64 unicast, 1 multicast | 1–255 |
| `SourceAddress` | Unset (OS choice) | Literal local unicast IP of the destination's family |
| `MulticastLoopback` | `false` | Multicast only |
| `SendBufferSize` | OS default | 65536–67108864 |
| `QueueCapacity` | `2048` | 64–65536 packets |
| `RtcpEnabled` | `true` | `true` or `false` |
| `Qos` | Inherits `Rtp:Transport:Qos` | `Enabled`, `Dscp`, `Ecn`; see [QoS marking](#qos-marking-dscp-and-ecn) |

At most 64 outputs, and at least one of `VideoPort` and `AudioPort`. Configuration contains only addresses and
ports, so it can live in `appsettings.json`, an environment-specific file or environment variables
(`Rtp__Outputs__0__Destination=...`).

### Unicast example

```json
"Rtp": {
  "Outputs": [
    { "Name": "decoder-1", "Application": "live", "Stream": "camera1",
      "Destination": "192.0.2.50", "VideoPort": 5004, "AudioPort": 5006 }
  ]
}
```

### Multicast examples

```json
"Rtp": {
  "Outputs": [
    { "Name": "lan-camera1", "Application": "live", "Stream": "camera1", "Mode": "Multicast",
      "Destination": "239.255.10.1", "VideoPort": 5004, "AudioPort": 5006,
      "SourceAddress": "192.0.2.10", "TimeToLive": 4 },
    { "Name": "lan6-camera1", "Application": "live", "Stream": "camera1", "Mode": "Multicast",
      "Destination": "ff15::1:1", "VideoPort": 5008, "AudioPort": 5010, "SourceAddress": "2001:db8::10" }
  ]
}
```

Use administratively scoped groups: IPv4 `239.0.0.0/8` (organisation-local), or IPv6 with scope 5 (site-local, `ff05::`
or `ff15::`) or scope 8 (organisation-local, `ff08::` or `ff18::`). Avoid `224.0.0.0/24` and link-local `ff02::`,
which hosts and switches treat specially.

## SDP and management routes

Receivers need an SDP description. It is served on the existing management routes, with the same loopback-only
access, `HEAD`/405 handling and `Management:Enabled` switch.

| Route | Response |
|-------|----------|
| `GET /health/rtp` | 200 JSON: every configured output (name, application, stream, mode, destination, ports, resolved `qos` policy) and, while its stream is published, its session (state, start time, SSRCs, packets and bytes sent, packets dropped, send errors, RTCP packets, current bitrate and packet rate, and the socket's `trafficClass` when marked) |
| `GET /health/rtp/{name}.sdp` | 200 `application/sdp` for the output's current publication; 503 `{"status":"not_publishing"}` when its stream is not published, `{"status":"not_described"}` before any configured track is; 404 for an unknown output |

Without configured outputs the RTP routes are 404. The SDP describes only tracks that are both configured for the
output and present in the stream. It changes when the stream's configuration does (its `o=` version follows the
stream description) and on each new publication (new SSRCs). Fetch it again after a publisher reconnects.

```
v=0
o=- 6979411395791180432 3 IN IP4 0.0.0.0
s=decoder-1
c=IN IP4 192.0.2.50
t=0 0
a=tool:RTSP.IngestService
m=video 5004 RTP/AVP 96
a=rtpmap:96 H264/90000
a=fmtp:96 packetization-mode=1;profile-level-id=64001f;sprop-parameter-sets=<SPS base64>,<PPS base64>
a=ssrc:2803116482 cname:2487286fc93dc9b2@rtsp-ingest
m=audio 5006 RTP/AVP 97
a=rtpmap:97 MPEG4-GENERIC/48000/2
a=fmtp:97 streamtype=5;profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config=1190
a=ssrc:1181920021 cname:2487286fc93dc9b2@rtsp-ingest
```

For IPv4 multicast `c=` carries the TTL (`c=IN IP4 239.255.10.1/4`).

## Receivers

On the service host, fetch the SDP. If the receiver is elsewhere, copy the file there; the route is loopback-only.

```
curl -s http://127.0.0.1:8080/health/rtp/decoder-1.sdp -o decoder-1.sdp
```

FFmpeg and ffplay (the protocol whitelist is required for SDP files):

```
ffplay -protocol_whitelist file,udp,rtp -i decoder-1.sdp
ffmpeg -protocol_whitelist file,udp,rtp -i decoder-1.sdp -c:v libx264 -c:a aac out.mp4
```

Decode rather than stream-copy. RTP has no decode timestamps, so `-c copy` of H.264 with B-frames into a container
fails with "unknown timestamp". That is a property of RTP, not of this output.

VLC: open the `.sdp` file (Media → Open File, or `vlc decoder-1.sdp`). For multicast, the receiver joins the group
named in `c=` on its default interface. Set the interface with `--miface` (VLC), or `localaddr=` in an FFmpeg
`rtp://` URL.

## MTU and network notes

- **`Mtu`:** set it to the smallest IP MTU on the path. With tunnels or VPNs (IPsec, WireGuard, GRE, VXLAN) it is below
  1500: use 1400 or the measured value. Jumbo-frame networks can use up to 9216. Packets are never larger than
  `Mtu − IP − UDP`, so a correct value avoids IP fragmentation, which loses the whole datagram if any fragment is lost.
- **IGMP and MLD:** on switched LANs, IGMP snooping (IPv4) or MLD snooping (IPv6) forwards a group only to ports
  with joined receivers. Without snooping, or without an IGMP querier on the VLAN, multicast floods every port, or
  stops reaching receivers when the membership times out. Enable a querier, on the router or on a switch.
- **Routing multicast:** crossing routers needs multicast routing (PIM-SM with a rendezvous point, or PIM-SSM for
  `232.0.0.0/8` and `ff3x::/32` with source-specific joins) and a `TimeToLive` above the hop count. The default TTL
  of 1 never leaves the local network.
- **Firewalls:** allow outbound UDP to the RTP and RTCP ports. Receivers must allow inbound UDP on them.

## Metrics

Meter `RTSP.IngestService.Rtp`. Tags are fixed enumerations. Output names, streams, addresses, ports and SSRCs are
never tags.

| Instrument | Type | Tags |
|------------|------|------|
| `rtp.sessions.active` | UpDownCounter | `mode`: `unicast`, `multicast` |
| `rtp.packets.sent` | Counter | `track`: `video`, `audio`; `mode` |
| `rtp.bytes.sent` | Counter (bytes, RTP header included) | `track`; `mode` |
| `rtp.packets.dropped` | Counter | `track`; `reason`: `queue_full`, `awaiting_keyframe`, `too_large`, `send_failed`, `output_unavailable` |
| `rtp.send.errors` | Counter | `kind`: `transient`, `terminal` |
| `rtp.rtcp.sent` | Counter | — |
| `rtp.bitrate` | Gauge (bit/s) | — (sum over active sessions) |
| `rtp.packet_rate` | Gauge (packets/s) | — (sum over active sessions) |

Drop counts for access units that were never packetised are estimated from their size. Per-output values are in
`/health/rtp`.

## Events

| ID | Level | Event |
|----|-------|-------|
| 9400 | Information | Output session started (output, session, mode, destination and family, ports, video SSRC, packet size limit, QoS policy and the byte the socket reports) |
| 9401 | Information | Output session ended (final state, packets and bytes sent, packets dropped, send errors) |
| 9402 | Error | Output session could not start (socket, including QoS marking that cannot be applied); it sends nothing for this publication |
| 9403 | Error | Terminal send failure; the session sends nothing more |
| 9404 | Warning | First transient send error of a session; further ones are counted, not logged |
| 9405 | Warning | First dropped media of each reason and track in a session (`awaiting_keyframe` is not logged) |
| 9406 | Warning | Stop drain timed out; queued packets discarded |
| 9407 | Information | RTP output enabled at startup (outputs, of which multicast, of which with QoS marking) |
| 9408 | Error | Unexpected packetisation failure; the output stops for this publication |
| 9409 | Information | At startup: an output's QoS marking was applied and verified on a probe socket (family, DSCP, ECN, byte) |
| 9410 | Error | At startup: an output's QoS marking cannot be applied on this system; its sessions will fail to start (reason) |
| 9411 | Warning | At startup: an output is configured to send ECN CE (3) |

Nothing is logged per packet.

## Performance

These are steady-state properties, verified by tests and measured by `RtpBenchmarks`, which is explicit and not part
of the default test run.

- **No per-packet allocation.** Packetisation writes RTP packets in place into pre-allocated ring slots, and the
  sender sends them from that memory with pre-serialised `SocketAddress` destinations. Wake-ups use a reusable
  `IValueTaskSource`. `Packetization_SteadyState_DoesNotAllocate` asserts zero bytes allocated across 1000 access
  units, with STAP-A, FU-A and AAC packets.
- **One copy.** Each payload byte is copied once, from the pooled RTMP payload into the datagram buffer. No sample is
  retained after its media call.
- **No locks on the media path.** The producer and consumer share only volatile indices. Statistics use interlocked
  counters, batched per drain. Rates are computed by the sender once per second.

Measured on the development machine (Windows, .NET 10, Release, one run):

| Benchmark | Result |
|-----------|--------|
| `Packetization_Throughput`: a 25 KB H.264 access unit (AUD, SEI, IDR) plus a 400-byte AAC access unit per iteration, packetised and drained | About 1.15 million access-unit pairs/s (23 million packets/s) on one core; 0 bytes allocated |
| `Session_LoopbackSendThroughput`: 25 fps, 25 KB frames plus AAC, offered 300 times faster than real time to one session over loopback UDP | Sender saturates at about 54,000 packets/s (554 Mbit/s); the excess is dropped as whole access units (`queue_full`); 0 send errors |

QoS marking adds nothing per packet: the option is set once per session, and the send path is the same code. In three
paired Release runs on Windows, the loopback session sent 60,000–65,000 packets/s unmarked and 49,000–74,000 marked
(EF), within run-to-run noise. Packetisation stayed at 0 bytes allocated. The Linux Release run (WSL2) measured
55,000 and 67,000 packets/s respectively.

A 5 Mbit/s stream is about 450 packets/s, so one output uses about 1% of the sender's loopback capacity. Real
network interfaces and multicast to many receivers cost more per packet than loopback. Run the benchmarks with
(the variable is optional and appends the results to a file):

```
$env:RTSP_RTP_BENCHMARK_OUTPUT = "benchmark.txt"
dotnet test --project tests/RTSP.IngestService.Tests -c Release -- --filter-class "*RtpBenchmarks" --explicit only
```

## Tests

| Test class | Covers |
|------------|--------|
| `RtpPacketizationTests` | RFC 3550 header fields; sequence and timestamp wraparound; random SSRC, sequence and offset; MTU limits; single NAL unit, STAP-A, FU-A and mixed access units within the limit; reassembly; queue-full atomicity; in-band parameter sets on real pipeline samples; AAC AU headers, fragmentation, size limits and frame-exact timestamps; the RTCP compound; zero-allocation steady state |
| `RtpOutputTests` | Unicast over IPv4 and IPv6 loopback with RTCP SR/BYE and SDP; several outputs per stream and stream selection; identical forwarding to the inner handler with every pooled buffer returned; backpressure, whole-AU drops and IDR resumption without blocking the pipeline; transient and terminal errors; start failure; failure isolation between outputs; concurrent publications; bounded stop on a fake clock; shutdown; source-address binding and interface lookup; error classification; multicast to a group joined on loopback (skipped where the host cannot loop multicast back) |
| `RtpOptionsTests` | Defaults; binding of every setting; every range, pattern and cross-field rule; strict binding; composition with and without outputs; exit at startup on invalid configuration |
| `RtpManagementTests` | Inventory and SDP before, during and after a publication; the `qos` policy and session `trafficClass`; 404, 405, HEAD and loopback-only access; routes absent without outputs |
| `RtpQosTests` | QoS defaults, DSCP and ECN boundaries and out-of-range values at both levels, strict binding, field-wise overrides; every DSCP/ECN combination composed and decomposed, with DSCP and ECN changed independently; real sockets for IPv4 and IPv6 unicast and multicast, enabled (value read back) and disabled (OS default kept); Windows ECN rejection with an actionable error; unicast and multicast sessions whose RTP and RTCP arrive with the configured byte (checked per datagram on Linux through `IP_RECVTOS`/`IPV6_RECVTCLASS`, by socket read-back on Windows); isolation of QoS failures; no policy carried between outputs or publications; identical packetisation with and without marking; startup events |
| `RtpFfmpegInteropTests` | The production host with an FFmpeg RTMP publish: SDP from the management route, ffprobe identifies H.264 320x240 and AAC 48 kHz stereo, FFmpeg decodes 3 s of both, HLS still served, session ended after the publish |
| `TelemetryExportTests` | The RTP meter's eight instruments exported unchanged with fixed tags, including from real traffic |

None of these tests depends on multicast network infrastructure. Multicast delivery is tested only on the loopback
interface of the test host.

## Limitations

- **Codecs:** H.264 and AAC only, because they are the only codecs media processing delivers. There is no H.265, AV1,
  Opus or G.711.
- **H.264 packetisation modes:** only `packetization-mode=1`. There is no interleaved mode (STAP-B, MTAP, FU-B), and
  SDP parameters such as `max-*` are not set.
- **No RTP session protocols:** there is no RTSP server, SAP announcement, SRTP or encryption, RTP header extensions,
  FEC (RFC 5109, SMPTE 2022-1) or retransmission. RTCP from receivers is ignored, so there is no congestion control;
  size the network for the stream's bitrate.
- **Sender-side multicast only:** sources are not filtered, and receivers choose any-source or source-specific joins.
  The sender never joins groups.
- **Restarts:** outputs are read at startup; adding, removing or changing one requires a restart.
- **One SDP per publication:** SSRCs and timestamp offsets are new for every publication, so a receiver started with
  an old SDP sees an SSRC change. FFmpeg and VLC follow it, but stricter receivers may need the new SDP.
- **SDP access is loopback-only:** copy SDP files to remote receivers.
- **QoS:** one marking per output (RTP and RTCP, video and audio). Windows cannot set ECN bits. ECN is not
  negotiated and congestion marks are not acted on. Host policy (Windows policy-based QoS, Linux `tc`/nftables)
  can rewrite the value after the socket sets it, which the read-back cannot detect.

## Implemented and planned

Implemented in this milestone:
- RTP/UDP output of H.264 (RFC 6184 single NAL unit, STAP-A, FU-A) and AAC (RFC 3640 AAC-hbr with fragmentation).
- Unicast and multicast over IPv4 and IPv6, with RTCP SR, SDES and BYE.
- Validated configuration.
- The bounded, lossy per-output queue.
- Failure isolation.
- Metrics, logs and the management inventory and SDP routes.
- FFmpeg interoperability tests.

Added by the UDP transport QoS milestone:
- DSCP and ECN marking per output, for IPv4 and IPv6, unicast and multicast, with verification and diagnostics.

Not implemented (possible later work, not committed):
- An RTSP server or SAP announcements.
- SRTP.
- FEC and retransmission.
- Receiver-report statistics.
- Runtime reconfiguration of outputs.
