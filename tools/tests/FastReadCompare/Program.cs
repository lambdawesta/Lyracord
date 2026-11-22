using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using DSharpPlus.Entities;
using Newtonsoft.Json;

// Compares the fast readers (channels, overwrites, roles, permissions) with the general serializer on the same JSON.
class Program
{
    static FieldInfo Enabled = typeof(DiscordGuild).Assembly
        .GetType("DSharpPlus.Net.Serialization.FastEntityReader")
        .GetField("Enabled", BindingFlags.Static | BindingFlags.NonPublic);

    static int Main()
    {
        var json = BuildGuild(channels: 400, roles: 250, seed: 1);
        var fast = Parse(json, true);
        var slow = Parse(json, false);

        var errors = new List<string>();
        Compare(slow, fast, errors);
        Console.WriteLine($"channels {Channels(fast).Count}/{Channels(slow).Count}, roles {Roles(fast).Count}/{Roles(slow).Count}");
        foreach (var e in errors.Take(40))
            Console.WriteLine("DIFF " + e);
        Console.WriteLine(errors.Count == 0 ? "OK: identical" : $"FAILED: {errors.Count} differences");

        // rough timing (PC JIT: reflection is far cheaper here than under .NET Native on the phone)
        var big = BuildGuild(channels: 3000, roles: 2000, seed: 2);
        for (int i = 0; i < 2; i++) { Parse(big, true); Parse(big, false); }
        var sw = Stopwatch.StartNew(); for (int i = 0; i < 5; i++) Parse(big, false); var slowMs = sw.Elapsed.TotalMilliseconds / 5;
        sw.Restart(); for (int i = 0; i < 5; i++) Parse(big, true); var fastMs = sw.Elapsed.TotalMilliseconds / 5;
        Console.WriteLine($"big guild ({big.Length / 1024} KB): general {slowMs:0.0} ms, fast {fastMs:0.0} ms");
        return errors.Count == 0 ? 0 : 1;
    }

    static DiscordGuild Parse(string json, bool fast)
    {
        Enabled.SetValue(null, fast);
        // same as READY: a plain JsonSerializer
        using var reader = new JsonTextReader(new StringReader(json));
        return new JsonSerializer().Deserialize<DiscordGuild>(reader);
    }

    static IDictionary Channels(DiscordGuild g) => (IDictionary)typeof(DiscordGuild).GetField("_channels", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(g);
    static IDictionary Roles(DiscordGuild g) => (IDictionary)typeof(DiscordGuild).GetField("_roles", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(g);

    static void Compare(DiscordGuild a, DiscordGuild b, List<string> errors)
    {
        CompareDict(Channels(a), Channels(b), "channel", errors);
        CompareDict(Roles(a), Roles(b), "role", errors);
    }

    static void CompareDict(IDictionary a, IDictionary b, string what, List<string> errors)
    {
        if (a.Count != b.Count) errors.Add($"{what} count {a.Count} vs {b.Count}");
        foreach (DictionaryEntry e in a)
        {
            if (!b.Contains(e.Key)) { errors.Add($"{what} {e.Key} missing"); continue; }
            var x = e.Value; var y = b[e.Key];
            if (x.GetType() != y.GetType()) errors.Add($"{what} {e.Key} type {x.GetType().Name} vs {y.GetType().Name}");
            CompareMembers(x, y, $"{what} {e.Key}", errors);
        }
    }

    static void CompareMembers(object x, object y, string where, List<string> errors)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        for (var t = x.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var m in t.GetMembers(all | BindingFlags.DeclaredOnly))
            {
                if (m.GetCustomAttribute<JsonPropertyAttribute>() == null) continue;
                object vx, vy;
                if (m is PropertyInfo p) { vx = p.GetValue(x); vy = p.GetValue(y); }
                else if (m is FieldInfo f) { vx = f.GetValue(x); vy = f.GetValue(y); }
                else continue;
                if (vx is IList lx && vy is IList ly)
                {
                    if (lx.Count != ly.Count) { errors.Add($"{where}.{m.Name} count {lx.Count} vs {ly.Count}"); continue; }
                    for (int i = 0; i < lx.Count; i++) CompareMembers(lx[i], ly[i], $"{where}.{m.Name}[{i}]", errors);
                    continue;
                }
                if (vx != null && vy != null && vx.GetType().Namespace == "DSharpPlus.Entities" && !vx.GetType().IsEnum)
                { CompareMembers(vx, vy, $"{where}.{m.Name}", errors); continue; }
                if (!Equals(vx, vy)) errors.Add($"{where}.{m.Name}: {vx ?? "null"} vs {vy ?? "null"}");
            }
        }
    }

    // ------------------------------------------------------------------ test data shaped like Discord's

