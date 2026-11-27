# Third-party notices

Wipriss itself uses only .NET and Windows. The exceptions are the picture converter, the table viewer, voice typing and the code ported from other projects below.

Each third-party component is listed here with its name, author, license and link. Where a license requires it, the full license text ships with the app.

## Shipped with the app

### Picture converter (`Wipriss.Converter.exe`)

| Package | Author | License | Link |
|---|---|---|---|
| Magick.NET-Q8-x64, Magick.NET.Core 15.1.0 | Dirk Lemstra | Apache-2.0 | https://github.com/dlemstra/Magick.NET |
| ImageMagick 7 (inside Magick.Native) | ImageMagick Studio LLC | ImageMagick License (Apache-2.0 style) | https://imagemagick.org/script/license.php |

`Magick.Native-Q8-x64.dll` also contains the open-source libraries ImageMagick reads and writes pictures with, among them libjpeg-turbo, libpng, zlib, libwebp, libtiff, libheif, libde265, aom, lcms2, openjpeg, libjxl, freetype and others. Most are under MIT, BSD, zlib or Apache-style licenses; some (libheif, libde265, libraw, librsvg, glib, pango and a few more) are under the GNU LGPL. They live in that separate DLL, which Wipriss loads dynamically and does not change, so it can be replaced with another build. The full list with every license text is in `licenses\Magick.NET-notice.txt` next to `Wipriss.exe`, copied unchanged from the Magick.NET package.

Wipriss only converts the picture formats it shows (JPG, PNG, WEBP, AVIF, BMP, ICO, TIFF; HEIC and GIF are read only). Everything else in ImageMagick is switched off by a security policy.

### Table viewer (`Wipriss.Reader.exe`)

| Package | Author | License | Link |
|---|---|---|---|
| ExcelDataReader 3.9.0 | ExcelDataReader contributors | MIT | https://github.com/ExcelDataReader/ExcelDataReader |
| ExcelNumberFormat 1.2.0 | andersnm | MIT | https://github.com/andersnm/ExcelNumberFormat |

ExcelDataReader reads XLSX, XLSM, XLSB and XLS files; ExcelNumberFormat shows numbers and dates in a cell's own number format. Both are used unchanged as NuGet packages. Their license (MIT), with the copyright lines "Copyright (c) 2014-2026 ExcelDataReader contributors" and "Copyright (c) 2017 andersnm":

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

OpenDocument (ODS) and CSV files are read by Wipriss's own code. The viewer only shows the values a file has saved: formulas are not recalculated, macros and external links never run.

### Voice typing (`Wipriss.Voice.exe`)

| Package | Author | License | Link |
|---|---|---|---|
| org.k2fsa.sherpa.onnx 2.0.3 (with its win-x64 runtime) | k2-fsa (Next-gen Kaldi) | Apache-2.0 | https://github.com/k2-fsa/sherpa-onnx |
| ONNX Runtime (inside that runtime, `onnxruntime.dll`) | Microsoft Corporation | MIT | https://github.com/microsoft/onnxruntime |

sherpa-onnx recognizes speech offline, it is used unchanged as a NuGet package. The full Apache-2.0 text: https://www.apache.org/licenses/LICENSE-2.0. ONNX Runtime: "Copyright (c) Microsoft Corporation", MIT, the same text as above.

The speech model is **not** shipped with the app and not stored in this repository. It is downloaded only when the owner presses the button, from links pinned to one revision, and every file is checked against its SHA-256 before use:

| Model | Author | License | Link |
|---|---|---|---|
| GigaAM v3 (RNN-T, with punctuation), ONNX export by the sherpa-onnx authors | GigaChat Team (Salute Developers) | MIT | https://github.com/salute-developers/GigaAM |
| Moonshine Base (English), int8 ONNX export by the sherpa-onnx authors | Useful Sensors | MIT | https://github.com/usefulsensors/moonshine |
| Whisper Small (multilingual), int8 ONNX export by the sherpa-onnx authors | OpenAI | MIT | https://github.com/openai/whisper |

The models are downloaded only when the user picks one; where the model repository has a license file, it is downloaded with it and lies next to it.

The microphone is recorded with Windows' own waveIn API, without a library.

### Code ported from other projects

**Filee** by Myeonggu Kim (KnifeLemon), MIT — https://github.com/KnifeLemon/Filee
`src/Wipriss.Converter/BackgroundRemover.cs` ("Убрать однотонный фон") is Filee's `BackgroundRemover.cs`, only the namespace changed. Its header names the source and the commit.
The Windows 11 Explorer menu is based on Filee's Explorer-menu extension (commit a89b139): `src/Wipriss.ExplorerMenu/WiprissExplorerMenu.c`, `.def` and `.rc` (from `FileeExplorerMenu.*`: own CLSID, title, app names and title-file folder), `AppxManifest.xml` (from `ExplorerMenuPackage.CreateManifest`: a static file, picture types only), `MenuPackage.cs` and the package lookup in `src/Modules/Wipriss.Modules.Converter/ExplorerMenu.cs` (from `ExplorerMenuRegistration.cs`), `tools/make-explorer-package.cs` and `tools/build-explorer-menu.ps1` (from `build/`). Each file's header lists the changes.

**Zig** 0.16.0 (build tool only, not shipped), MIT — https://ziglang.org. `tools/build-explorer-menu.ps1` downloads it (SHA-256 checked) to compile the Explorer-menu DLL; the DLL links no Zig or C runtime code.

```
MIT License

Copyright (c) 2026 Myeonggu Kim (KnifeLemon) and Filee contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Development only (not shipped with the app)

Used to run the automated tests in `tests/`:

| Package | Author | License | Link |
|---|---|---|---|
| xunit, xunit.runner.visualstudio | .NET Foundation and Contributors | Apache-2.0 | https://github.com/xunit/xunit |
| Microsoft.NET.Test.Sdk | Microsoft | MIT | https://github.com/microsoft/vstest |

Projects that inspired Wipriss are credited in the [README](README.md#thanks).
