# Template engine limits

Each runaway template runs under a 10-second wall-clock guard enforced by the harness (the output writer stops the render once the guard has elapsed). "Stop behaviour" is what happened, with the elapsed time, the characters written and the managed memory allocated during the render.

| Engine | Limit | Default | Value used | Runaway template | Stop behaviour | Cancellation available |
|---|---|---|---|---|---|---|
| Scriban 7.5.1 | `LoopLimit` | 1000 | default | `{{ for i in 1..100000000 }}x{{ end }}` | `ScriptRuntimeException`: <input>(1,14) : error : Range expression exceeds LoopLimit `1000`. — 0.01 s, 0 characters written, 0 MiB allocated | yes (`TemplateContext.CancellationToken`) |
| Scriban 7.5.1 | `RecursiveLimit` / `LimitToString` | 100 / 1048576 | default | — (not exercised) | — | yes (`TemplateContext.CancellationToken`) |
| Liquid (Fluid 2.40.0) | `MaxSteps` | 0 | 100000 | `{% for i in (1..100000000) %}x{% endfor %}` | `InvalidOperationException`: The maximum level of recursion has been reached. Your script must have a cyclic include statement. — 6.83 s, 50000 characters written, 4588 MiB allocated | not available in 2.40.0 (`TemplateContext` has no `CancellationToken`) |
| Liquid (Fluid 2.40.0) | `MaxRecursion` | 100 | default | — (no includes are used) | — | not available in 2.40.0 (`TemplateContext` has no `CancellationToken`) |
| Liquid (Fluid 2.40.0) | output size (`MaxOutputSize`) | not available in 2.40.0 | — | `{{ big }}` with a 2 MiB value | completed (no limit reached) — 0.01 s, 2097152 characters written, 0 MiB allocated — no output limit in 2.40.0 | not available in 2.40.0 (`TemplateContext` has no `CancellationToken`) |
| Handlebars.Net 2.4.3 | none (no step, loop or output limit) | — | — | `{{#each big}}x{{/each}}` over 10 000 000 elements | completed (no limit reached) — 0.58 s, 10000000 characters written, 458 MiB allocated | no (the compiled template takes no cancellation token) |
