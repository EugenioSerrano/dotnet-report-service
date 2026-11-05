# Third-party tools in the test oracle image — ADR-013 C3

**Status: accepted by the owner on 2026-11-02.** This record is the C3 condition of ADR-013
before the first build of `eng/pdf-oracle`: the owner of the G21 waiver accepts
the package-to-licence list, and the image build records the exact versions it
actually installed. Nothing in this image is shipped, published or composed; it
is a test-only reader, invoked as separate processes, never linked into a
product assembly (ADR-013 C1-C5).

- **Base:** `mcr.microsoft.com/dotnet/aspnet:10.0.13-noble@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac` (Ubuntu 24.04.5 LTS, amd64).
- **Installed by the image:** `apt-get install --no-install-recommends poppler-utils qpdf` — the probe reported **50 packages newly installed and 0 upgraded** (97 → 147) on 2026-11-02, four disposable containers, `--rm`, nothing installed on a host; the raw diff is not retained, so the build re-reads the set.
- **Invocation:** `pdftotext`, `pdfinfo`, `pdftoppm` and `qpdf` as separate processes; no library from this list is loaded into a test process.
- **Waiver:** ADR-013 §3.1 covers poppler-utils and qpdf **with the apt packages installed as their dependencies**; the rows below are that dependency closure. The list changes only by the owner's commit (C3).

## 1. Packages installed by the oracle image (50)

