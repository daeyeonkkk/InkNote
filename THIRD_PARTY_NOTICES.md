# Third-Party Notices

InkNote itself is licensed under the MIT License (see `LICENSE`).
The app build includes or links the following third-party components.

| Component | Used for | License |
|---|---|---|
| [PDFium](https://pdfium.googlesource.com/pdfium/) | Rendering and saving PDFs (`pdfium.dll`) | BSD-3-Clause / Apache-2.0 |
| [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) (`bblanchon.PDFium.Win32`) | Prebuilt PDFium binaries | MIT (build scripts), Apache-2.0 (package) |
| Libraries compiled into `pdfium.dll`: Abseil, AGG 2.3, dragonbox, fast_float, FreeType, HarfBuzz, ICU, Little CMS, libjpeg-turbo, libpng, LLVM libc, OpenJPEG, simdutf, zlib | Fonts, images, color management inside PDFs | See each file in `third_party/pdfium/licenses/` |
| [Windows App SDK / WinUI 3](https://github.com/microsoft/WindowsAppSDK) | UI framework (redistributed with the app) | Microsoft Software License Terms (redistributable files) |
| [Win2D](https://github.com/microsoft/Win2D) | GPU drawing of pages and ink | MIT |
| [.NET](https://github.com/dotnet/runtime) | Runtime (bundled in self-contained builds) | MIT |

The full license texts for PDFium and the libraries compiled into it are in
`third_party/pdfium/licenses/`, and are copied next to `InkNote.exe` in every build.
