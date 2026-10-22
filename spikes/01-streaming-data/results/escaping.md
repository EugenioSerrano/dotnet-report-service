# Escaping: native behaviour of each engine and the common policy

Policy = the output of `System.Net.WebUtility.HtmlEncode`. "Native" = the engine's own default: Scriban writes `{{ value }}` unescaped and escapes only through `html.escape`; Liquid (Fluid) with its default `HtmlEncoder.Default`; Handlebars with its default encoder. "With policy" = the configuration the measurements use. ✔ = equal to the policy.

| Value | Policy | Scriban `{{ v }}` | Scriban `html.escape` | Liquid native | Liquid with policy | Handlebars native | Handlebars with policy |
|---|---|---|---|---|---|---|---|
| `` <script>alert(1)</script> `` | `` &lt;script&gt;alert(1)&lt;/script&gt; `` | `` <script>alert(1)</script> `` | `` &lt;script&gt;alert(1)&lt;/script&gt; `` ✔ | `` &lt;script&gt;alert(1)&lt;/script&gt; `` ✔ | `` &lt;script&gt;alert(1)&lt;/script&gt; `` ✔ | `` &lt;script&gt;alert(1)&lt;/script&gt; `` ✔ | `` &lt;script&gt;alert(1)&lt;/script&gt; `` ✔ |
| `` Tom & Jerry "quoted" <b>bold</b> `` | `` Tom &amp; Jerry &quot;quoted&quot; &lt;b&gt;bold&lt;/b&gt; `` | `` Tom & Jerry "quoted" <b>bold</b> `` | `` Tom &amp; Jerry &quot;quoted&quot; &lt;b&gt;bold&lt;/b&gt; `` ✔ | `` Tom &amp; Jerry &quot;quoted&quot; &lt;b&gt;bold&lt;/b&gt; `` ✔ | `` Tom &amp; Jerry &quot;quoted&quot; &lt;b&gt;bold&lt;/b&gt; `` ✔ | `` Tom &amp; Jerry &quot;quoted&quot; &lt;b&gt;bold&lt;/b&gt; `` ✔ | `` Tom &amp; Jerry &quot;quoted&quot; &lt;b&gt;bold&lt;/b&gt; `` ✔ |
| `` '; DROP TABLE items; -- `` | `` &#39;; DROP TABLE items; -- `` | `` '; DROP TABLE items; -- `` | `` &#39;; DROP TABLE items; -- `` ✔ | `` &#x27;; DROP TABLE items; -- `` | `` &#39;; DROP TABLE items; -- `` ✔ | `` &#x27;; DROP TABLE items; -- `` | `` &#39;; DROP TABLE items; -- `` ✔ |
| `` {{ header.customer.name }} `` | `` {{ header.customer.name }} `` | `` {{ header.customer.name }} `` ✔ | `` {{ header.customer.name }} `` ✔ | `` {{ header.customer.name }} `` ✔ | `` {{ header.customer.name }} `` ✔ | `` {{ header.customer.name }} `` ✔ | `` {{ header.customer.name }} `` ✔ |
| `` {% raw %}{{ 7*7 }}{% endraw %} `` | `` {% raw %}{{ 7*7 }}{% endraw %} `` | `` {% raw %}{{ 7*7 }}{% endraw %} `` ✔ | `` {% raw %}{{ 7*7 }}{% endraw %} `` ✔ | `` {% raw %}{{ 7*7 }}{% endraw %} `` ✔ | `` {% raw %}{{ 7*7 }}{% endraw %} `` ✔ | `` {% raw %}{{ 7*7 }}{% endraw %} `` ✔ | `` {% raw %}{{ 7*7 }}{% endraw %} `` ✔ |
| `` ${7*7} `` | `` ${7*7} `` | `` ${7*7} `` ✔ | `` ${7*7} `` ✔ | `` ${7*7} `` ✔ | `` ${7*7} `` ✔ | `` ${7*7} `` ✔ | `` ${7*7} `` ✔ |
| `` Año ñ € `` | `` A&#241;o &#241; € `` | `` Año ñ € `` | `` A&#241;o &#241; € `` ✔ | `` A&#xF1;o &#xF1; &#x20AC; `` | `` A&#241;o &#241; € `` ✔ | `` Año ñ € `` | `` A&#241;o &#241; € `` ✔ |
| `` a`b=c `` | `` a`b=c `` | `` a`b=c `` ✔ | `` a`b=c `` ✔ | `` a`b=c `` ✔ | `` a`b=c `` ✔ | `` a&#x60;b&#x3D;c `` | `` a`b=c `` ✔ |
| `` 𝄞 `` | `` &#119070; `` | `` 𝄞 `` | `` &#119070; `` ✔ | `` &#x1D11E; `` | `` &#119070; `` ✔ | `` 𝄞 `` | `` &#119070; `` ✔ |
