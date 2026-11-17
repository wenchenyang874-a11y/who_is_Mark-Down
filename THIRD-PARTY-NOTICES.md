# Third-Party Notices

WIMD includes the following third-party software. Legal comments for Mermaid's
transitive dependencies remain embedded in the old .NET
`Resources/mermaid.min.js` and the Rust `ui/vendor/mermaid.min.js`.

## Microsoft local managed diagnostics (legacy .NET reference build)

- Microsoft.Diagnostics.Tracing.TraceEvent 4.0.0: <https://github.com/microsoft/perfview>
- Microsoft.Diagnostics.NETCore.Client 0.2.661903: <https://github.com/dotnet/diagnostics>
- Microsoft.Extensions.Logging.Abstractions 8.0.3 and Microsoft.Extensions.DependencyInjection.Abstractions 8.0.2: <https://github.com/dotnet/runtime>
- System.Reflection.TypeExtensions 4.7.0: <https://github.com/dotnet/runtime>
- Licenses: MIT (Microsoft). These components are used for bounded local method-stack sampling, not telemetry.
- The unused native ETW/PDB build helpers are excluded. No symbol download service is configured or used.

Copyright (c) Microsoft Corporation. All rights reserved.

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

## Mermaid 11.16.1

- Project: <https://github.com/mermaid-js/mermaid>
- Package: `mermaid@11.16.1`
- npm integrity: `sha512-TQsq6u22fAn3rek5VOubrhKPo1g5hwC3FXUN9hiyupTckcYiGuuKGkNQrKYwGJkXUxZdojwRG46gsSCFZMDp4g==`
- Embedded bundle SHA-256: `18327BEF70D96FB505FE7287D9F6A7362EBF07FF6576DDFAFFB1A06F3E1A2954`
- License: MIT

The MIT License (MIT)

Copyright (c) 2014 - 2022 Knut Sveidqvist

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

## Rust and Tauri dependencies

WIMD 2.0 includes the following direct redistributable Rust dependencies.
Exact transitive versions and checksums are locked in `rust/Cargo.lock`; each
crate remains under its own upstream license and copyright notices.
The complete 281-crate Windows dependency manifest is included as
`THIRD-PARTY-DEPENDENCIES.md` in the installed application.

The Windows build uses unmodified `MPL-2.0` transitive crates:
`cssparser`, `cssparser-macros`, `dtoa-short`, `option-ext`, and `selectors`.
Their exact versions and upstream source links are listed in that manifest;
the MPL-2.0 text is available at <https://www.mozilla.org/MPL/2.0/>.

| Package | Version | License | Upstream |
| --- | --- | --- | --- |
| ammonia | 4.2.5 | MIT OR Apache-2.0 | <https://github.com/rust-ammonia/ammonia> |
| base64 | 0.22.1 | MIT OR Apache-2.0 | <https://github.com/marshallpierce/rust-base64> |
| chrono | 0.4.45 | MIT OR Apache-2.0 | <https://github.com/chronotope/chrono> |
| clipboard-win | 5.4.3 | BSL-1.0 | <https://github.com/DoumanAsh/clipboard-win> |
| notify | 8.2.2 | CC0-1.0 | <https://github.com/notify-rs/notify> |
| open | 5.4.5 | MIT | <https://github.com/Byron/open-rs> |
| pulldown-cmark | 0.13.6 | MIT | <https://github.com/raphlinus/pulldown-cmark> |
| regex | 1.13.1 | MIT OR Apache-2.0 | <https://github.com/rust-lang/regex> |
| reqwest | 0.13.5 | MIT OR Apache-2.0 | <https://github.com/seanmonstar/reqwest> |
| rfd | 0.15.4 | MIT | <https://github.com/PolyMeilex/rfd> |
| serde / serde_json | 1.0.229 / 1.0.154 | MIT OR Apache-2.0 | <https://github.com/serde-rs/serde> |
| sha2 | 0.10.9 | MIT OR Apache-2.0 | <https://github.com/RustCrypto/hashes> |
| tauri / tauri-build | 2.12.5 / 2.7.4 | Apache-2.0 OR MIT | <https://github.com/tauri-apps/tauri> |
| thiserror | 2.0.21 | MIT OR Apache-2.0 | <https://github.com/dtolnay/thiserror> |
| uuid | 1.29.2 | Apache-2.0 OR MIT | <https://github.com/uuid-rs/uuid> |
| webview2-com | 0.39.1 | MIT | <https://github.com/wravery/webview2-rs> |
| windows / windows-sys | 0.62.2 / 0.61.2 | MIT OR Apache-2.0 | <https://github.com/microsoft/windows-rs> |

The permission text of the MIT License reproduced above applies to
MIT-licensed Rust dependencies. Their copyright notices are the respective
upstream package authors and contributors, not Knut Sveidqvist. The Apache
License 2.0 text is included as the repository `LICENSE`.

### Boost Software License 1.0

Copyright notices remain with the `clipboard-win` authors and contributors.

Permission is hereby granted, free of charge, to any person or organization
obtaining a copy of the software and accompanying documentation covered by
this license (the "Software") to use, reproduce, display, distribute,
execute, and transmit the Software, and to prepare derivative works of the
Software, and to permit third-parties to whom the Software is furnished to do
so, all subject to the following:

The copyright notices in the Software and this entire statement, including the
above license grant, this restriction and the following disclaimer, must be
included in all copies of the Software, in whole or in part, and all derivative
works of the Software, unless such copies or derivative works are solely in the
form of machine-executable object code generated by a source language
processor.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE, TITLE AND NON-INFRINGEMENT. IN NO EVENT
SHALL THE COPYRIGHT HOLDERS OR ANYONE DISTRIBUTING THE SOFTWARE BE LIABLE FOR
ANY DAMAGES OR OTHER LIABILITY, WHETHER IN CONTRACT, TORT OR OTHERWISE,
ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.

`notify` is dedicated to the public domain under CC0-1.0. The complete legal
code is available at <https://creativecommons.org/publicdomain/zero/1.0/legalcode>.

## Bundled fonts

WIMD distributes unmodified LXGW WenKai Regular and LXGW WenKai Mono Regular (v1.530), LXGW Marker Gothic Regular (v1.004), JetBrains Mono Regular (v2.304), and Fira Code Regular (6.2), each under the **SIL Open Font License 1.1** rather than WIMD's Apache-2.0 license. Copyright holders include LXGW and The Klee Project Authors, The LXGW Marker Gothic Project Authors, The JetBrains Mono Project Authors, and The Fira Code Project Authors. The Marker Gothic authors list is also included unchanged as AUTHORS.txt.

The complete original licenses accompany each font in the application
directories. Source copies are under `assets/fonts/<family>/` for the legacy
.NET build and `rust/crates/wimd-app/ui/fonts/<family>/` for the Rust build.
Each font directory includes fixed official sources, copyright statements,
versions, file sizes and SHA-256 hashes. Fonts are not modified, subsetted,
converted or renamed, are not sold on their own, and are not installed
system-wide. Existing Windows font files are never redistributed by WIMD.
