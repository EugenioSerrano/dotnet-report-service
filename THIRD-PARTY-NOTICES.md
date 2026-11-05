# Third-party notices

The product ships the third-party components below. Each entry names the
component, the exact version this repository pins, the licence its own package
metadata declares, its copyright line and where the licence text it requires is
reproduced — in [the licence texts](#licence-texts) at the end of this file, or
at the canonical address of the licence the package declares by its SPDX
expression. A component's entry is added by the change that first depends on it.

The components of the two worker processes (the orchestration worker and the
render worker) are the whole set a shipped image carries; every other product
process runs from the shared framework and the packages listed here.

## Microsoft.Playwright 1.63.0

- Licence: MIT — declared by the package's own metadata
  (`<license type="expression">MIT</license>`) and published at
  https://licenses.nuget.org/MIT.
- Copyright: `© Microsoft Corporation. All rights reserved.` — from the
  package's own `.nuspec`.
- Project: https://github.com/microsoft/playwright-dotnet.
- The licence text is reproduced in [MIT](#mit) below. The browser the package
  drives is installed into the render worker's base image as the package
  installs it (Chromium); its own notices travel with it.

## Temporalio 1.21.0

- Licence: MIT — declared by the package's own metadata and published at
  https://licenses.nuget.org/MIT.
- Copyright: the package records no separate copyright line; its author is
  `Temporal`.
- Project: https://github.com/temporalio/sdk-dotnet.
- The licence text is reproduced in [MIT](#mit) below.

## NexusRpc 0.4.0

- Licence: MIT — declared by the package's own metadata and published at
  https://licenses.nuget.org/MIT.
- Copyright: the package records no separate copyright line; its author is
  `Temporal`.
- Project: https://github.com/temporalio/nexus-dotnet (distributed with the
  workflow-engine client).
- The licence text is reproduced in [MIT](#mit) below.

## Dapper 2.1.89

- Licence: Apache-2.0 — declared by the package's own metadata and published at
  https://licenses.nuget.org/Apache-2.0.
- Copyright: `2019 Stack Exchange, Inc.` — from the package's own `.nuspec`.
- Project: https://dapperlib.dev/.
- The licence's notice form is reproduced in [Apache-2.0](#apache-license-20)
  below.

## Npgsql 10.0.3

- Licence: PostgreSQL — declared by the package's own metadata and published at
  https://licenses.nuget.org/PostgreSQL.
- Copyright: `Copyright 2025 © The Npgsql Development Team` — from the
  package's own `.nuspec`.
- Project: https://github.com/npgsql/npgsql.
- The licence text is reproduced in [The PostgreSQL Licence](#the-postgresql-licence)
  below.

## PdfPig 0.1.16

- Licence: Apache-2.0 — declared by the package's own metadata and published at
  https://licenses.nuget.org/Apache-2.0.
- Copyright: the package records no separate copyright line; its author is
  `UglyToad`.
- Project: https://github.com/UglyToad/PdfPig.
- The licence's notice form is reproduced in [Apache-2.0](#apache-license-20)
  below.

PdfPig bundles material of its own inside `UglyToad.PdfPig.dll`, and the
product ships it as the package bundles it:

- **The Adobe Core 14 fonts and their AFM metrics.** Adobe's terms for the AFM
  files accompany the package as Adobe publishes them; the reference is Adobe's
  *MustRead.html*, distributed with the AFM files, which states the terms under
  which the metrics may be used and redistributed.
- **The Adobe Glyph List.** BSD-3-Clause; its text is reproduced in
  [BSD-3-Clause](#bsd-3-clause) below.
- **The Adobe CMaps.** BSD-3-Clause; its text is reproduced in
  [BSD-3-Clause](#bsd-3-clause) below.
- **The colour profile `sRGB2014.icc`.** The package states no licence terms
  for it; the profile carries only its copyright tag, *"Copyright International
  Color Consortium, 2015"*, reproduced here. The product ships the profile as
  PdfPig 0.1.16 bundles it. The product owner accepts in writing that the
  profile's terms are not stated in the package and have not been confirmed, and
  that the question is revisited if its terms turn out to be unacceptable.

## Scriban 7.5.1

- Licence: BSD-2-Clause — declared by the package's own metadata and published
  at https://licenses.nuget.org/BSD-2-Clause.
- Copyright: `Alexandre Mutel` — from the package's own `.nuspec`.
- Project: https://scriban.github.io/.
- The licence text is reproduced in [BSD-2-Clause](#bsd-2-clause) below.

## YamlDotNet 18.1.0

- Licence: MIT — declared by the package's own metadata and published at
  https://licenses.nuget.org/MIT.
- Copyright: `Copyright (c) Antoine Aubry and contributors` — from the
  package's own `.nuspec`.
- Project: https://github.com/aaubry/YamlDotNet.
- The licence text is reproduced in [MIT](#mit) below.

## Corvus.Text.Json 5.6.1

- Licence: Apache-2.0 — declared by the package's own metadata and published at
  https://licenses.nuget.org/Apache-2.0.
- Copyright: `Copyright (c) Endjin Limited 2023. All rights reserved.` — from
  the package's own `.nuspec`.
- Project: https://github.com/corvus-dotnet/Corvus.Text.Json.
- The licence's notice form is reproduced in [Apache-2.0](#apache-license-20)
  below.

## NodaTime 3.3.1

- Licence: Apache-2.0 — declared by the package's own metadata and published at
  https://licenses.nuget.org/Apache-2.0.
- Copyright: the package records no separate copyright line; its author is
  `Jon Skeet`.
- Project: https://nodatime.org/.
- The licence's notice form is reproduced in [Apache-2.0](#apache-license-20)
  below.

## Google.Protobuf 3.26.1

- Licence: BSD-3-Clause — declared by the package's own metadata and published
  at https://licenses.nuget.org/BSD-3-Clause.
- Copyright: `Copyright 2015, Google Inc.` — from the package's own `.nuspec`.
- Project: https://github.com/protocolbuffers/protobuf.
- The licence text is reproduced in [BSD-3-Clause](#bsd-3-clause) below.

## Microsoft.Bcl.AsyncInterfaces 6.0.0

- Licence: MIT — declared by the package's own metadata and published at
  https://licenses.nuget.org/MIT.
- Copyright: `© Microsoft Corporation. All rights reserved.` — from the
  package's own `.nuspec`.
- Project: https://dot.net/.
- The licence text is reproduced in [MIT](#mit) below.

## System.ComponentModel.Annotations 5.0.0

- Licence: MIT — declared by the package's own metadata and published at
  https://licenses.nuget.org/MIT.
- Copyright: `© Microsoft Corporation. All rights reserved.` — from the
  package's own `.nuspec`.
- Project: https://github.com/dotnet/runtime.
- The licence text is reproduced in [MIT](#mit) below.

## OpenTelemetry 1.20.1

- Licence: Apache-2.0 — declared by the package's own metadata and published at
  https://licenses.nuget.org/Apache-2.0.
- Copyright: `Copyright The OpenTelemetry Authors` — from the package's own
  `.nuspec`.
- Project: https://opentelemetry.io/.
- The licence's notice form is reproduced in [Apache-2.0](#apache-license-20)
  below.

## OpenTelemetry.Api 1.20.1

- Licence: Apache-2.0 — declared by the package's own metadata and published at
  https://licenses.nuget.org/Apache-2.0.
- Copyright: `Copyright The OpenTelemetry Authors`.
- Project: https://opentelemetry.io/.
- The licence's notice form is reproduced in [Apache-2.0](#apache-license-20)
  below.

## OpenTelemetry.Api.ProviderBuilderExtensions 1.20.1

- Licence: Apache-2.0 — declared by the package's own metadata and published at
  https://licenses.nuget.org/Apache-2.0.
- Copyright: `Copyright The OpenTelemetry Authors`.
- Project: https://opentelemetry.io/.
- The licence's notice form is reproduced in [Apache-2.0](#apache-license-20)
  below.

## OpenTelemetry.Exporter.OpenTelemetryProtocol 1.20.1

- Licence: Apache-2.0 — declared by the package's own metadata and published at
  https://licenses.nuget.org/Apache-2.0.
- Copyright: `Copyright The OpenTelemetry Authors`.
- Project: https://opentelemetry.io/.
- The licence's notice form is reproduced in [Apache-2.0](#apache-license-20)
  below.

## OpenTelemetry.Extensions.Hosting 1.20.1

- Licence: Apache-2.0 — declared by the package's own metadata and published at
  https://licenses.nuget.org/Apache-2.0.
- Copyright: `Copyright The OpenTelemetry Authors`.
- Project: https://opentelemetry.io/.
- The licence's notice form is reproduced in [Apache-2.0](#apache-license-20)
  below.

## OpenTelemetry.Instrumentation.AspNetCore 1.20.1

- Licence: Apache-2.0 — declared by the package's own metadata and published at
  https://licenses.nuget.org/Apache-2.0.
- Copyright: `Copyright The OpenTelemetry Authors`.
- Project: https://opentelemetry.io/.
- The licence's notice form is reproduced in [Apache-2.0](#apache-license-20)
  below.

## OpenTelemetry.Instrumentation.Http 1.20.1

- Licence: Apache-2.0 — declared by the package's own metadata and published at
  https://licenses.nuget.org/Apache-2.0.
- Copyright: `Copyright The OpenTelemetry Authors`.
- Project: https://opentelemetry.io/.
- The licence's notice form is reproduced in [Apache-2.0](#apache-license-20)
  below.

## OpenTelemetry.Instrumentation.Runtime 1.20.1

- Licence: Apache-2.0 — declared by the package's own metadata and published at
  https://licenses.nuget.org/Apache-2.0.
- Copyright: `Copyright The OpenTelemetry Authors`.
- Project: https://opentelemetry.io/.
- The licence's notice form is reproduced in [Apache-2.0](#apache-license-20)
  below.

---

## Licence texts

### MIT

```text
MIT License

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

The copyright line of each MIT-licensed component is the one its own entry
above records.

### BSD-2-Clause

```text
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### BSD-3-Clause

```text
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its contributors
   may be used to endorse or promote products derived from this software
   without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### Apache License 2.0

Every Apache-2.0 component above declares the licence by its SPDX expression
and publishes the full text at the address its entry names
(https://licenses.nuget.org/Apache-2.0). The notice the licence requires is:

```text
Copyright (c) the component's copyright holder, as its entry above records.

Licensed under the Apache License, Version 2.0 (the "License"); you may not use
this file except in compliance with the License. You may obtain a copy of the
License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software distributed
under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
CONDITIONS OF ANY KIND, either express or implied. See the License for the
specific language governing permissions and limitations under the License.
```

### The PostgreSQL Licence

```text
PostgreSQL Database Management System
(formerly known as Postgres, then as Postgres95)

Portions Copyright (c) 1996-2025, The PostgreSQL Global Development Group

Portions Copyright (c) 1994, The Regents of the University of California

Permission to use, copy, modify, and distribute this software and its
documentation for any purpose, without fee, and without a written agreement is
hereby granted, provided that the above copyright notice and this paragraph and
the following two paragraphs appear in all copies.

IN NO EVENT SHALL THE UNIVERSITY OF CALIFORNIA BE LIABLE TO ANY PARTY FOR
DIRECT, INDIRECT, SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES, INCLUDING LOST
PROFITS, ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS DOCUMENTATION, EVEN IF
THE UNIVERSITY OF CALIFORNIA HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH
DAMAGE.

THE UNIVERSITY OF CALIFORNIA SPECIFICALLY DISCLAIMS ANY WARRANTIES, INCLUDING,
BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
PARTICULAR PURPOSE. THE SOFTWARE PROVIDED HEREUNDER IS ON AN "AS IS" BASIS, AND
THE UNIVERSITY OF CALIFORNIA HAS NO OBLIGATIONS TO PROVIDE MAINTENANCE,
SUPPORT, UPDATES, ENHANCEMENTS, OR MODIFICATIONS.
```
