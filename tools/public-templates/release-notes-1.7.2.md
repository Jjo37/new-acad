## new-acad v1.7.2 — 修复图片描摹（JPEG/GIF/TIFF 失败）+ 新增 WEBP 支持（2026-10-04）

### 本次修复的问题

**1）JPEG / GIF / TIFF 图片描摹一直失败（本次核心修复）**

把 JPEG 等图片拖进面板让它"画进 CAD / 描成线条"时，会收到类似「程序在读取它的时候数据被截断」的错误，描摹跑不起来。原因是插件有一个"大结果自动降级"机制（超过 64KB 的返回会被自动裁剪以保护上下文），而描摹 JPEG/GIF/TIFF 时需要先把图片经插件转成灰度数据（体积很大），这份二进制数据被误当成普通文本截断 → 解码失败。

- 受影响的格式：**JPEG / GIF / TIFF**（PNG、BMP 走本地解码，一直正常）
- 其实图片本身没问题，是数据传输环节被截断
- 现已修复：**JPEG / GIF / TIFF 描摹恢复正常**

**2）新增 WEBP 格式描摹**

以前 WEBP 图片只能"看"、不能描摹（解码库不认 WEBP）。现在插件增加了 WIC 解码回退，**描摹支持：PNG / BMP / JPEG / GIF / TIFF / WEBP**。

**3）去掉误导提示**

此前描摹出错会提示"JPEG 请先转 PNG"，这个提示是错的（JPEG 本来支持）。已移除，避免再被带偏。

### 下载哪个

- `new-acad-setup-v1.7.2.exe` — 一键安装器（推荐）
- `new-acad-v1.7.2.zip` — 绿色版（内含安装器）
- `new-acad.bundle.zip` — Autodesk Autoloader 包

> 已装 v1.7.1 的用户：只需替换插件 DLL（或重跑安装器）即可，无需重装其他内容。

### 支持范围（同 v1.7.1）

- 支持 **Civil 3D 2025 / 2026**（.NET 8）
- **不支持 Civil 3D 2024**（.NET Framework 4.8，插件无法加载）

---

## new-acad v1.7.2 — Fix image tracing (JPEG/GIF/TIFF failed) + add WEBP support (2026-10-04)

### Issues fixed in this release

**1) JPEG / GIF / TIFF image tracing always failed (the main fix)**

When you dropped a JPEG (etc.) into the palette and asked it to "draw into CAD / trace as lines", you got an error like "the data was truncated while reading it" and the trace never ran. The plugin has a "large-result auto-degrade" guard (anything over 64 KB gets trimmed to protect the model context). Tracing JPEG/GIF/TIFF first converts the image to raw gray data through the plugin (a big binary blob), and that blob was mistakenly truncated like ordinary text → decode failed.

- Affected formats: **JPEG / GIF / TIFF** (PNG and BMP decode locally and were always fine)
- The image itself was never the problem — it was the transfer being cut off
- Fixed: **JPEG / GIF / TIFF tracing works again**

**2) WEBP tracing added**

Previously WEBP images could be *viewed* but not *traced* (the decoder did not understand WEBP). The plugin now falls back to WIC decoding, so tracing supports: **PNG / BMP / JPEG / GIF / TIFF / WEBP**.

**3) Misleading hint removed**

On failure the tool used to suggest "convert JPEG to PNG first" — that was wrong (JPEG was always supported). Removed.

### Downloads

- `new-acad-setup-v1.7.2.exe` — installer (recommended)
- `new-acad-v1.7.2.zip` — portable (installer included)
- `new-acad.bundle.zip` — Autodesk Autoloader bundle

> Already on v1.7.1? Replacing the plugin DLL (or re-running the installer) is enough.

### Supported versions (same as v1.7.1)

- **Civil 3D 2025 / 2026** (.NET 8)
- **Civil 3D 2024 is NOT supported** (.NET Framework 4.8 — the plugin cannot load)