Versions are the archive state on 2026-11-02, the probe date. The build snapshots
`dpkg-query -W` before and after the install and takes the **added or changed**
packages as its package set (the probe's method); the unchanged base packages
already present are outside C3. For every package in that set, the build re-reads
the version and the **full** `/usr/share/doc/<pkg>/copyright`, records both with
the image digest in the Bolt's SPEC/MEM evidence, and stops when a package is
absent from this list, when **any stanza** in the copyright file is not among
the expressions the row lists, or when a copyright file is missing.
`poppler-utils` and `qpdf` are pinned by the SPEC; the 48 dependencies are
re-read per build. The row's version is the probe date, not a pin for the 48.

### Probe (2026-11-02)

`docker run --rm --user 0:0 <base>@sha256:d5633778… bash -lc 'apt-get update -qq; dpkg-query -W -f="${Package} ${Version}\n" >/tmp/before; apt-get install -y -qq --no-install-recommends poppler-utils qpdf; dpkg-query -W -f="${Package} ${Version}\n" >/tmp/after; diff /tmp/before /tmp/after; for p in $(diff /tmp/before /tmp/after | sed -n "s/^> //p" | cut -d" " -f1); do echo "== $p"; cat /usr/share/doc/$p/copyright; done'`

Four disposable containers on the pinned base; nothing installed on a host. A
package whose version changed is treated as changed and **inside** the set; only
the unchanged base packages are outside C3. The 0-upgraded figure is the
acceptance basis; the raw diff is not retained, so the per-build re-read above is
the control, and this table is the acceptance basis, not a version pin.

| # | Package | Version (probe 2026-11-02) | Licence (from `/usr/share/doc/<pkg>/copyright`) |
|---|---------|----------------------------|--------------------------------------------------|
| 1 | fontconfig-config | 2.15.0-1.1ubuntu2 | permissive fontconfig grant (MIT/X11-style, © Keith Packard) |
| 2 | fonts-dejavu-core | 2.37-8 | Bitstream Vera + GPL-2+ (font exception) |
| 3 | fonts-dejavu-mono | 2.37-8 | Bitstream Vera + GPL-2+ (font exception) |
| 4 | libbrotli1 | 1.1.0-2ubuntu0.1 | MIT |
| 5 | libbsd0 | 0.12.1-1build1.1 | BSD-3-clause (+ BSD-2/BSD-4 variants) |
| 6 | libcairo2 | 1.18.0-3build1 | LGPL-2.1 or MPL-1.1 |
| 7 | libcurl3t64-gnutls | 8.5.0-2ubuntu10.16 | curl (MIT-like) (+ ISC, OLDAP-2.8, GPL-2+ with Autoconf exception) |
| 8 | libdeflate0 | 1.19-1build1.1 | Expat (MIT) |
| 9 | libexpat1 | 2.6.1-2ubuntu0.6 | MIT |
| 10 | libfontconfig1 | 2.15.0-1.1ubuntu2 | permissive fontconfig grant (MIT/X11-style) |
| 11 | libfreetype6 | 2.13.2+dfsg-1ubuntu0.2 | FTL or GPL-2+ (+ BSD-3, FSFAP) |
| 12 | libgssapi-krb5-2 | 1.20.1-6ubuntu2.10 | MIT (Kerberos) |
| 13 | libjbig0 | 2.1-6.1ubuntu2 | GPL-2+ |
| 14 | libjpeg-turbo8 | 2.1.5-2ubuntu2 | BSD-3/IJG, Zlib, Expat |
| 15 | libjpeg8 | 8c-2ubuntu11 | LGPL-2.1 (empty transitional package) |
| 16 | libk5crypto3 | 1.20.1-6ubuntu2.10 | MIT (Kerberos) |
| 17 | libkeyutils1 | 1.6.3-3build1 | GPL-2+ / LGPL-2+ |
| 18 | libkrb5-3 | 1.20.1-6ubuntu2.10 | MIT (Kerberos) |
| 19 | libkrb5support0 | 1.20.1-6ubuntu2.10 | MIT (Kerberos) |
| 20 | liblcms2-2 | 2.14-2ubuntu0.1 | MIT (+ GPL-3, IJG GPL-2+) |
| 21 | libldap2 | 2.6.10+dfsg-0ubuntu0.24.04.1 | OpenLDAP-2.8 (+ FSF-unlimited, GPL-2+/GPL-3+ with Autoconf exception) |
| 22 | liblerc4 | 4.0.0+ds-4ubuntu2 | Apache-2.0 |
| 23 | libnghttp2-14 | 1.59.0-1ubuntu0.4 | Expat (MIT) (+ all-permissive, GPL-3+ with Autoconf exception) |
| 24 | libnspr4 | 2:4.35-1.1build1 | MPL-2.0 |
| 25 | libnss3 | 2:3.98-1ubuntu0.3 | MPL-2.0 (+ Zlib, BSD-3, public-domain) |
| 26 | libopenjp2-7 | 2.5.0-2ubuntu0.5 | BSD-2 (+ MIT, BSD-3, public-domain) |
| 27 | libpixman-1-0 | 0.42.2-1build1 | MIT |
| 28 | libpng16-16t64 | 1.6.43-5ubuntu0.7 | libpng (+ Expat, GPL-2+ or BSD-like with advertising clause) |
| 29 | libpoppler134 | 24.02.0-1ubuntu9.11 | GPL-2 or GPL-3 (+ Apache-2.0 stanzas) |
| 30 | libpsl5t64 | 0.21.2-1.1build1 | MIT (+ gnulib, Chromium) |
| 31 | libqpdf29t64 | 11.9.0-1.1ubuntu0.1 | Apache-2.0 (+ SAPHIR MIT-style, RSA notice) |
| 32 | librtmp1 | 2.4+20151223.gitfa8646d.1-2build7 | GPL-2+ and LGPL-2.1 (per file) |
| 33 | libsasl2-2 | 2.1.28+dfsg1-5ubuntu3.1 | BSD-3-Clause-Attribution (+ BSD-2, GPL-3+) |
| 34 | libsasl2-modules-db | 2.1.28+dfsg1-5ubuntu3.1 | BSD-3-Clause-Attribution (+ BSD-2, GPL-3+) |
| 35 | libsharpyuv0 | 1.3.2-0.4build3 | BSD-3-Clause (+ Apache-2.0) |
| 36 | libsqlite3-0 | 3.45.1-1ubuntu2.8 | public domain (option GPL-2+) |
| 37 | libssh-4 | 0.10.6-2ubuntu0.5 | LGPL-2.1 (+ public-domain, BSD-2) |
| 38 | libtiff6 | 4.5.1+git230720-4ubuntu2.5 | libtiff (BSD-like / Hylafax) |
| 39 | libwebp7 | 1.3.2-0.4build3 | BSD-3-Clause (+ Apache-2.0) |
| 40 | libx11-6 | 2:1.8.7-1ubuntu0.1 | MIT (X.Org variant) |
| 41 | libx11-data | 2:1.8.7-1ubuntu0.1 | MIT (X.Org variant) |
| 42 | libxau6 | 1:1.0.9-1build6 | MIT (X.Org) |
| 43 | libxcb-render0 | 1.15-1ubuntu2 | MIT (X.Org) |
| 44 | libxcb-shm0 | 1.15-1ubuntu2 | MIT (X.Org) |
| 45 | libxcb1 | 1.15-1ubuntu2 | MIT (X.Org) |
| 46 | libxdmcp6 | 1:1.1.3-0ubuntu6 | MIT (X.Org) |
| 47 | libxext6 | 2:1.3.4-1build2 | MIT (X.Org) |
| 48 | libxrender1 | 1:0.9.10-1.1build1 | MIT (X.Org) |
| 49 | poppler-utils | 24.02.0-1ubuntu9.11 | GPL-2 or GPL-3 (+ Apache-2.0 stanzas) |
| 50 | qpdf | 11.9.0-1.1ubuntu0.1 | Apache-2.0 (+ SAPHIR MIT-style, RSA notice) |

**Copyleft rows the acceptance must see:** 2-3 (DejaVu, Bitstream Vera + GPL-2+
font exception), 6 (cairo, LGPL-2.1/MPL-1.1), 11 (freetype, FTL or GPL-2+), 13
(jbig, GPL-2+), 15 (libjpeg8, LGPL-2.1), 17 (keyutils, GPL-2+/LGPL-2+), 20
(lcms2, GPL-3 stanza), 23 (nghttp2, GPL-3+ with Autoconf exception), 24-25 (NSS,
MPL-2.0), 28 (libpng, GPL-2+ alternative), 29 and 49 (poppler, GPL-2/GPL-3), 30
(psl, gnulib stanza), 32 (rtmp, GPL-2+/LGPL-2.1), 37 (ssh, LGPL-2.1), and the
GPL stanzas inside 7, 21, 33, 34 and 36. All of them are apt dependencies
installed by the two top-level packages; ADR-013 §3.1 covers exactly that
closure. The row lists **every** expression the copyright file may carry; the
build accepts the file only if **all** its stanzas are among them, so an added
stanza (for example an AGPL stanza) stops the build. No branch is elected: a
dual-licence file is listed by all its branches, and the build records the exact
stanzas and the copyright file's SHA-256. The waiver does not cover a library
loaded into a test process.

**qpdf is in the closure and the gate is `waived`.** `qpdf` and `libqpdf29t64`
are Apache-2.0; ADR-013 §3.1 item 1 says qpdf needs no waiver, while item 4
orders the gate recorded `waived — ADR-013` for each package of the closure.
The SPEC's gate row records the closure as waived, following item 4; this C3
records the package-to-licence list; the item-1/item-4 tension is the accepted
ADR's and needs an OQ or a superseding ADR, not a one-sided reading.
SPEC-261101-0254's gate row is therefore compliant.

## 2. Packages already in the base image (22) — outside the C3 accepted closure

The probe's diff is the 50 packages the install adds. The base image also carries
the 22 packages below. They are already present in the base image and were not
installed by the oracle; the other 75 base packages are also outside C3. The 22
are the shared-library closure of the two binaries (`ldd` on `pdftotext` and
`qpdf` in the base image); the other 75 are not part of that closure.
**They are outside the C3 accepted closure:** ADR-013 §3.1 item 1 covers the apt
packages installed as dependencies of poppler-utils and qpdf, and ADR-004's
base-image note leaves the base OS packages under their own licences. They are
listed for completeness only; they are not accepted here and need no row in
section 1. **Their licences were not read by the probe**; if a future scope ever
includes the base, they must be read the same way before that acceptance.

| Package | Version |
|---------|---------|
| gcc-14-base | 14.2.0-4ubuntu2~24.04.1 |
| libbz2-1.0 | 1.0.8-5.1ubuntu0.1 |
| libc6 | 2.39-0ubuntu8.9 |
| libcom-err2 | 1.47.0-2.4~exp1ubuntu4.1 |
| libdb5.3t64 | 5.3.28+dfsg2-7 |
| libffi8 | 3.4.6-1build1 |
| libgcc-s1 | 14.2.0-4ubuntu2~24.04.1 |
| libgmp10 | 2:6.3.0+dfsg-2ubuntu6.1 |
| libgnutls30t64 | 3.8.3-1.1ubuntu3.6 |
| libhogweed6t64 | 3.9.1-2.2build1.1 |
| libidn2-0 | 2.3.7-2build1.1 |
| liblzma5 | 5.6.1+really5.4.5-1ubuntu0.3 |
| libmd0 | 1.1.0-2build1.1 |
| libnettle8t64 | 3.9.1-2.2build1.1 |
| libp11-kit0 | 0.25.3-4ubuntu2.2 |
| libssl3t64 | 3.0.13-0ubuntu3.16 |
| libstdc++6 | 14.2.0-4ubuntu2~24.04.1 |
| libtasn1-6 | 4.19.0-3ubuntu0.24.04.2 |
| libunistring5 | 1.1-2build1.1 |
| libzstd1 | 1.5.5+dfsg2-2build1.1 |
| sysvinit-utils | 3.08-6ubuntu3 |
| zlib1g | 1:1.3.dfsg-3.1ubuntu2.2 |

## 3. Owner acceptance (C3)

**Status: accepted by the owner on 2026-11-02; section 1 only.** The owner of the
ADR-013 waiver, `human:eugenioserrano`, accepts the package-to-licence rows of
section 1 and no others. Section 2 is outside this acceptance. The build stops
when a package is absent, when **any stanza** in the copyright file is not among
the expressions the row lists, or when a copyright file is missing. Exact
versions and licences, and the image digest, are recorded per build; the list
changes only by the owner's commit, which is the record of this acceptance.

- Accepted by: `human:eugenioserrano` (commit `611fd0f`, 2026-11-02T15:09:40-03:00; its author is `Eugenio Serrano <eugenioserrano@msn.com>`, its parent `e9dc44e`, and it changes only this note — confirmed from git history by `agent:avenga-devflow` on 2026-11-02)
- Date: `2026-11-02`
- Scope accepted: **section 1 only** (the 50 packages installed by the image; the 22 base packages are not part of this acceptance).

SPEC-261101-0254 §6 B.3 requires that this list be proposed for
`human:eugenioserrano`'s acceptance before the first build; section 1 is the
proposal, and the commit above is the acceptance. C1/C2 are stated and verified
in SPEC-261101-0254 and the V-Bounce MEM; C4/C5 are outside this note.
