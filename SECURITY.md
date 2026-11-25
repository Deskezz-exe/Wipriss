# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Report privately through GitHub: the **Security** tab → **Report a vulnerability**. I aim to reply within 7 days and will credit you in the fix notes if you wish.

## Supported versions

Only the latest release gets security fixes.

## What Wipriss promises

- **No telemetry, no analytics, no accounts.** The app goes online only to download a model or engine when you ask, and to check for updates (can be turned off).
- **Local data stays local and encrypted.** Clipboard history, notes and reminders live in the Wipriss folder (`%USERPROFILE%\Wipriss\Служебное` by default), encrypted with Windows DPAPI for your user account only. Content that password managers mark as private is never stored.
- **Voice never leaves your PC.** The microphone is on only while recording, and the island shows it.
- **Least privilege.** The app runs without admin rights. Admin actions go through a one-shot helper that you approve via UAC and that accepts only fixed, validated commands.
- **The keyboard is watched for one key only.** To take PrtSc from Snipping Tool at once, the screenshot module uses a low-level keyboard hook. It reads only the key code, keeps PrtSc and passes every other key on untouched; nothing is recorded or sent. Turning the switch off removes the hook.
- **Screenshots stay in memory.** A screenshot goes to the clipboard marked so that Windows clipboard history (and its cloud sync) and Wipriss' own history skip it. It is written to disk only when you press Save in the editor. No temporary files.
- **Network check stays local.** Traffic numbers come from Windows' own accounting; file signatures are checked offline. The only thing that ever leaves the PC is a file's SHA-256 fingerprint, and only when you click "check on VirusTotal".
- **Careful with processes.** Only programs on your own desktop can be stopped, always after a confirmation. Core Windows processes, services, Docker, WSL and Wipriss itself are off limits. Every target is matched by its process ID and start time, so a reused ID never hits another program.
- **Safe cleaning.** Nothing is deleted without being shown first. Deletion goes to a restorable quarantine, symbolic links are never followed, and core Windows folders cannot be selected.
- **Untrusted files are sandboxed.** Documents open in a separate process without extra rights. PDF JavaScript and spreadsheet macros never run.
- **Verified downloads.** Models and engines come over HTTPS from pinned URLs and are checked against SHA-256 before use.
- **Verifiable releases.** Releases are built from source by GitHub Actions and ship with SHA-256 checksums and build attestations.
