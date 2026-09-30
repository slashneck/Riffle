![Drift](docs/banner.png)

Drift plays calm radio while you work. Pick a mood, press play and close the window. The music keeps going from a small tile in your taskbar, right next to the clock.

## What it does

- Moods for Lofi, Ambient (drone, space, cosmic, focus, nature), Jazz, Mellow and Classical
- Crossfades between stations so nothing ever cuts in hard
- The colors follow the cover of whatever is playing
- A tile in the taskbar next to the tray with the cover, the song and a small level meter
- Shows up in the Windows media flyout and works with your keyboard's media keys
- Starts quiet and stays out of the way

## Where the music comes from

Drift doesn't host or store any music. It plays the public streams of these stations:

| Station | In Drift |
| --- | --- |
| [SomaFM](https://somafm.com) | Fluid, Groove Salad, Drone Zone, Deep Space One, Synphaera, Sonic Universe, Lush |
| [NTS](https://www.nts.live) | Low Key, Slow Focus, Field Recordings, Expansions, Sheet Music |
| [Radio Paradise](https://radioparadise.com) | Mellow Mix, Global Mix |
| [Radio Swiss Jazz](https://www.radioswissjazz.ch) and [Radio Swiss Classic](https://www.radioswissclassic.ch) | Swiss Jazz, Swiss Classic |

Most of them run on listener donations. If you like what you hear, support them, there's a link in the menu. Drift is not affiliated with any of these stations.

## Privacy

No accounts, no telemetry, no analytics. Drift only talks to:

- the station you're listening to (NTS streams through its provider, Radiomast)
- `somafm.com` for SomaFM's playlist files
- `img.radioparadise.com` for Radio Paradise covers
- `www.nts.live` and `ntslive.co.uk` for NTS mixtape artwork
- `itunes.apple.com` and `mzstatic.com` to find album covers by artist and title. Turn off "Find album covers" in the menu if you'd rather not.

Settings live in `%AppData%\Drift\settings.json`. That's the only thing Drift writes to disk.

## Using it

| Key | |
| --- | --- |
| Space | Play or pause |
| Left, Right | Previous or next station |
| Up, Down | Volume |
| Esc | Tuck the window away |

On the taskbar tile, click to open Drift, middle click to pause, scroll for volume and right click for more.

## About the taskbar tile

Windows 11 took away the old way for apps to put things inside the taskbar. The tile is a small window that sits over the empty part of the taskbar next to the tray. Drift keeps it lined up, out of the way of your app buttons, and hides it when a game or video goes fullscreen or the taskbar auto hides.

## Building

You need Windows 11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet run --project src/Drift
```

For a standalone `Drift.exe` in `dist\Drift`:

```
powershell -ExecutionPolicy Bypass -File build/publish.ps1
```

## License

MIT. The music belongs to the artists and the stations.
