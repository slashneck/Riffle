![Drift](docs/banner.png)

Drift is background sound for while you work. Pick a mood and it plays. Close the window and it keeps going from a small tile in your taskbar, right next to the clock.

## What it does

- Moods: Lofi, Ambient, Noise, Jazz, Mellow, Classical, Cloud Era and Memphis
- Noise is rain, waves, brown, pink and white noise, made on your PC with no internet
- Crossfades between stations and songs so nothing ever cuts in hard
- The colors follow the cover of whatever is playing
- A tile in the taskbar next to the tray with the cover, the song and a small level meter
- Shows up in the Windows media flyout and works with your keyboard's media keys
- Sleep timer that slowly fades out
- Thumbs down a song and it won't play again
- Add your own YouTube playlists

## Where the music comes from

Drift doesn't host or store any music.

| Mood | Comes from |
| --- | --- |
| Ambient, Jazz, Mellow, Classical | Public streams from [SomaFM](https://somafm.com), [NTS](https://www.nts.live), [Radio Paradise](https://radioparadise.com), [Radio Swiss Jazz](https://www.radioswissjazz.ch) and [Radio Swiss Classic](https://www.radioswissclassic.ch) |
| Lofi | Playlists from [Lofi Girl](https://lofigirl.com) and [Chillhop](https://chillhop.com) on YouTube |
| Cloud Era, Memphis | Hand picked song lists, each song played from YouTube |
| Noise | Made by Drift |

Most of these stations and labels live on listener support. If you like what you hear, there are links in Settings. Drift is not affiliated with any of them.

About YouTube: Drift plays the audio of YouTube videos without YouTube's own player, using [YoutubeExplode](https://github.com/Tyrrrz/YoutubeExplode). YouTube's terms don't allow that, and YouTube changes things often, so those moods can stop working until Drift is updated. The radio and noise moods don't depend on it.

## Privacy

No accounts, no telemetry, no analytics. Drift only talks to:

- the radio station you're listening to (NTS streams through its provider, Radiomast)
- `somafm.com` for SomaFM's playlist files
- `img.radioparadise.com` for Radio Paradise covers
- `www.nts.live` and `ntslive.co.uk` for NTS artwork
- `youtube.com`, `googlevideo.com` and `ytimg.com`, only when a YouTube mood is playing
- `itunes.apple.com` and `mzstatic.com` to find album covers by artist and title. Turn off "Find album covers" in Settings if you'd rather not.

Anyone you connect to sees your IP address, like with any website. When a YouTube mood plays, Google sees which videos you're playing, the same as watching them without being signed in. No cookies are kept between runs and songs are only held in memory, never saved.

Settings, your playlists and skipped songs live in `%AppData%\Drift\settings.json`. "Start with Windows" adds one entry under the current user's Run key and removes it when you turn it off.

## Using it

| Key | |
| --- | --- |
| Space | Play or pause |
| Left, Right | Previous or next (song or station) |
| Up, Down | Volume |
| Esc | Close the window |

On the taskbar tile, click to open or close Drift, middle click to pause, scroll for volume and right click for more. Clicking anywhere outside Drift closes it too.

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
