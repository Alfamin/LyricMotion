# Lyric Motion

A plugin for the [Noctis](https://github.com/heartached/Noctis) music player that makes word-synced
lyrics move with the singing.

- Each word floats up a little as it is sung and stays there until its line is over.
- Its letters grow a touch, one after another, as the highlight reaches them, and shrink back, one
  after another, when the word is done.
- How one word hands over to the next can be chosen: every word a step of its own (smooth or
  crisp), or the motion passing along the line in one piece.
- Held words (sung for 0.6 seconds or longer) grow more and get a soft glow, then settle gently.
- Persian, Arabic, Hebrew and other right-to-left lyrics are noticed by themselves and laid out from
  the right: words run from the right, lines sit against the right edge, each word fills in from the
  right. Letters in these scripts are joined up, so such a word moves in one piece.
- Nothing shakes, bounces or overshoots. Everything follows the highlight Noctis already draws, so
  the motion is exactly in time with it.
- It works on the lyrics page and in the lyrics side panel.

The plugin only adds motion. It never changes the lyrics, their timing or your music files.

It needs lyrics with a time for every word (for example an `.elrc` file next to the song). Songs
with line-timed lyrics are shown by Noctis as before.
[WordLyrics](https://github.com/Alfamin/WordLyrics) makes word-timed lyrics for a whole library.

## Installing it

1. Download [LyricMotion-for-Noctis.zip](https://github.com/Alfamin/LyricMotion/raw/main/LyricMotion-for-Noctis.zip).
2. In Noctis: Settings → Plugins → turn on **Community plugins**, then **Install from file…** and pick
   the zip.
3. Switch **Lyric Motion** on and restart Noctis.

To update, install the new zip the same way and restart Noctis.

Made for Noctis 1.5.3 or newer; tested on 1.5.8 and 1.5.9 on Windows.

## Settings

| Setting | Default | What it does |
|---|---|---|
| Animation | Word by word (smooth) | Word by word: every word is a step of its own; smooth lets the end of a word run a moment into the next one, crisp finishes every word exactly on its note. Flowing: the motion passes along the line in one piece. Off: Noctis' own animation. |
| Motion strength | Balanced | Subtle, Balanced or Expressive: how far words float and grow, and how much held words grow and glow. |
| Letter by letter | on | A word lights up and grows one letter at a time, with the highlight. Off: each word moves in one piece. |
| Held word starts at (ms) | 600 | A word sung at least this long counts as held: it grows more and glows. |
| Glow | on | Soft light behind a held word's letters; stronger the longer the word is held. |
| Right-to-left lyrics | on | Lays Persian, Arabic, Hebrew and other right-to-left lyrics out from the right. |
| Also animate the lyrics side panel | on | Off: only the lyrics page moves. |

## Building it

No .NET SDK is needed: a Roslyn `csc.exe` and Noctis' own files are enough.

1. Get the C# compiler: download the NuGet package
   [microsoft.net.compilers.toolset](https://www.nuget.org/packages/Microsoft.Net.Compilers.Toolset/5.0.0)
   (a `.nupkg` is a zip) and unpack it into `tools\roslyn`, so that `tools\roslyn\tasks\net472\csc.exe`
   exists. Or pass any other `csc.exe` with `-Compiler`.
2. Build:

   ```
   powershell -ExecutionPolicy Bypass -File build.ps1 -Noctis C:\path\to\Noctis
   ```

   This writes `dist\lyric-motion-<version>.zip` and the same file as `LyricMotion-for-Noctis.zip`.

The plugin has to be built against a Noctis whose plugin kit is the one `src\plugin.json` names in
`apiVersion` (1.1, which Noctis 1.5.8 has). Built against a newer kit, it would be refused by every
older Noctis; built against 1.1 it loads in the newer ones too. Noctis updates itself, so keep a copy
of the 1.5.8 release for building and pass it with `-Noctis`. The script stops with a message if the
kit does not match.

## How it works

The plugin kit has no lyrics API, so the plugin looks at the lyric lines Noctis has on screen and reads
the same values the lyrics page uses, by name. If a Noctis update renames something, the plugin steps
aside and the lyrics look as they do without it.

The word being sung is redrawn exactly over Noctis' own text. Until the highlight reaches a word,
nothing about it changes. From then on each letter is a small picture, drawn once at the size the
screen shows it at, which can be moved and scaled by fractions of a pixel without the text flickering.
When a line is over, its words are handed back to Noctis untouched.

| File | What is in it |
|---|---|
| `src\Plugin.cs` | The entry point: finds the lyric views Noctis has on screen. |
| `src\Surface.cs` | One list of lyric lines; runs a frame loop only while something moves. |
| `src\Rig.cs` | The motion of one line and of each of its words. |
| `src\LetterLayer.cs` | Draws a sung word as letters (or in one piece) and moves them. |
| `src\Motion.cs` | The curves and springs: nothing overshoots. |
| `src\Tuning.cs` | Every number that shapes the motion, from the settings. |
| `src\Flow.cs` | Right-to-left detection and layout. |
| `src\Access.cs` | Reads Noctis' line and word objects by name. |
