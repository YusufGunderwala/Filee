<p align="center">
  <img src="docs/media/banner-en.jpg" alt="Filee: Drag. Drop. Converted." width="100%">
</p>

<p align="center">
  <b>The fastest, privacy-first offline file converter you trigger with a single gesture.</b><br>
  Hold <kbd>Ctrl</kbd> (<kbd>Option ⌥</kbd> on macOS), drag any file, and drop it on the format donut that opens at your cursor.
</p>

<p align="center">
  <a href="https://github.com/YusufGunderwala/Filee/releases/latest"><b>⚡ Download Latest Release</b></a> •
  <a href="#-whats-new-in-v210">What's New in v2.1.0</a> •
  <a href="#-key-features">Features</a> •
  <a href="#-download--installation">Install</a> •
  <a href="#-command-line-filee-cli">CLI</a> •
  <a href="#-supported-formats-255">Formats</a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-2.1.0-blue?style=for-the-badge" alt="Version 2.1.0">
  <img src="https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-brightgreen?style=for-the-badge" alt="Platforms">
  <img src="https://img.shields.io/badge/privacy-100%25%20Offline%20%26%20Local-red?style=for-the-badge" alt="Privacy First">
  <img src="https://img.shields.io/badge/license-MIT-purple?style=for-the-badge" alt="MIT License">
</p>

---

## 🚀 See It in Action

<p align="center">
  <img src="docs/media/demo-en.webp" alt="Filee in action: Drag files with Ctrl held, drop on format, converted instantly" width="820">
</p>

---

## 🌟 What's New in v2.1.0

### 1. 📄 High-Fidelity Word ⇄ PDF Converter (Built-in, Zero Cloud)
- **High-accuracy DOCX → PDF**: Native vector SkiaSharp engine rendering inline and floating images, custom font metrics, exact line-spacing (`HLineSpacing`), superscript/subscript, text highlights, hard breaks, tab stops, and justified alignment.
- **Header & Footer support**: Preserves multi-section running headers, footers, and page numbers.
- **Shapes & Tables**: Full vector support for bordered tables, background cell colors, shapes (rectangles, ellipses, dividers), and floating text boxes.
- **Roundtrip PDF → Word**: Fast and structured conversion back to DOCX.
- **Legacy DOC reader**: Converts `.doc` binary OLE/RTF files to modern DOCX and PDF offline.

### 2. 📋 Drop-to-Clipboard (Instant OCR & QR Code Extractor)
- Drag any image, screenshot, or receipt directly onto **"Copy Text"** to extract text via native Windows OCR without saving a file to disk.
- Drag any barcode or QR code image to **"Read QR"** to extract the payload URL or text straight to your clipboard in under 5 ms.
- Interactive Toast Notifications with instant `[Paste]` and `[Save .txt]` actions.

### 3. 🎯 Target File Size Squeezer (Fit Under Limit)
- Perfect for **Discord (10 MB / 25 MB)**, **Gmail (25 MB)**, and government/tax portals (**2 MB PDF / 500 KB JPG**).
- Computes optimal 2-pass video bitrate for FFmpeg deterministically: `Bitrate = Target Size / Duration`.
- Binary-searches image quality levels (JPEG, WebP, AVIF) to guarantee output under your exact byte budget.

---

## ✨ Key Features

<table>
  <tr>
    <td width="33%" valign="top">
      <h4>⚡ Right at your cursor</h4>
      No cumbersome window to open. Press <kbd>Ctrl</kbd> (<kbd>Option ⌥</kbd> on macOS) while dragging any file, and a smart radial donut opens containing only the formats that make sense.
    </td>
    <td width="33%" valign="top">
      <h4>🛡️ 100% Offline & Private</h4>
      Zero cloud uploads, zero telemetry, zero servers. Your sensitive contracts, photos, and spreadsheets are converted locally on your device and saved right beside the original.
    </td>
    <td width="33%" valign="top">
      <h4>📦 255+ File Formats</h4>
      Documents, images, video, audio, e-books, archives, 3D models, CAD, and geospatial files without needing Microsoft Office, Adobe Acrobat, or LibreOffice.
    </td>
  </tr>
</table>

<p align="center">
  <img src="docs/media/page-toolbar-dark.png" alt="Filee Donut UI" width="600">
</p>

---

## 📥 Download & Installation

### Windows 10 / 11
1. Download **`Filee-2.1.0-win-x64-portable.zip`** from [Releases](https://github.com/YusufGunderwala/Filee/releases).
2. Unzip and run **`Filee.exe`**.
3. *(Optional)* Add to PATH or create desktop shortcut for single-drag conversions anytime.

### macOS (Apple Silicon M1/M2/M3/M4 & Intel x64)
1. Download **`Filee-2.1.0-osx-arm64.zip`** (for Apple Silicon) or **`Filee-2.1.0-osx-x64.zip`** (for Intel).
2. Extract the archive and open the app:
   ```bash
   unzip Filee-2.1.0-osx-arm64.zip -d ~/Applications/Filee
   chmod +x ~/Applications/Filee/Filee
   ~/Applications/Filee/Filee
   ```
3. *Note for macOS*: On first run, allow the app in **System Settings → Privacy & Security → Open Anyway**.

---

## 💻 Command Line (Filee CLI)

Filee includes a headless, blazing fast CLI tool for terminal power users and batch scripts:

```bash
# Convert DOCX to high-fidelity PDF
filee convert document.docx --to pdf

# Convert PDF back to editable Word
filee convert invoice.pdf --to docx

# Convert images with target size constraint (under 2 MB)
filee convert photo.raw --to jpg --target-size 2MB

# Batch convert video for Discord limit
filee convert clip.mov --to mp4 --target-size 25MB

# Inspect available conversion paths for any extension
filee formats docx
```

---

## 🎨 Presets & Custom Automations

<p align="center">
  <img src="docs/media/page-presets-en.png" alt="Preset Editor" width="700">
</p>

Create custom presets tailored for your workflow:
- **Web Optimized**: WebP 85% quality + strip metadata
- **Discord Video**: 720p H.264 capped at 25 MB
- **Print PDF**: SkiaSharp 300 DPI vector PDF

---

## 🛠️ Building from Source

```bash
# Clone the repository
git clone https://github.com/YusufGunderwala/Filee.git
cd Filee

# Build the desktop app
dotnet build src/Filee.App/Filee.App.csproj -c Release

# Run Filee
dotnet run --project src/Filee.App/Filee.App.csproj
```

---

## 📜 License

Filee is proudly open-source under the [MIT License](LICENSE).
