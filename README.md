![Riffle](docs/banner.png)

Riffle is a true shuffle for your SoundCloud. Paste your playlists, merge as many as you like, and it plays every song once, in a properly random order, from a small tile in your taskbar.

## What it does

- **True shuffle.** Your whole playlist, not the first few songs. Every order is equally likely and every song plays once before anything repeats.
- **Merge playlists.** Select several and they play as one. A song that's in two of them only plays once, re-uploads included.
- **Lives in the taskbar.** A tile next to the tray with the cover, the song and a level meter. Hover it for pause and next, scroll it for volume.
- **Crossfades,** a five band equalizer, and an option to even out loudness.
- **A real queue.** Search it, play something next, and see your history.
- **Never again.** Thumbs down a song and it stays out for good.
- **Your place is kept.** Close it and pick up mid round tomorrow.
- **Other shuffle styles.** Spread artists apart, or hear what you've played least first. Give a playlist more weight if you want more of it.
- Colors that follow the cover, a sleep timer, media keys and the Windows media flyout.

## How it plays

Riffle doesn't download or store music. It runs SoundCloud's official embedded player in the background and tells it what to play. Artists and uploaders stay credited, and every song links back to SoundCloud.

Riffle is not made by or affiliated with SoundCloud.

## Ads

"Block ads" turns away requests to known ad networks, like a browser ad blocker would. It's on by default and you can switch it off in Settings. Ads pay artists and SoundCloud, so consider SoundCloud Go if you listen a lot.

## Privacy

No telemetry and no analytics. Riffle only talks to SoundCloud: `soundcloud.com`, `w.soundcloud.com`, `api-widget.soundcloud.com`, `dwt.soundcloud.com`, `sndcdn.com` and `media-streaming.soundcloud.cloud`. Since the player is SoundCloud's own, SoundCloud sees what you play, like on their site.

Signing in is optional and happens on SoundCloud's own page. Riffle never sees your password. The session stays in Riffle's browser profile on your PC, and Sign out in Settings removes it.

Your library, settings and listening counts live in `%AppData%\Riffle`, and the browser profile in `%LocalAppData%\Riffle`. Song details only, never audio.

## Using it

| Key | |
| --- | --- |
| Space | Play or pause |
| Left, Right | Previous or next |
| Up, Down | Volume |
| Esc | Close the window |

On the taskbar tile, click to open or close Riffle, middle click to pause, scroll for volume and right click for more. Clicking anywhere outside Riffle closes it too.

## Building

You need Windows 11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Playback uses the WebView2 runtime that comes with Windows 11.

```
dotnet run --project src/Riffle
```

For a standalone `Riffle.exe` in `dist\Riffle`:

```
powershell -ExecutionPolicy Bypass -File build/publish.ps1
```

## License

MIT. The music belongs to the artists.
