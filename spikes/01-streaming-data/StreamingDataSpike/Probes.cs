using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Fluid;
using HandlebarsDotNet;
using Scriban.Runtime;
using StreamingDataSpike.Rendering;

namespace StreamingDataSpike;

/// <summary>The escaping and limits probes; both print a Markdown table.</summary>
internal static class Probes
{
    /// <summary>The hostile values of the pilot datasets.</summary>
    public static readonly string[] HostileValues =
    [
        "<script>alert(1)</script>",
        "Tom & Jerry \"quoted\" <b>bold</b>",
        "'; DROP TABLE items; --",
        "{{ header.customer.name }}",
        "{% raw %}{{ 7*7 }}{% endraw %}",
        "${7*7}",
    ];

    private static readonly string[] EscapeValues = [.. HostileValues, "Año ñ €", "a`b=c", "𝄞"];

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    /// <summary>Native escaping of each engine next to the policy, for every probe value.</summary>
    public static int Escape()
    {
        var output = new StringBuilder();
        output.AppendLine("# Escaping: native behaviour of each engine and the common policy");
        output.AppendLine();
        output.AppendLine("Policy = the output of `System.Net.WebUtility.HtmlEncode`. \"Native\" = the engine's own default: Scriban writes `{{ value }}` unescaped and escapes only through `html.escape`; Liquid (Fluid) with its default `HtmlEncoder.Default`; Handlebars with its default encoder. \"With policy\" = the configuration the measurements use. ✔ = equal to the policy.");
        output.AppendLine();
        output.AppendLine("| Value | Policy | Scriban `{{ v }}` | Scriban `html.escape` | Liquid native | Liquid with policy | Handlebars native | Handlebars with policy |");
        output.AppendLine("|---|---|---|---|---|---|---|---|");

        var parser = new FluidParser();
        parser.TryParse("{{ v }}", out var liquid, out _);
        var liquidOptions = new TemplateOptions { CultureInfo = CultureInfo.InvariantCulture };
        var handlebarsNative = Handlebars.Create(new HandlebarsConfiguration { FormatProvider = CultureInfo.InvariantCulture }).Compile("{{ v }}");
        var handlebarsPolicy = Handlebars.Create(new HandlebarsConfiguration { FormatProvider = CultureInfo.InvariantCulture, TextEncoder = new HtmlPolicyHandlebarsEncoder() }).Compile("{{ v }}");
        var scribanRaw = Scriban.Template.Parse("{{ v }}");
        var scribanEscaped = Scriban.Template.Parse("{{ v | html.escape }}");

        foreach (var value in EscapeValues)
        {
            var policy = HtmlPolicy.Encode(value);
            var model = new Dictionary<string, object?> { ["v"] = value };
            var cells = new[]
            {
                RenderScriban(scribanRaw, model),
                RenderScriban(scribanEscaped, model),
                RenderLiquid(liquid, liquidOptions, model, System.Text.Encodings.Web.HtmlEncoder.Default),
                RenderLiquid(liquid, liquidOptions, model, HtmlPolicyTextEncoder.Instance),
                handlebarsNative(model),
                handlebarsPolicy(model),
            };

            output.Append("| ").Append(Code(value)).Append(" | ").Append(Code(policy));
            foreach (var cell in cells)
            {
                output.Append(" | ").Append(Code(cell)).Append(string.Equals(cell, policy, StringComparison.Ordinal) ? " ✔" : string.Empty);
            }

            output.AppendLine(" |");
        }

        Console.Out.Write(output.ToString().ReplaceLineEndings("\n"));
        return 0;
    }

