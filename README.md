# Waverider

A Windows-native music companion that combines:

- **Real-time visualizations** of whatever Spotify is playing — using **per-process WASAPI loopback** (`ActivateAudioInterfaceAsync` + `AUDIOCLIENT_ACTIVATION_PARAMS{ ProcessLoopback }`) so Discord voice / YouTube / browsers don't bleed in. Falls back to default-device loopback (NAudio) on demand.
- **Spotify integration** — sign in with PKCE OAuth, see now-playing metadata, control transport.
- **Spinning-disc album art** — the album art is a spinning **vinyl record** or **CD** (your pick) that spins up on play and coasts to a stop on pause.
- **Two mini players** — an always-on-top overlay tucked in the corner of your screen: either the visualizer (360 × 130) or a borderless **spinning disc** that is just the disc, with no window around it.
- **Custom theming** — drop JSON files into the `Themes/` folder next to the executable.

Built with the recommended stack: **WinUI 3 / Windows App SDK 1.8**, **NAudio**, **FftSharp**, **SpotifyAPI-Web**, **SkiaSharp** (`SKSwapChainPanel`).

## Stack

| Layer | Library |
|---|---|
| Audio capture | Custom `ProcessLoopbackCapture` (WinRT activation API) for Spotify-only; NAudio `WasapiLoopbackCapture` for full system mode |
| FFT analysis | FftSharp |
| Spotify auth + REST | SpotifyAPI-Web + SpotifyAPI-Web.Auth |
| Rendering | SkiaSharp + SkiaSharp.Views.WinUI |
| Window/UI | WinUI 3 (Mica + Acrylic backdrops) |

## Prerequisites

- Windows 10 1903+ or Windows 11
- .NET 9 SDK
- A Spotify Developer app — free at <https://developer.spotify.com/dashboard>

### Spotify app setup

1. Create an app on the Spotify Developer dashboard.
2. Add the redirect URI **exactly** as: `http://127.0.0.1:5544/callback`
3. Copy the *Client ID* into `src/Waverider/AppHost.cs`:
   ```csharp
   public const string SpotifyClientId = "YOUR_CLIENT_ID";
   ```
   No client secret is required (we use PKCE).

## Build & run

```pwsh
dotnet restore
dotnet build -c Debug -p:Platform=x64
dotnet run --project src/Waverider -p:Platform=x64
```

The project is configured as **unpackaged + self-contained**, so no MSIX, no developer cert, no Windows App Runtime installer required — `dotnet run` just works.

## Usage

- **Sign in** — top-right "Sign in" button opens a browser window, completes PKCE auth, and persists the refresh token to `%LocalAppData%\Waverider\token.json`.
- **Visualizer** — pick from `Bars`, `Waveform`, `Radial`, `Spectrogram` in the dropdown over the canvas.
- **Mini player** — click the mini icon in the top-right; the window resizes to a corner overlay with always-on-top enabled. Click the same icon again to restore (the full window returns to where it was).
- **Album art disc** — open the gear; under *Album art disc* pick **Vinyl record** or **Compact disc**. It replaces the album art in the full window and is what the disc mini player shows.
- **Mini player type** — open the gear; under *Mini player* pick **Visualizer** or **Spinning disc**. You can also switch while in mini mode: the visualizer mini has a swap button on hover, and the disc has it in its right-click menu.
- **Disc mini player** — the window is clipped to the disc, with the spindle hole cut clear through. Hover the disc for the track and artist scrolling past; click to play / pause; press and drag to move it; right-click for previous / next, vinyl / CD, switch player, and restore.
- **Capture source** — open the gear; under *Audio capture* pick **Spotify only** (default — uses per-process WASAPI loopback, ignores Discord / browsers / YouTube) or **All system audio** (default-device loopback). The Spotify-only path watches for the Spotify process and starts/stops automatically when you launch or close it.
- **Themes** — open the gear, pick from `Spotify`, `Synthwave`, `Mica Light`, `Solar Flare`, or drop your own JSON in the `Themes/` folder.

## Custom themes

Each theme is a JSON file that controls backdrop, element theme, and the visualizer palette:

```json
{
  "name": "My Theme",
  "backdrop": "Mica",       // Mica | MicaAlt | Acrylic | None
  "elementTheme": "Dark",   // Default | Light | Dark
  "accent": "#1DB954",
  "primary": "#1DB954",
  "secondary": "#1ED760",
  "background": "#0A0A0A",
  "surface": "#181818",
  "foreground": "#FFFFFF",
  "muted": "#B3B3B3"
}
```

Files in `Themes/*.json` are copied next to the executable on build.

## Project layout

```
src/Waverider/
  Audio/                  WASAPI capture + FFT
  Disc/                   Spinning vinyl / CD: Skia renderer (shadow, rotating body, fixed sheen) + DiscView
  Spotify/                PKCE auth + now-playing polling
  Visualization/          IVisualizer + 4 implementations + SKSwapChainPanel host
  Theming/                Theme model + JSON loader
  ViewModels/             PlayerViewModel
  Views/                  PlayerView (full) + MiniPlayerView (visualizer overlay) + MiniDiscView (disc overlay)
  Themes/                 Built-in JSON themes
  AppHost.cs              DI container
  MainWindow.xaml(.cs)    Shell + mini/full toggle
```

## Backlog

- **Tonearm** on the vinyl disc: swings onto the record on play, lifts off on pause. Deliberately left out of the first disc pass.

## Known gaps / next steps

- The Spotify polling is a 2-second timer; for instant playback-state changes we'd need to subscribe to Spotify's WebPlayback SDK or use platform `GlobalSystemMediaTransportControls` as a fallback.
- Authoring custom themes from inside the app (color picker, save) is not wired up — edit JSON directly for now.
- Vortice.Windows / D3D11 path is not wired in. Drop it under `Visualization/` when you outgrow Skia for 3D shaders.
- Volume slider isn't surfaced; transport is play/pause/skip/shuffle/repeat only.
- Per-process loopback requires Windows 10 build 20348+ / Windows 11. On older builds the Spotify-only path will fall back to system loopback automatically.

## Built-in player & library (added)

- **This app is its own Spotify device.** A hidden WebView2 hosts Spotify's Web Playback SDK (Widevine), so playback no longer needs the Spotify desktop client. Requires Premium. The visualizer captures this app's own audio (*Settings → Audio capture → This app*).
- **Library drawer** (title-bar icon): search, liked songs, playlists, queue; open playlists/albums/artists; per-track menu for play / queue / like / add to playlist.
- **Transport bar:** seek, shuffle, repeat, like the current track, device picker, volume.
- **Auto-reconnect:** if the built-in player drops, it reconnects (then reloads) on its own.
- **Visualizer editor:** *Customize* next to the visualizer picker. User visualizers save to `%LocalAppData%\Waverider\Visualizers`.
- **Themes** may set optional `panel`, `stage` (visualizer background) and `stroke` colors; they're derived if omitted.
- **Spotify API limits (dev mode, 2026):** playlist contents load only for playlists you own/collaborate on; search returns 10 per page; no artist top-tracks.
- **Crash log:** `%LocalAppData%\Waverider\crash.log`; player status in `webplayer.log`.
