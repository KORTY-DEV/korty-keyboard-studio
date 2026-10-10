# Third-party notices

KORTY Keyboard Studio's AJAZZ AK820 Pro protocol layer (Feature04 family: command framing, lighting packet layout,
image header/payload layout, sequences and timings) is an independent C# implementation of the behaviour observed in
the source of the working reference web configurator:

- **kabelklaus/ajazz-ak820-pro** — https://github.com/kabelklaus/ajazz-ak820-pro — MIT License, Copyright (c) 2026 kabelklaus.
  Reference file: `app.js`, SHA-256 `52e8215cb4e6cd97b9a1cea9d9d2fbe75902f4234970e54a3230741da1ed0844`.
  License text: `THIRD_PARTY_LICENSE_kabelklaus.txt`.
  `tools/make-golden-vectors.mjs` transcribes a handful of pure byte-building functions from `app.js` to generate the
  regression vectors in `tests/Korty.Tests/Golden/kabelklaus-golden.json` (MIT-licensed material, attributed here).

Information (not code) was also taken from the public documentation of wsclx/ak820pro-modder (MIT). The GPL-2.0 project
fpb/ajazz-ak820-pro was consulted for hardware facts only; none of its code is used.
