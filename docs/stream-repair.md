# Stream repair

The repair works on transport stream packets. It never decodes or re-encodes anything, so a lost picture stays lost; it decides what a player gets to see around the hole, the way a receiver's demultiplexer does.

It is a setting per SAT>IP tuner, on by default. Web tuners do not use it. With it off, bytes pass unchanged. What it did is shown per stream on the overview page.

| Problem | What happens |
|---|---|
| Datagrams arrive out of order (RTP over UDP) | Sorted back by sequence number, within a window of 32 datagrams. This one really restores the data, and it is always on. |
| Tuner flags a packet as undecodable (`transport_error_indicator`) | Dropped instead of handed on as garbage. |
| Packet sent twice | The repeat is dropped. |
| Continuity counter shows missing packets | The pid is held back until it can resume cleanly: at the next random access point on a pid that carries them, so a decoder keeps the last good picture instead of showing macroblocks, otherwise at the next unit start. A pid waits at most 20,000 packets for a random access point. |
| Discontinuity flagged by the broadcaster | Respected, not counted as a gap. |
| Lost packet boundary | Bytes are skipped until the sync pattern is found again. |

| Pictures after a keyframe that are shown before it | Left out: they refer to what was lost. |

Continuity counters of what goes out are rewritten per pid, so the output is continuous.

## Late keys

A key that arrives after the broadcaster changed the parity leaves the key of the period before in its place. Packets descrambled with it turn into noise that nothing downstream can detect: the counters are intact and the scrambling bits are cleared. At a seven second crypto period, keys arrived 30 to 390 ms ahead of the change, and in about one change in fifty up to 350 ms after it. Each of those was a burst of damaged pictures and sound.

So the first unit start after a parity change is tried against the key on a copy, and has to come out with a PES start code. If it does not, the service's packets wait, for at most 400 ms, while ECMs keep going to the server. When the key fits, everything goes out whole and in order. If none fits in time, the packets are left out and the freeze frame takes over until one does. A key that never changes passes the test and costs nothing. The wait always applies to a whole service, so one key per service is assumed: on a service with separate keys per stream only the first stream to change is tested.

## Freeze frame

When no picture has passed for 500 ms, because the signal or a key is gone or the repair is waiting for the next keyframe, the last keyframe goes out again every 200 ms with new timestamps. The PCR carries on, and audio tracks that fell silent get silent frames (MPEG audio at any rate, AC-3 in 2.0 and 5.1 at 48 kHz; other formats get none). The picture stays the one the broadcaster coded, nothing is decoded or encoded. After 90 seconds the outage shows as what it is.

Known limits, measured with ffmpeg on recordings of Das Erste HD (720p50) and ProSieben HD (1080i):

- The repeated picture is the last keyframe, not the last picture shown, so the image can jump back by up to one group of pictures.
- H.264 decoders complain about the repeats in their logs, because a repeat carries the frame number and picture order of the original.
- After an outage ffmpeg can drop the first real pictures for about a second (65 pictures in the 720p test, none in the 1080i one), because the repeat's picture order is ahead of them. The freeze lasts that much longer; there are no blocks and nothing stalls. Without repeats it resumes at once. Curing this means rewriting slice headers for each codec.
- How hardware decoders in televisions take the repeats has not been tried.

The frame just before an outage can be cut off by it. A decoder conceals that one for its 20 to 40 ms.

What it cannot do: bring lost data back. SAT>IP has no retransmission or forward error correction over RTP, and the error correction of the broadcast itself happens in the tuner. A bad signal has to be fixed at the dish.
