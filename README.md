<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/logo/wipriss.svg">
    <img src="assets/logo/wipriss-dark.svg" alt="Wipriss logo" width="120">
  </picture>
</p>

<h1 align="center">Wipriss</h1>

<p align="center">One tiny, fast island at the top of your Windows screen with the everyday tools you need.</p>

> **Status: early development.** Nothing to download yet. The plan is in [PLAN.md](PLAN.md) (in Russian).

## Why

I got tired of installing a dozen separate utilities, so I'm building one place for the tools I use every day. I made it for myself and I'm sharing it with anyone who finds it useful.

Wipriss is built with the help of AI (Claude Code).

## Principles

- **Fast and light.** Uses almost no memory and 0% CPU when idle, so it runs fine on weak PCs. Heavy work runs in separate processes that exit when done.
- **Private.** No telemetry, no accounts. Your data stays on your PC and is encrypted. See [SECURITY.md](SECURITY.md).
- **Modular.** Turn any module off and it isn't loaded at all. New modules plug in without touching the core.

## Planned modules

| Module | What it does |
|---|---|
| Reminders | Banner (center, corner or none) at the exact time, with or without sound. No polling. |
| Clipboard history | Last 20 copies, filter by text or images, skips password-manager content. |
| Processes | Your running apps, with end task. |
| Ports | Which process holds which port, and a one-click stop for the whole process tree. |
| Smart cleaner | Finds leftovers of uninstalled programs by checking what is actually installed, explains each item, deletes to a quarantine you can restore from. |
| Screenshot & mini recording | PrtScreen replacement: area, window, scrolling capture, copy to clipboard, short screen recordings. |
| Voice typing | Press a key, speak, the text appears at the cursor. Runs fully offline. |
| Notes | Quick notes that survive crashes and reboots. |
| Image converter | Format, resolution, target file size, background removal. |
| PDF / XLSX / CSV reader | View and edit these files in a light window, optionally on double-click. |

## Thanks

Wipriss stands on the shoulders of these open-source projects. Their ideas shaped it. Any code taken from them keeps its original copyright and license and is listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

- [nabyscape](https://github.com/BillyNabil/nabyscape) by BillyNabil: a dynamic-island dock for Windows (MIT).
- [Filee](https://github.com/KnifeLemon/Filee) by Myeonggu Kim (KnifeLemon): a drag-and-drop file converter (MIT).
- [OpenScreen](https://github.com/getopenscreen/openscreen) by the OpenScreen contributors: screen recording and demo editing (MIT).
- [SuperDictate](https://github.com/shlgd/SuperDictate) by shlgd: local voice dictation for macOS (MIT).

## License

[MIT](LICENSE). Provided "as is", without warranty. Always review what the cleaner shows before deleting anything.