    /// <summary>Runaway templates per engine, each under a 10-second wall-clock guard.</summary>
    public static int Limits()
    {
        var scribanDefaults = new Scriban.TemplateContext();
        var fluidDefaults = new TemplateOptions();
        var rows = new List<string>();

        // Scriban: the default loop limit.
        var scriban = Scriban.Template.Parse("{{ for i in 1..100000000 }}x{{ end }}");
        rows.Add(Row(
            "Scriban 7.5.1",
            "`LoopLimit`",
            scribanDefaults.LoopLimit.ToString(CultureInfo.InvariantCulture),
            "default",
            "`{{ for i in 1..100000000 }}x{{ end }}`",
            Guarded(writer =>
            {
                var context = new Scriban.TemplateContext();
                context.PushOutput(new TextWriterOutput(writer));
                scriban.Evaluate(context);
            }),
            HasProperty(typeof(Scriban.TemplateContext), "CancellationToken")));
        rows.Add(Row(
            "Scriban 7.5.1",
            "`RecursiveLimit` / `LimitToString`",
            scribanDefaults.RecursiveLimit.ToString(CultureInfo.InvariantCulture) + " / " + scribanDefaults.LimitToString.ToString(CultureInfo.InvariantCulture),
            "default",
            "— (not exercised)",
            "—",
            HasProperty(typeof(Scriban.TemplateContext), "CancellationToken")));

        // Liquid (Fluid): the step limit.
        var parser = new FluidParser();
        parser.TryParse("{% for i in (1..100000000) %}x{% endfor %}", out var loop, out _);
        rows.Add(Row(
            "Liquid (Fluid 2.40.0)",
            "`MaxSteps`",
            fluidDefaults.MaxSteps.ToString(CultureInfo.InvariantCulture),
            FluidRenderer.MaxSteps.ToString(CultureInfo.InvariantCulture),
            "`{% for i in (1..100000000) %}x{% endfor %}`",
            Guarded(writer =>
            {
                var context = new Fluid.TemplateContext(new TemplateOptions { MaxSteps = FluidRenderer.MaxSteps });
                loop.RenderAsync(writer, HtmlPolicyTextEncoder.Instance, context).AsTask().GetAwaiter().GetResult();
            }),
            FluidCancellation()));
        rows.Add(Row(
            "Liquid (Fluid 2.40.0)",
            "`MaxRecursion`",
            fluidDefaults.MaxRecursion.ToString(CultureInfo.InvariantCulture),
            "default",
            "— (no includes are used)",
            "—",
            FluidCancellation()));

        // Liquid (Fluid): an output above 1 MiB; Fluid 2.40.0 has no output-size limit.
        parser.TryParse("{{ big }}", out var bigOutput, out _);
        var bigValue = new string('y', 2 * 1_048_576);
        rows.Add(Row(
            "Liquid (Fluid 2.40.0)",
            "output size (`MaxOutputSize`)",
            typeof(TemplateOptions).GetProperty("MaxOutputSize") is not null ? "present" : NotInFluid,
            "—",
            "`{{ big }}` with a 2 MiB value",
            Guarded(writer =>
            {
                var context = new Fluid.TemplateContext(new TemplateOptions { MaxSteps = FluidRenderer.MaxSteps });
                context.SetValue("big", bigValue);
                bigOutput.RenderAsync(writer, HtmlPolicyTextEncoder.Instance, context).AsTask().GetAwaiter().GetResult();
            }) + " — no output limit in 2.40.0",
            FluidCancellation()));

        // Handlebars: no built-in limit.
        var handlebars = Handlebars.Create(new HandlebarsConfiguration { FormatProvider = CultureInfo.InvariantCulture, TextEncoder = new HtmlPolicyHandlebarsEncoder() });
        var each = handlebars.Compile(new StringReader("{{#each big}}x{{/each}}"));
        var list = new int[10_000_000];
        rows.Add(Row(
            "Handlebars.Net 2.4.3",
            "none (no step, loop or output limit)",
            "—",
            "—",
            "`{{#each big}}x{{/each}}` over 10 000 000 elements",
            Guarded(writer => each(writer, new Dictionary<string, object?> { ["big"] = list }, null!)),
            "no (the compiled template takes no cancellation token)"));

        var output = new StringBuilder();
        output.AppendLine("# Template engine limits");
        output.AppendLine();
        output.AppendLine("Each runaway template runs under a 10-second wall-clock guard enforced by the harness (the output writer stops the render once the guard has elapsed). \"Stop behaviour\" is what happened, with the elapsed time, the characters written and the managed memory allocated during the render.");
        output.AppendLine();
        output.AppendLine("| Engine | Limit | Default | Value used | Runaway template | Stop behaviour | Cancellation available |");
        output.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var row in rows)
        {
            output.AppendLine(row);
        }

