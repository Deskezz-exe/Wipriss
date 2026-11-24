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

## Modules

| Module | What it does | Status |
|---|---|---|
| Notes | Quick notes that survive crashes and reboots. Encrypted on disk. | ✅ ready |
| Clipboard history | Last 20 copies, filter by text or images, skips password-manager content. Encrypted on disk. | ✅ ready |
| Reminders | Banner (center, corner or none) at the exact time, with or without sound. One system timer, no polling. | ✅ ready |
| Network | Who used how much internet today, and who looks suspicious: a program that normally stays offline moving hundreds of MB, an unsigned file running from Temp, a fake `svchost.exe`, a connection to a crypto-mining port. Every flag comes with plain reasons. No admin rights, nothing sent anywhere. | ✅ ready |
| Ports | Which program holds which port (for example `node vite`), and a stop button for the whole process tree, including the `npm` that would restart it. Windows, services, Docker and WSL are marked and never stopped. | ✅ ready |
| Screenshots | PrtSc freezes the screen: drag an area or click a window, take the whole screen, or scroll: the page scrolls by itself and is glued into one tall picture. The shot goes straight to the clipboard and never into clipboard history; a small card offers an editor (pen, marker, arrow, frame, blur for private details, crop, size). Nothing is written to disk unless you press Save. | ✅ ready |
| Image converter | Format, resolution, target file size, background removal. | planned |
| PDF / XLSX / CSV reader | View and edit these files in a light window, optionally on double-click. | planned |
| Voice typing | Press a key, speak, the text appears at the cursor. Runs fully offline. | planned |
| Smart cleaner | Finds leftovers of uninstalled programs by checking what is actually installed, explains each item, deletes to a quarantine you can restore from. | planned |

Tip: `Wipriss.exe --open notes` (or `clipboard`, `reminders`, `network`, `ports`, `capture`) opens a module directly. Put it in a shortcut with a hotkey for one-key access.

## One folder

Everything Wipriss keeps is in one place, `%USERPROFILE%\Wipriss` by default (you can move it in settings):

- `Скриншоты` (screenshots): only the ones you saved;
- `Служебное` (internal): notes, clipboard history, reminders and settings, encrypted with Windows DPAPI.

## Thanks

Wipriss stands on the shoulders of these open-source projects. Their ideas shaped it. Any code taken from them keeps its original copyright and license and is listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

- [nabyscape](https://github.com/BillyNabil/nabyscape) by BillyNabil: a dynamic-island dock for Windows (MIT).
- [Filee](https://github.com/KnifeLemon/Filee) by Myeonggu Kim (KnifeLemon): a drag-and-drop file converter (MIT).
- [OpenScreen](https://github.com/getopenscreen/openscreen) by the OpenScreen contributors: screen recording and demo editing (MIT).
- [SuperDictate](https://github.com/shlgd/SuperDictate) by shlgd: local voice dictation for macOS (MIT).

## License

[MIT](LICENSE). Provided "as is", without warranty. Always review what the cleaner shows before deleting anything.
