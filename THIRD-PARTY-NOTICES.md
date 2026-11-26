# Third-party notices

Wipriss itself uses only .NET and Windows. The one exception is the picture converter below.

Each third-party component is listed here with its name, author, license and link. Where a license requires it, the full license text ships with the app.

## Shipped with the app

### Picture converter (`Wipriss.Converter.exe`)

| Package | Author | License | Link |
|---|---|---|---|
| Magick.NET-Q8-x64, Magick.NET.Core 15.1.0 | Dirk Lemstra | Apache-2.0 | https://github.com/dlemstra/Magick.NET |
| ImageMagick 7 (inside Magick.Native) | ImageMagick Studio LLC | ImageMagick License (Apache-2.0 style) | https://imagemagick.org/script/license.php |

`Magick.Native-Q8-x64.dll` also contains the open-source libraries ImageMagick reads and writes pictures with, among them libjpeg-turbo, libpng, zlib, libwebp, libtiff, libheif, libde265, aom, lcms2, openjpeg, libjxl, freetype and others. Most are under MIT, BSD, zlib or Apache-style licenses; some (libheif, libde265, libraw, librsvg, glib, pango and a few more) are under the GNU LGPL. They live in that separate DLL, which Wipriss loads dynamically and does not change, so it can be replaced with another build. The full list with every license text is in `licenses\Magick.NET-notice.txt` next to `Wipriss.exe`, copied unchanged from the Magick.NET package.

Wipriss only converts the picture formats it shows (JPG, PNG, WEBP, AVIF, BMP, ICO, TIFF; HEIC and GIF are read only). Everything else in ImageMagick is switched off by a security policy.

## Development only (not shipped with the app)

Used to run the automated tests in `tests/`:

| Package | Author | License | Link |
|---|---|---|---|
| xunit, xunit.runner.visualstudio | .NET Foundation and Contributors | Apache-2.0 | https://github.com/xunit/xunit |
| Microsoft.NET.Test.Sdk | Microsoft | MIT | https://github.com/microsoft/vstest |

Projects that inspired Wipriss are credited in the [README](README.md#thanks).