        Console.Out.Write(output.ToString().ReplaceLineEndings("\n"));
        return 0;
    }

    private static string RenderScriban(Scriban.Template template, Dictionary<string, object?> model)
    {
        var globals = new ScriptObject();
        globals["v"] = model["v"];
        var context = new Scriban.TemplateContext();
        context.PushGlobal(globals);
        return template.Render(context);
    }

    private static string RenderLiquid(IFluidTemplate template, TemplateOptions options, Dictionary<string, object?> model, TextEncoder encoder)
    {
        var context = new Fluid.TemplateContext(options);
        context.SetValue("v", model["v"]);
        using var writer = new StringWriter();
        template.RenderAsync(writer, encoder, context).AsTask().GetAwaiter().GetResult();
        return writer.ToString();
    }

    private static string Guarded(Action<TextWriter> render)
    {
        using var writer = new GuardWriter(Guard);
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var clock = Stopwatch.StartNew();
        string outcome;
        try
        {
            var task = Task.Run(() => render(writer));
            if (!task.Wait(Guard + TimeSpan.FromSeconds(5)))
            {
                return $"did not stop within the guard ({clock.Elapsed.TotalSeconds:F1} s)";
            }

            outcome = "completed (no limit reached)";
        }
        catch (AggregateException exception) when (exception.InnerException is not null)
        {
            var inner = exception.InnerException;
            outcome = inner is GuardElapsedException
                ? "stopped only by the harness guard"
                : $"`{inner.GetType().Name}`: {inner.Message.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal)}";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{outcome} — {clock.Elapsed.TotalSeconds:F2} s, {writer.Characters} characters written, {(GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1_048_576.0:F0} MiB allocated");
    }

    private static string Row(string engine, string limit, string defaultValue, string used, string template, string behaviour, string cancellation) =>
        $"| {engine} | {limit} | {defaultValue} | {used} | {template} | {behaviour} | {cancellation} |";

    /// <summary>How the limits table names a member the pinned Fluid version does not have.</summary>
    private const string NotInFluid = "not available in 2.40.0";

    private static string FluidCancellation() =>
        typeof(Fluid.TemplateContext).GetProperty("CancellationToken") is not null
            ? HasProperty(typeof(Fluid.TemplateContext), "CancellationToken")
            : NotInFluid + " (`TemplateContext` has no `CancellationToken`)";

    private static string HasProperty(Type type, string name) =>
        type.GetProperty(name) is not null ? $"yes (`{type.Name}.{name}`)" : $"no (`{type.Name}` has no `{name}`)";

    private static string Code(string text) => "`` " + text.Replace("|", "\\|", StringComparison.Ordinal) + " ``";

    /// <summary>Counts what is written and stops the render once the guard has elapsed.</summary>
    private sealed class GuardWriter(TimeSpan guard) : TextWriter
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public long Characters { get; private set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Count(1);

        public override void Write(char[] buffer, int index, int count) => Count(count);

        public override void Write(string? value) => Count(value?.Length ?? 0);

        public override void Write(ReadOnlySpan<char> buffer) => Count(buffer.Length);

        private void Count(int characters)
        {
            Characters += characters;
            if (_clock.Elapsed > guard)
            {
                throw new GuardElapsedException();
            }
        }
    }

    private sealed class GuardElapsedException : Exception
    {
        public GuardElapsedException()
            : base("The wall-clock guard elapsed.")
        {
        }
    }
}
