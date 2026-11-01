# Plan

Nightwave separates what channels come from (tuners) from what comes out (bouquets as M3U and XMLTV). It passes streams on as the source delivers them.

## Layout

| Project | Holds |
|---|---|
| `Nightwave.Core` | Tuner contracts, configuration, SQLite, bouquets, lineup, M3U and XMLTV writers, stream hub, stream repair, scan coordinator |
| `Nightwave.Tuners.Dvb` | SAT>IP devices, DVB cards through the Linux DVB API, scanning, tuner pool, EPG, dvbapi, descrambling |
| `Nightwave.Tuners.Iptv` | M3U, HDHomeRun devices, per-tuner HTTP clients and proxies, shared web streams, web guides |
| `Nightwave.Server` | `nightwaved`: Blazor interface (Radzen), output endpoints, login |

## Decisions

- Blazor Server with Radzen Blazor (MIT) and an own design system in `wwwroot/app.css` after the Apple Human Interface Guidelines. No WebAssembly, no separate frontend build.
- `config.json` for what an operator sets, SQLite for channels, guide and bouquets. `Upsert` of the guide runs on a tuner's reading loop, so it only queues; a batch goes to the database in one transaction.
- Every stream goes through this server, so credentials stay here, tuners can be shared, proxies cover everything, and scrambled channels can be opened. The exception is a web playlist of segments (HLS) or a non-HTTP address, which cannot be relayed as one byte stream: without a proxy the client is redirected to it, with a proxy it is refused with an error saying why.
- Channel logos stay a separate GPL-3.0 data file, never embedded.
- No transcoding, no deinterlacing.
- Errors name what failed and what to do about it. No free tuner is an error, never a queue. The one exception to the error: a client with a single stream, alone on its tuner, changes channel on that tuner, because players may open the new channel before closing the old one. Clients are told apart by address.

## Phases

1. **Done**: project, SAT>IP backend moved over, M3U, per-tuner proxy, descrambler configuration, bouquet editor, per-bouquet M3U and XMLTV, stream repair, login, database clearing.
2. **Done**: live monitor with stream analysis, channel and transponder tables, design system with light and dark mode.
3. **Next**: channel overrides (name, number, guide id per channel), HLS relay through the proxy, remaining UI languages, translated error messages. Logos per channel are done.
4. **Done, untested on hardware**: DVB-S, DVB-C and DVB-T cards with Unicable, DiSEqC 1.0, 1.1 and 1.2, USALS, cable FRITZ!Box, HDHomeRun devices, HDHomeRun emulation. `ITsTransport` is the seam for cards: a `/dev/dvb` transport delivers a transport stream to the existing tuner pool, feeds, descrambling and EPG, and fills in `SnrDb` and `BitErrorRate` of `SignalReading`.
5. **Against Tvheadend**: recordings and timeshift, a user and access model, more start transponders and a blind scan for more satellites.
