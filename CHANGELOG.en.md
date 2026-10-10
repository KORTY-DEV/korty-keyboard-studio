# Changelog

## 1.2.0
- **Key response time in 5 levels** with the vendor's descriptions and timings (wired / 2.4G / Bluetooth), plus sleep time and the firmware-side disables for Win key, Alt+F4 and Alt+Tab. All sent as one settings block (layout recovered from the vendor software by static analysis; not yet hardware-tested).
- Removed the PC-side key-blocking hook (the keyboard does it itself) and the clock-sync button.
- Added a ready **24-frame example animation** and a button to load it on the Screen page.
- New activation stage "Settings"; dashboard updated.
- Note: kabelklaus' "standby" byte is actually the key response level.

## 1.1.0
- Keyboard control is enabled with a single confirmation (no typed phrase); consent is remembered and can be revoked on the Safety page.
- New **Keyboard** page: clock sync, sleep time, optional PC-side Win / Alt+F4 / Alt+Tab blocking; honest notes about key response time (not yet available) and reset (intentionally not offered).
- 13 one-click lighting presets; animated background generator for the screen (Aurora, Plasma, Matrix, Neon rings).
- New application icon, shortcut script, self-contained single-file release (`Build-Release.ps1`), MIT license, English/Russian README.
- Overview and Safety pages simplified; device wording no longer says "candidate".

## 1.0.0
- Clock stage, GIF/PNG as full-screen background layers, time snapshot layer, honest limits (no overlay, no Fn+Del equivalent, 2.4 GHz input-only).

## 0.4
- GIF transfer fix (long write timeout, one retry on a fresh handle, exact Win32 codes), colour picker (HSV/HEX), local effect preview.

## 0.3
- Protocol implemented from the working reference (kabelklaus), byte-exact golden tests, Windows HID transport with handshake and ACK.