    static string BuildGuild(int channels, int roles, int seed)
    {
        var r = new Random(seed);
        var sb = new StringBuilder();
        sb.Append("{\"id\":\"1233492259937452205\",\"name\":\"Test\",\"channels\":[");
        int[] types = { 0, 2, 4, 5, 13, 15, 0, 0, 2, 14, 6, 0 };
        for (int i = 0; i < channels; i++)
        {
            if (i > 0) sb.Append(',');
            var type = types[r.Next(types.Length)];
            var id = 1275138759612170000UL + (ulong)i;
            sb.Append('{');
            var parts = new List<string>
            {
                $"\"id\":\"{id}\"",
                $"\"type\":{type}",
                $"\"name\":\"канал-{i} «{r.Next(1000)}» \\u2728\"",
                $"\"position\":{r.Next(50)}",
                r.Next(3) == 0 ? "\"parent_id\":null" : $"\"parent_id\":\"{1275138759612100000UL + (ulong)r.Next(20)}\"",
                $"\"nsfw\":{(r.Next(5) == 0 ? "true" : "false")}",
                $"\"flags\":{r.Next(4)}",
                $"\"version\":{r.Next(100000)}",
            };
            if (r.Next(2) == 0) parts.Add($"\"topic\":\"тема {i}\"");
            if (r.Next(4) == 0) parts.Add("\"topic\":null");
            if (r.Next(2) == 0) parts.Add($"\"last_message_id\":\"{1572700000000000000UL + (ulong)r.Next(100000)}\"");
            if (r.Next(3) == 0) parts.Add("\"last_message_id\":null");
            if (type == 2 || type == 13) { parts.Add($"\"bitrate\":{64000 + r.Next(5) * 1000}"); parts.Add($"\"user_limit\":{r.Next(20)}"); parts.Add(r.Next(2) == 0 ? "\"rtc_region\":null" : "\"rtc_region\":\"rotterdam\""); if (r.Next(2) == 0) parts.Add("\"video_quality_mode\":2"); }
            parts.Add(r.Next(3) == 0 ? "\"rate_limit_per_user\":0" : (r.Next(2) == 0 ? $"\"rate_limit_per_user\":{r.Next(60)}" : "\"rate_limit_per_user\":null"));
            if (r.Next(3) == 0) parts.Add($"\"last_pin_timestamp\":\"2024-0{1 + r.Next(9)}-1{r.Next(9)}T12:3{r.Next(9)}:00.{r.Next(999):000}000+00:00\"");
            if (type == 15) parts.Add("\"available_tags\":[{\"id\":\"1\",\"name\":\"tag\",\"moderated\":false,\"emoji_id\":null,\"emoji_name\":\"x\"}],\"default_sort_order\":null,\"default_forum_layout\":0");
            var ow = new StringBuilder("\"permission_overwrites\":[");
            int n = r.Next(5);
            for (int k = 0; k < n; k++)
            {
                if (k > 0) ow.Append(',');
                var allow = ((ulong)r.Next() << 20) | (ulong)r.Next();
                var deny = r.Next(3) == 0 ? "0" : (((ulong)r.Next() << 18) | (ulong)r.Next()).ToString();
                ow.Append($"{{\"id\":\"{1233492259937452205UL + (ulong)r.Next(100)}\",\"type\":{r.Next(2)},\"allow\":\"{allow}\",\"deny\":\"{deny}\"}}");
            }
            ow.Append(']');
            parts.Add(ow.ToString());
            if (r.Next(10) == 0) parts.Add("\"permissions\":\"2251799813685247\"");
            // shuffle key order like real payloads
            foreach (var p in parts.OrderBy(_ => r.Next())) sb.Append(p).Append(',');
            sb.Length--;
            sb.Append('}');
        }
        sb.Append("],\"roles\":[");
        for (int i = 0; i < roles; i++)
        {
            if (i > 0) sb.Append(',');
            var parts = new List<string>
            {
                $"\"id\":\"{1233492259937452205UL + (ulong)i}\"",
                $"\"name\":\"роль {i}\"",
                $"\"color\":{r.Next(0xFFFFFF)}",
                $"\"hoist\":{(r.Next(2) == 0 ? "true" : "false")}",
                $"\"position\":{r.Next(300)}",
                $"\"permissions\":\"{((ulong)r.Next() << 22) | (ulong)r.Next()}\"",
                $"\"managed\":{(r.Next(8) == 0 ? "true" : "false")}",
                $"\"mentionable\":{(r.Next(2) == 0 ? "true" : "false")}",
                r.Next(4) == 0 ? "\"icon\":\"a1b2c3\"" : "\"icon\":null",
                r.Next(5) == 0 ? "\"unicode_emoji\":\"🎂\"" : "\"unicode_emoji\":null",
                $"\"flags\":{r.Next(2)}",
                $"\"description\":null",
            };
            if (r.Next(6) == 0) parts.Add(r.Next(2) == 0 ? "\"tags\":{\"bot_id\":\"123\"}" : "\"tags\":{\"premium_subscriber\":null}");
            if (r.Next(10) == 0) parts.Add("\"colors\":{\"primary_color\":1,\"secondary_color\":null}");
            sb.Append('{');
            foreach (var p in parts.OrderBy(_ => r.Next())) sb.Append(p).Append(',');
            sb.Length--;
            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }
}
