# Third-Party Notices

WIMD includes the following third-party software. Legal comments for Mermaid's
transitive dependencies remain embedded in `Resources/mermaid.min.js`.

## Microsoft local managed diagnostics

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
## Bundled fonts

WIMD distributes unmodified LXGW WenKai Regular and LXGW WenKai Mono Regular (v1.530), LXGW Marker Gothic Regular (v1.004), JetBrains Mono Regular (v2.304), and Fira Code Regular (6.2), each under the **SIL Open Font License 1.1** rather than WIMD's Apache-2.0 license. Copyright holders include LXGW and The Klee Project Authors, The LXGW Marker Gothic Project Authors, The JetBrains Mono Project Authors, and The Fira Code Project Authors. The Marker Gothic authors list is also included unchanged as AUTHORS.txt.

The complete original licenses accompany each font in `Fonts/<family>/OFL.txt` in the application directory (`assets/fonts/<family>/OFL.txt` in source). `Fonts/README.md` includes fixed official sources, copyright statements, versions, file sizes and SHA-256 hashes. See **Settings → Appearance → Fonts → licenses and sources** to read those local notices. Fonts are not modified, subsetted, converted or renamed, are not sold on their own, and are not installed system-wide. Existing Windows font files are never redistributed by WIMD.
