using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace DTFBot
{
    internal static class Program
    {
        private static Discord.WebSocket.DiscordSocketClient _client;
        private static System.Timers.Timer _pollTimer;
        private static HashSet<string> _seenLogs = new HashSet<string>();
        private static bool _firstPoll = true;

        private static async Task Main(string[] args)
        {
            Cfg.LoadFile();
            EditStore.Load();

            if (string.IsNullOrWhiteSpace(Cfg.BotToken))
            {
                Console.WriteLine("================================================");
                Console.WriteLine("  DTF Bot - no token found!");
                Console.WriteLine("  Create config.json next to the exe:");
                Console.WriteLine('{' + "\"token\": \"YOUR_BOT_TOKEN\", \"guildId\": \"YOUR_SERVER_ID\"," + '"'.ToString() + "securityChannelId\": \"CHANNEL_ID\", \"adminRoleId\": \"ROLE_ID\"}");
                Console.WriteLine("  See SETUP.md for where to get each value.");
                Console.WriteLine("================================================");
                return;
            }

            _client = new Discord.WebSocket.DiscordSocketClient(new Discord.WebSocket.DiscordSocketConfig
            {
                GatewayIntents = Discord.GatewayIntents.Guilds | Discord.GatewayIntents.GuildMessages
            });

            // ---- keep-alive web server (lets Render free tier host this 24/7) ----
            // runs in background; if the port is taken (e.g. second instance), pick a random free one
            var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
            var wbuilder = WebApplication.CreateBuilder(args);
            wbuilder.WebHost.UseUrls("http://0.0.0.0:" + port);
            var wapp = wbuilder.Build();
            wapp.MapGet("/", () => Results.Text("DTF Bot alive"));
            wapp.MapGet("/health", () => Results.Json(new { alive = true, bot = _client?.CurrentUser?.Username ?? "starting" }));
            _ = Task.Run(async () =>
            {
                try { await wapp.RunAsync(); }
                catch (IOException)
                {
                    // port busy — another instance owns it; retry on a random port so we still boot
                    try
                    {
                        var wb2 = WebApplication.CreateBuilder(args);
                        wb2.WebHost.UseUrls("http://0.0.0.0:0");
                        var wa2 = wb2.Build();
                        wa2.MapGet("/", () => Results.Text("DTF Bot alive"));
                        await wa2.RunAsync();
                    }
                    catch { }
                }
                catch { }
            });

            // self-ping the public URL every 10 min so Render never sleeps
            var selfUrl = Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL");
            if (!string.IsNullOrEmpty(selfUrl))
            {
                var ping = new System.Timers.Timer(600_000);
                ping.Elapsed += async (s, e) => { try { using var hc = new HttpClient(); await hc.GetAsync(selfUrl + "/"); } catch { } };
                ping.Start();
            }

            _client.Log += m => { Console.WriteLine($"[bot] {m}"); return Task.CompletedTask; };
            _client.UserJoined += OnUserJoined;
            _client.ModalSubmitted += OnModal;
            _client.Ready += OnReady;
            _client.SlashCommandExecuted += OnSlash;
            _client.ButtonExecuted += OnButton;

            await _client.LoginAsync(Discord.TokenType.Bot, Cfg.BotToken);
            await _client.StartAsync();

            // gateway watchdog: if READY never arrives (silent drop on cloud hosts),
            // restart the client so the bot recovers by itself
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(60_000);
                    try
                    {
                        if (_client.ConnectionState == Discord.ConnectionState.Connected && _client.CurrentUser != null) continue;
                        Console.WriteLine("[bot] gateway not ready - restarting client");
                        await _client.StopAsync();
                        await Task.Delay(3_000);
                        await _client.StartAsync();
                    }
                    catch (Exception ex) { Console.WriteLine("[bot] gateway restart: " + ex.Message); }
                }
            });

            await Task.Delay(-1);
        }

        private static async Task OnReady()
        {
            Console.WriteLine($"[bot] connected as {_client.CurrentUser}");

            var guild = _client.GetGuild(Cfg.GuildId);
            if (guild == null) { Console.WriteLine("[bot] guildId wrong - commands not registered"); return; }

            // ---- slash commands ----
            var cmds = new List<Discord.ApplicationCommandProperties>
            {
                new Discord.SlashCommandBuilder().WithName("dtf").WithDescription("DTF showcase + pricelist").Build(),
                new Discord.SlashCommandBuilder().WithName("features").WithDescription("Full list of everything DTF cleans").Build(),
                new Discord.SlashCommandBuilder().WithName("howitworks").WithDescription("How each DTF cleaner works").Build(),
                new Discord.SlashCommandBuilder().WithName("key")
                    .WithDescription("Generate a license key for a client")
                    .AddOption(new Discord.SlashCommandOptionBuilder()
                        .WithName("name").WithDescription("client name").WithType(Discord.ApplicationCommandOptionType.String).WithRequired(true))
                    .AddOption(new Discord.SlashCommandOptionBuilder()
                        .WithName("days").WithDescription("how many days").WithType(Discord.ApplicationCommandOptionType.Integer).WithRequired(true))
                    .AddOption(new Discord.SlashCommandOptionBuilder()
                        .WithName("hwid").WithDescription("bind to HWID (optional)").WithType(Discord.ApplicationCommandOptionType.String).WithRequired(false))
                    .Build(),
                new Discord.SlashCommandBuilder().WithName("keys").WithDescription("List all keys + status").Build(),
                new Discord.SlashCommandBuilder().WithName("log").WithDescription("Last 15 access attempts").Build(),
                new Discord.SlashCommandBuilder().WithName("ban")
                    .WithDescription("Ban a key")
                    .AddOption(new Discord.SlashCommandOptionBuilder().WithName("key").WithDescription("the key").WithType(Discord.ApplicationCommandOptionType.String).WithRequired(true))
                    .Build(),
                new Discord.SlashCommandBuilder().WithName("unban")
                    .WithDescription("Unban a key")
                    .AddOption(new Discord.SlashCommandOptionBuilder().WithName("key").WithDescription("the key").WithType(Discord.ApplicationCommandOptionType.String).WithRequired(true))
                    .Build(),
                new Discord.SlashCommandBuilder().WithName("reset")
                    .WithDescription("Reset a key's HWID (client can activate on a new PC)")
                    .AddOption(new Discord.SlashCommandOptionBuilder().WithName("key").WithDescription("the key").WithType(Discord.ApplicationCommandOptionType.String).WithRequired(true))
                    .Build(),
                new Discord.SlashCommandBuilder().WithName("stats").WithDescription("Quick business stats").Build(),
                new Discord.SlashCommandBuilder().WithName("welcometest").WithDescription("Preview the welcome embed (admin)").Build(),
                new Discord.SlashCommandBuilder().WithName("vouch").WithDescription("Post the review/vouch session announcement (admin)").Build(),
                new Discord.SlashCommandBuilder().WithName("edit")
                    .WithDescription("Edit a bot message + image (admin)")
                    .AddOption(new Discord.SlashCommandOptionBuilder()
                        .WithName("command").WithDescription("which message to edit").WithType(Discord.ApplicationCommandOptionType.String).WithRequired(true)
                        .AddChoice("dtf showcase", "dtf")
                        .AddChoice("features list", "features")
                        .AddChoice("how it works", "howitworks")
                        .AddChoice("ticket panel", "ticketpanel"))
                    .Build(),
                new Discord.SlashCommandBuilder().WithName("message")
                    .WithDescription("Create a custom announcement with image + preview, then pin it (admin)")
                    .Build(),
                new Discord.SlashCommandBuilder().WithName("ticketpanel")
                    .WithDescription("Post the Create-ticket panel in this channel (admin)")
                    .Build(),
            };
            await guild.BulkOverwriteApplicationCommandAsync(cmds.ToArray());
            Console.WriteLine("[bot] slash commands registered");

            // ---- poll the access log for live security alerts ----
            _pollTimer = new System.Timers.Timer(15_000);
            _pollTimer.Elapsed += async (s, e) => await PollAccessLog();
            _pollTimer.Start();
        }

        // ---------------- welcome system ----------------

        private static async Task OnUserJoined(SocketGuildUser user)
        {
            try
            {
                var guild = user.Guild;
                var ch = guild.TextChannels.FirstOrDefault(c => c.Name == "welcome")
                    ?? guild.TextChannels.FirstOrDefault(c => c.Name == "rules");
                if (ch == null) return;
                await ch.SendMessageAsync(embed: BuildWelcome(user));
            }
            catch (Exception ex) { Console.WriteLine($"[welcome] {ex.Message}"); }
        }

        private static Discord.Embed BuildWelcome(SocketGuildUser user)
        {
            string av = user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl();
            return new Discord.EmbedBuilder()
                .WithColor(new Discord.Color(124, 58, 237))
                .WithTitle("\uD83D\uDC8E WELCOME TO DTF")
                .WithThumbnailUrl(av)
                .WithDescription(
                    $"{user.Mention}\n\n" +
                    "Welcome, boss. Maligayang pagdating sa DTF — kung saan ang bawat PC ay malinis, walang bakas, at walang nakakakita.")
                .AddField("\uD83D\uDD25 What is DTF",
                    "50+ one-click cleaners that wipe forensic traces, browser history, memory and junk — then cleans its own tracks on exit.", false)
                .AddField("\uD83D\uDCCD Start here",
                    "\u2022 Read **#rules** — bawal ang key sharing at scamming\n" +
                    "\u2022 Go to the ticket channel and click **🎫 Create ticket** — private ticket opens, doon ang bayaran at key", false)
                .AddField("\uD83D\uDCB0 Plans",
                    "\u2022 `\u20B1400` — 30 days\n\u2022 `\u20B1700` — 90 days\n\u2022 `\u20B11200` — Lifetime", false)
                .AddField("\u2764\uFE0F Ang rules ng barrio",
                    "Respeto. Loyalty. Walang scam.\n" +
                    "Dito walang palusot — ang nag-share ng key, permanently banned.", false)
                .WithImageUrl("https://dtf-license.onrender.com/assets/banner.png?v=2")
                .Build();
        }

        private static Discord.Embed BuildVouch()
        {
            return new Discord.EmbedBuilder()
                .WithColor(new Discord.Color(124, 58, 237))
                .WithTitle("\uD83D\uDC9C DTF REVIEW / VOUCH SESSION")
                .WithThumbnailUrl("https://dtf-license.onrender.com/assets/logo.png?v=2")
                .WithDescription(
                    "**Think DTF is worth it? Let everyone know.**\n\n" +
                    "We're hosting a **Review & Vouch Session** for all DTF clients.")
                .AddField("\uD83D\uDCCB How to participate",
                    "\u2022 Share your honest experience with DTF\n" +
                    "\u2022 Post your vouch/review in the designated channel\n" +
                    "\u2022 Include what you liked about the service\n" +
                    "\u2022 Screenshots or proof are welcome\n" +
                    "\u2022 No fake or misleading reviews", false)
                .AddField("\uD83D\uDC9C Why it matters",
                    "Your feedback helps DTF grow.\n" +
                    "**Real clients. Real reviews. Real experiences.**", false)
                .WithImageUrl("https://dtf-license.onrender.com/assets/banner.png?v=2")
                .WithFooter("DTF Built Different. \uD83D\uDC9C")
                .Build();
        }

        // ---------------- showcase + security feed ----------------

        private static async Task PollAccessLog()
        {
            try
            {
                if (Cfg.SecurityChannelId == 0) return;
                var ch = _client.GetChannel(Cfg.SecurityChannelId) as SocketTextChannel;
                if (ch == null) return;

                var (_, logs, _) = await LicenseApi.Snapshot();
                if (logs.ValueKind != JsonValueKind.Array) return;

                foreach (var l in EnumerateNewestFirst(logs, 30))
                {
                    string id = $"{l.GetProperty("time").GetString()}|{l.GetProperty("key").GetString()}|{l.GetProperty("result").GetString()}";
                    if (!_seenLogs.Add(id)) continue;
                    if (_firstPoll) continue; // don't spam history on startup

                    string result = l.GetProperty("result").GetString();
                    string icon = result.Contains("wrong") || result.Contains("ban") || result.Contains("rate") ? "🚨" : "✅";
                    string client = l.TryGetProperty("client", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : "—";

                    var embed = new Discord.EmbedBuilder()
                        .WithColor(result.Contains("wrong") || result.Contains("ban") || result.Contains("rate")
                            ? new Discord.Color(239, 68, 68)
                            : new Discord.Color(139, 92, 246))
                        .WithTitle($"{icon} DTF activation attempt")
                        .AddField("Result", result, true)
                        .AddField("Client", client, true)
                        .AddField("Key", "`" + l.GetProperty("key").GetString() + "`", true)
                        .AddField("IP", "`" + (l.TryGetProperty("ip", out var ip) ? ip.GetString() : "—") + "`", true)
                        .AddField("HWID", "`" + (l.TryGetProperty("hwid", out var hw) ? hw.GetString() : "—") + "`", true)
                        .AddField("PC", (l.TryGetProperty("pc", out var pc) ? pc.GetString() : "—") + " · " + (l.TryGetProperty("os", out var os) ? os.GetString() : "—"), true)
                        .WithFooter("DTF Security · " + l.GetProperty("time").GetString())
                        .Build();

                    await ch.SendMessageAsync(embed: embed);
                }
                _firstPoll = false;
            }
            catch { }
        }

        private static string Opt(SocketSlashCommand cmd, string name)
            => cmd.Data.Options.FirstOrDefault(o => o.Name == name)?.Value?.ToString();

        private static IEnumerable<JsonElement> EnumerateNewestFirst(JsonElement arr, int max)
        {
            var list = new List<JsonElement>();
            foreach (var x in arr.EnumerateArray()) list.Add(x);
            list.Reverse(); // API returns newest-first already; reverse to oldest-first so alerts post in order
            foreach (var x in list.Take(max)) yield return x;
        }

        // ---------------- showcase embed ----------------

        private static Discord.Embed BuildShowcase()
        {
            const string base_ = "https://dtf-license.onrender.com";
            return new Discord.EmbedBuilder()
                .WithColor(new Discord.Color(124, 58, 237))
                .WithTitle(EditStore.Get("dtf", "title") ?? "DTF")
                .WithUrl(base_)
                .WithThumbnailUrl(EditStore.Get("dtf", "logo") ?? base_ + "/assets/logo.png?v=2")
                .WithDescription(EditStore.Get("dtf", "msg") ??
                    "**Your PC remembers everything. DTF makes it forget.**\n" +
                    "50+ one-click cleaners that wipe every trace on your PC — then cleans its own tracks on exit.\n\n" +
                    "`/features` · full cleaner list\n" +
                    "`/howitworks` · what each cleaner does")
                .AddField("◆ PRICELIST",
                    "• `₱ 400  | 30 days`\n" +
                    "• `₱ 700  | 90 days`\n" +
                    "• `₱ 1200 | Lifetime`", false)
                .AddField("◆ HOW TO BUY",
                    "Click **PURCHASE HERE** → a private ticket opens → pay GCash/Maya → key drops in your ticket.", false)
                .WithImageUrl(EditStore.Get("dtf", "image") ?? base_ + "/assets/banner.png?v=2")
                .Build();
        }

        private static Discord.Embed BuildFeatures()
        {
            return new Discord.EmbedBuilder()
                .WithColor(new Discord.Color(124, 58, 237))
                .WithTitle(EditStore.Get("features", "title") ?? "DTF — FEATURES")
                .WithThumbnailUrl(EditStore.Get("features", "logo") ?? "https://dtf-license.onrender.com/assets/logo.png?v=2")
                .WithDescription(EditStore.Get("features", "msg") ?? "Everything DTF can clean — one click each.")
                .AddField("◆ FEATURES",
                    "```Windows Temp\n" +
                    "Prefetch\n" +
                    "Recent + Jump Lists\n" +
                    "User Assist\n" +
                    "Thumbnail Cache\n" +
                    "Shimcache\n" +
                    "Amcache\n" +
                    "BAM / LastRunTime\n" +
                    "Event Logs\n" +
                    "USB History\n" +
                    "Browser History + Searches\n" +
                    "Cookies + Downloads\n" +
                    "DNS Cache\n" +
                    "Trace Scanner (any app)\n" +
                    "String Cleaner\n" +
                    "Memory Cleaning\n" +
                    "Process Memory Wipe\n" +
                    "Windows Search Index\n" +
                    "USN Journal\n" +
                    "WMI / ETL / SPP GUIDs\n" +
                    "ShellBags / MuiCache\n" +
                    "NVIDIA / DirectX Caches\n" +
                    "Exit Auto-Clean\n" +
                    "Self-Destruct\n" +
                    "Anti-Screenshot```", false)
                .WithImageUrl(EditStore.Get("features", "image") ?? "https://dtf-license.onrender.com/assets/banner.png?v=2")
                .Build();
        }

        private static Discord.Embed BuildHowItWorks()
        {
            return new Discord.EmbedBuilder()
                .WithColor(new Discord.Color(124, 58, 237))
                .WithTitle(EditStore.Get("howitworks", "title") ?? "DTF — HOW IT WORKS")
                .WithThumbnailUrl(EditStore.Get("howitworks", "logo") ?? "https://dtf-license.onrender.com/assets/logo.png?v=2")
                .WithDescription(EditStore.Get("howitworks", "msg") ?? "Every button in the app, explained.")
                .AddField("🧹 Core Cleanup",
                    "The everyday stuff that slows your PC and leaves trails.\n" +
                    "• **Windows Temp** — today's temp files only, old ones untouched\n" +
                    "• **Prefetch** — app launch traces (forensic #1)\n" +
                    "• **Recent + Jump Lists** — files you've opened\n" +
                    "• **User Assist** — every app you've ever run, from Registry\n" +
                    "• **Thumbnail Cache** — images you've viewed", false)
                .AddField("🕵️ Forensic Artifacts",
                    "The exact sources tools like OSForensics parse.\n" +
                    "• **Shimcache** — program execution evidence (AppCompatCache)\n" +
                    "• **Amcache** — installed & executed program inventory\n" +
                    "• **BAM / LastRunTime** — last-run times of every app\n" +
                    "• **Event Logs** — every Windows log, full sweep\n" +
                    "• **USB history** — every device ever plugged, incl. setupapi logs", false)
                .AddField("🌐 Browser Traces",
                    "History and searches — logins stay safe.\n" +
                    "• **History + downloads + searches** — Chrome, Edge, Brave, Opera, Firefox\n" +
                    "• **Cookies** — tracking cookies cleared\n" +
                    "• **Saved passwords & logins kept** — never touched", false)
                .AddField("🔎 Trace Scanner",
                    "Type any name — \"discord\", a game, anything — and DTF finds every leftover file, registry entry and log it left behind. Then remove all of it in one click.", false)
                .AddField("🧠 Memory Tools",
                    "RAM holds recent activity even after files are deleted.\n" +
                    "• **Memory cleaning** — flush caches (standby, shim, font)\n" +
                    "• **Process memory wipe** — targeted cleaning of running apps\n" +
                    "• **Working-set trim** — frees RAM without closing anything", false)
                .AddField("⚙️ Advanced",
                    "For deep cleans.\n" +
                    "• **Windows Search index** — what you searched, everywhere\n" +
                    "• **USN Journal** — the drive's file-activity diary\n" +
                    "• **WMI logs, ETL traces, SPP GUIDs**\n" +
                    "• **ShellBags / RegSeeker / MuiCache** — folder & app trails\n" +
                    "• **NVIDIA / DirectX caches**", false)
                .AddField("🛡️ Built-in Privacy",
                    "DTF cleans other apps' tracks — and its own.\n" +
                    "• **Exit Auto-Clean** — wipes its own run-traces on close\n" +
                    "• **Self-Destruct** — one button deletes DTF completely\n" +
                    "• **Anti-Screenshot** — invisible to screen captures", false)
                .WithImageUrl(EditStore.Get("howitworks", "image") ?? "https://dtf-license.onrender.com/assets/banner.png?v=2")
                .Build();
        }

        private static Discord.MessageComponent BuildComponents()
        {
            return new Discord.ComponentBuilder()
                .WithButton("🛒 PURCHASE HERE", "dtf_ticket_direct", Discord.ButtonStyle.Success)
                .Build();
        }

        // ---------------- slash commands ----------------

        private static async Task OnSlash(SocketSlashCommand cmd)
        {
            try
            {
                // admin-only guard for security commands
                bool isAdmin = Cfg.AdminRoleId == 0 || (cmd.User is SocketGuildUser gu && gu.Roles.Any(r => r.Id == Cfg.AdminRoleId));
                string name = cmd.CommandName;

                if (name == "dtf")
                {
                    await cmd.RespondAsync(embed: BuildShowcase(), components: BuildComponents());
                    return;
                }

                if (name == "features")
                {
                    await cmd.RespondAsync(embed: BuildFeatures());
                    return;
                }

                if (name == "howitworks")
                {
                    await cmd.RespondAsync(embed: BuildHowItWorks());
                    return;
                }

                if (!isAdmin)
                {
                    await cmd.RespondAsync("⛔ Admin role required.", ephemeral: true);
                    return;
                }

                switch (name)
                {
                    case "key":
                    {
                        string clientName = Opt(cmd, "name") ?? "client";
                        long days = Convert.ToInt64(Opt(cmd, "days") ?? "30");
                        string hwid = Opt(cmd, "hwid");
                        var (ok, key, err) = await LicenseApi.CreateKey(clientName, (int)Math.Clamp(days, 1, 3650), hwid);
                        if (!ok) { await cmd.RespondAsync($"✗ {err}", ephemeral: true); return; }
                        await cmd.RespondAsync(
                            $"✅ Key for **{clientName}** — {days} day(s)\n`{key}`\n\nSend this in their ticket. Need a HWID change later? Use the 🔑 Keys tab on the site.",
                            ephemeral: true);
                        return;
                    }

                    case "keys":
                    {
                        var (keys, _, _) = await LicenseApi.Snapshot();
                        var sb = new StringBuilder();
                        int i = 0;
                        foreach (var k in keys.EnumerateArray().Take(15))
                        {
                            i++;
                            string kk = k.GetProperty("key").GetString();
                            string shortKey = kk.Length > 19 ? kk.Substring(0, 16) + "…" : kk;
                            string who = k.TryGetProperty("discord", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : "—";
                            string exp = k.TryGetProperty("expires", out var ex) && ex.ValueKind == JsonValueKind.String ? ex.GetString() : "never";
                            bool banned = k.TryGetProperty("banned", out var b) && b.GetBoolean();
                            sb.AppendLine($"**{i}.** `{shortKey}` · {who} · exp {exp}{(banned ? " · 🚫BANNED" : "")}");
                        }
                        var embed = new Discord.EmbedBuilder()
                            .WithColor(new Discord.Color(139, 92, 246))
                            .WithTitle($"🔑 Keys ({keys.GetArrayLength()} total)")
                            .WithDescription(sb.ToString())
                            .Build();
                        await cmd.RespondAsync(embed: embed, ephemeral: true);
                        return;
                    }

                    case "log":
                    {
                        var (_, logs, _) = await LicenseApi.Snapshot();
                        var sb = new StringBuilder();
                        int i = 0;
                        foreach (var l in logs.EnumerateArray().Take(15))
                        {
                            i++;
                            string result = l.GetProperty("result").GetString();
                            string client = l.TryGetProperty("client", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : "—";
                            sb.AppendLine($"**{i}.** {l.GetProperty("time").GetString()} · {result} · {client} · `{l.GetProperty("key").GetString()}`");
                        }
                        var embed = new Discord.EmbedBuilder()
                            .WithColor(new Discord.Color(139, 92, 246))
                            .WithTitle("👁 Last 15 access attempts")
                            .WithDescription(sb.Length > 0 ? sb.ToString() : "no attempts yet")
                            .Build();
                        await cmd.RespondAsync(embed: embed, ephemeral: true);
                        return;
                    }

                    case "ban":
                    case "unban":
                    {
                        string key = Opt(cmd, "key") ?? "";
                        var r = name == "ban" ? await LicenseApi.Ban(key) : await LicenseApi.Unban(key);
                        await cmd.RespondAsync(name == "ban" ? $"🚫 Banned `{key}`" : $"✅ Unbanned `{key}`", ephemeral: true);
                        return;
                    }

                    case "reset":
                    {
                        string key = Opt(cmd, "key") ?? "";
                        await LicenseApi.ResetHwid(key);
                        await cmd.RespondAsync($"♻️ HWID reset for `{key}` — client can activate on a new PC now.", ephemeral: true);
                        return;
                    }

                    case "edit":
                    {
                        string editTarget = Opt(cmd, "command") ?? "dtf";
                        await SendEditor(cmd, editTarget);
                        return;
                    }

                    case "message":
                    {
                        if (!IsAdminUser(cmd)) { await cmd.RespondAsync("\u26D4 Admin role required.", ephemeral: true); return; }
                        if (!_drafts.ContainsKey(cmd.User.Id)) _drafts[cmd.User.Id] = new MsgDraft();
                        await cmd.RespondAsync(embed: BuildMsgBuilder(_drafts[cmd.User.Id]), components: MsgBuilderButtons(), ephemeral: true);
                        return;
                    }

                    case "ticketpanel":
                    {
                        if (!IsAdminUser(cmd)) { await cmd.RespondAsync("\u26D4 Admin role required.", ephemeral: true); return; }
                        await cmd.DeferAsync(ephemeral: true);
                        var panel = await cmd.Channel.SendMessageAsync(embed: BuildTicketPanel(), components: TicketPanelButtons());
                        try { await panel.PinAsync(); } catch { }
                        await cmd.FollowupAsync("\uD83C\uDFAB Ticket panel posted and pinned in this channel.", ephemeral: true);
                        return;
                    }

                    case "vouch":
                    {
                        await cmd.DeferAsync();
                        var msg = await cmd.Channel.SendMessageAsync(embed: BuildVouch());
                        try { await msg.PinAsync(); } catch { }
                        await cmd.FollowupAsync("\uD83D\uDCCC Vouch session posted and pinned.", ephemeral: true);
                        return;
                    }

                    case "welcometest":
                    {
                        await cmd.RespondAsync(embed: BuildWelcome(cmd.User as SocketGuildUser ?? _client.GetGuild(Cfg.GuildId)?.CurrentUser as SocketGuildUser), ephemeral: true);
                        return;
                    }

                    case "stats":
                    {
                        var (keys, logs, reqs) = await LicenseApi.Snapshot();
                        int total = keys.GetArrayLength();
                        int active = 0, banned = 0;
                        foreach (var k in keys.EnumerateArray())
                        {
                            bool isBanned = k.TryGetProperty("banned", out var b) && b.GetBoolean();
                            if (isBanned) banned++;
                            else if (k.TryGetProperty("hwid", out var h) && h.ValueKind == JsonValueKind.String) active++;
                        }
                        var embed = new Discord.EmbedBuilder()
                            .WithColor(new Discord.Color(34, 197, 94))
                            .WithTitle("📊 DTF Stats")
                            .AddField("Keys", total.ToString(), true)
                            .AddField("Active", active.ToString(), true)
                            .AddField("Banned", banned.ToString(), true)
                            .AddField("Open requests", reqs.ValueKind == JsonValueKind.Array ? reqs.GetArrayLength().ToString() : "0", true)
                            .AddField("Access attempts", logs.ValueKind == JsonValueKind.Array ? logs.GetArrayLength().ToString() : "0", true)
                            .Build();
                        await cmd.RespondAsync(embed: embed, ephemeral: true);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                try { await cmd.RespondAsync($"✗ error: {ex.Message}", ephemeral: true); } catch { }
            }
        }

        // ---------------- buttons ----------------

        private static async Task OnButton(SocketMessageComponent cmd)
        {
            string cid = cmd.Data.CustomId ?? "";

            // ----- /message builder buttons: msgbuilder_<action> -----
            if (cid.StartsWith("msgbuilder_"))
            {
                if (!IsAdminUser(cmd)) { await cmd.RespondAsync("\u26D4 Admin role required.", ephemeral: true); return; }
                if (!_drafts.TryGetValue(cmd.User.Id, out var d)) d = _drafts[cmd.User.Id] = new MsgDraft();

                switch (cid.Substring(11))
                {
                    case "title":
                    case "body":
                    case "image":
                    {
                        string label = cid.EndsWith("title") ? "Title" : cid.EndsWith("image") ? "Image URL (https://... direct link to png/jpg)" : "Message text (Discord markdown allowed)";
                        var modal = new Discord.ModalBuilder()
                            .WithCustomId("msgmodal_" + cid.Substring(11))
                            .WithTitle("Message Builder")
                            .AddTextInput(label, "msgmodal_" + cid.Substring(11),
                                cid.EndsWith("body") ? Discord.TextInputStyle.Paragraph : Discord.TextInputStyle.Short,
                                null, null, cid.EndsWith("body") ? 2000 : 800, false);
                        await cmd.RespondWithModalAsync(modal.Build());
                        return;
                    }
                    case "preview":
                        await cmd.RespondAsync(embed: BuildDraftPreview(d), ephemeral: true);
                        return;
                    case "post":
                        await cmd.DeferAsync(ephemeral: true);
                        var posted = await cmd.Channel.SendMessageAsync(embed: BuildDraftPreview(d));
                        try { await posted.PinAsync(); } catch { }
                        _drafts.Remove(cmd.User.Id);
                        await cmd.FollowupAsync("\uD83D\uDCCC Posted and pinned in this channel.", ephemeral: true);
                        return;
                    case "reset":
                        _drafts[cmd.User.Id] = new MsgDraft();
                        await cmd.UpdateAsync(m => { m.Embed = BuildMsgBuilder(_drafts[cmd.User.Id]); m.Components = MsgBuilderButtons(); });
                        return;
                    case "cancel":
                        _drafts.Remove(cmd.User.Id);
                        await cmd.RespondAsync("\uD83D\uDDED Builder closed — draft discarded.", ephemeral: true);
                        return;
                }
            }

            // ----- editor buttons (edit_<part>:<target> and editmodal routing) -----
            if (cid.StartsWith("edit_") && cid.Contains(':'))
            {
                var seg = cid.Split(':');
                string part = seg[0].Substring(5);   // after "edit_"
                string target = seg.Length > 1 ? seg[1] : "dtf";

                if (part == "preview")
                {
                    Discord.Embed preview = target switch
                    {
                        "features" => BuildFeatures(),
                        "howitworks" => BuildHowItWorks(),
                        "ticketpanel" => BuildTicketPanel(),
                        _ => BuildShowcase()
                    };
                    await cmd.RespondAsync(embed: preview, ephemeral: true);
                    return;
                }
                if (part == "reset")
                {
                    foreach (var p in new[] { "msg", "title", "image", "logo", "footer" }) EditStore.Set(target, p, null);
                    await cmd.UpdateAsync(m => { m.Embed = BuildEditorEmbed(target); m.Components = EditorButtons(target); });
                    return;
                }

                string current = EditStore.Get(target, part) ?? "";
                string label = part == "msg" ? "Message text (Discord markdown allowed)" : char.ToUpper(part[0]) + part.Substring(1);
                var modal = new Discord.ModalBuilder()
                    .WithCustomId($"editmodal_{part}:{target}")
                    .WithTitle("Edit " + target)
                    .AddTextInput(label, $"editmodal_{part}:{target}",
                        part == "msg" ? Discord.TextInputStyle.Paragraph : Discord.TextInputStyle.Short,
                        null, null, part == "msg" ? 2000 : 1000, true);
                await cmd.RespondWithModalAsync(modal.Build());
                return;
            }

            if (cid == "dtf_ticket_direct")
            {
                await CreateTicketAsync(cmd);
            }
            else if (cmd.Data.CustomId == "dtf_site")
            {
                await cmd.RespondAsync("🌐 **DTF showcase:** https://dtf-license.onrender.com", ephemeral: true);
            }
            else if (cmd.Data.CustomId == "dtf_ticket_close")
            {
                await CloseTicketAsync(cmd);
            }
            else if (cmd.Data.CustomId == "dtf_how")
            {
                await cmd.RespondAsync(
                    "**How DTF works**\n1️⃣ Purchase → get a key in your ticket\n2️⃣ Run DTF → paste your key → it activates to your PC\n3️⃣ Clean everything with one click\n\nEvery key is locked to one PC. Need to move PCs? Ask staff for a HWID reset.",
                    ephemeral: true);
            }
        }

        // ---------------- /message custom announcement builder ----------------

        private class MsgDraft
        {
            public string Title = "";
            public string Body = "";
            public string Image = "";
        }

        private static readonly Dictionary<ulong, MsgDraft> _drafts = new();

        private static Discord.Embed BuildMsgBuilder(MsgDraft d)
        {
            var eb = new Discord.EmbedBuilder()
                .WithColor(new Discord.Color(124, 58, 237))
                .WithTitle("\uD83D\uDCDD Message Builder")
                .WithDescription(
                    "Create a custom announcement, preview it, then **Post + Pin** it in any channel." +
                    "\n\u2003\u2003\u2003*This panel is only visible to you.*")
                .AddField("Title", string.IsNullOrWhiteSpace(d.Title) ? "*(not set)*" : "```" + Trunc(d.Title, 200) + "```", false)
                .AddField("Message", string.IsNullOrWhiteSpace(d.Body) ? "*(not set)*" : "```" + Trunc(d.Body, 900) + "```", false)
                .AddField("Image", string.IsNullOrWhiteSpace(d.Image) ? "*(none)*" : "```" + Trunc(d.Image, 200) + "```", false);
            return eb.Build();
        }

        private static Discord.MessageComponent MsgBuilderButtons()
        {
            return new Discord.ComponentBuilder()
                .WithButton("Title", "msgbuilder_title", Discord.ButtonStyle.Primary)
                .WithButton("\uD83D\uDCDD Message", "msgbuilder_body", Discord.ButtonStyle.Primary)
                .WithButton("\uD83D\uDCF7 Image URL", "msgbuilder_image", Discord.ButtonStyle.Secondary)
                .WithButton("\uD83D\uDC41 Preview", "msgbuilder_preview", Discord.ButtonStyle.Success)
                .WithButton("\uD83D\uDCCC Post + Pin", "msgbuilder_post", Discord.ButtonStyle.Danger)
                .WithButton("\uD83D\uDD01 Reset", "msgbuilder_reset", Discord.ButtonStyle.Secondary)
                .WithButton("\u274C Cancel", "msgbuilder_cancel", Discord.ButtonStyle.Secondary)
                .Build();
        }

        private static Discord.Embed BuildDraftPreview(MsgDraft d)
        {
            var eb = new Discord.EmbedBuilder().WithColor(new Discord.Color(139, 92, 246));
            if (!string.IsNullOrWhiteSpace(d.Title)) eb.WithTitle(d.Title);
            if (!string.IsNullOrWhiteSpace(d.Body)) eb.WithDescription(d.Body);
            if (!string.IsNullOrWhiteSpace(d.Image)) eb.WithImageUrl(d.Image);
            if (string.IsNullOrWhiteSpace(d.Title) && string.IsNullOrWhiteSpace(d.Body) && string.IsNullOrWhiteSpace(d.Image))
                eb.WithDescription("*(empty draft — set a title or message first)*");
            return eb.Build();
        }

        // ---------------- /edit message editor ----------------

        private static readonly string[] _editableCommands = { "dtf", "features", "howitworks" };

        private static bool IsAdminUser(SocketSlashCommand cmd)
            => Cfg.AdminRoleId == 0 || (cmd.User is SocketGuildUser gu && gu.Roles.Any(r => r.Id == Cfg.AdminRoleId));

        private static bool IsAdminUser(Discord.WebSocket.SocketMessageComponent cmd)
            => Cfg.AdminRoleId == 0 || (cmd.User is SocketGuildUser gu && gu.Roles.Any(r => r.Id == Cfg.AdminRoleId));

        private static Discord.Embed BuildEditorEmbed(string target)
        {
            string msg = EditStore.Get(target, "msg");
            string title = EditStore.Get(target, "title");
            string image = EditStore.Get(target, "image");
            string logo = EditStore.Get(target, "logo");
            string footer = EditStore.Get(target, "footer");

            var eb = new Discord.EmbedBuilder()
                .WithColor(new Discord.Color(124, 58, 237))
                .WithTitle($"\u270F\uFE0F Editor — /{target}")
                .WithDescription("Current content. Use the buttons below to change it.")
                .AddField("Title", string.IsNullOrWhiteSpace(title) ? "*(default)*" : "```" + title + "```", false)
                .AddField("Message", string.IsNullOrWhiteSpace(msg) ? "*(default)*" : "```" + Trunc(msg, 900) + "```", false)
                .AddField("Image URL", string.IsNullOrWhiteSpace(image) ? "*(default banner)*" : "```" + Trunc(image, 200) + "```", true)
                .AddField("Logo URL", string.IsNullOrWhiteSpace(logo) ? "*(default logo)*" : "```" + Trunc(logo, 200) + "```", true);
            if (!string.IsNullOrWhiteSpace(footer)) eb.AddField("Footer", "```" + footer + "```", false);
            return eb.Build();
        }

        private static string Trunc(string s, int n) => s.Length > n ? s.Substring(0, n - 3) + "..." : s;

        private static Discord.MessageComponent EditorButtons(string target)
        {
            return new Discord.ComponentBuilder()
                .WithButton("\uD83D\uDCDD Message", "edit_msg:" + target, Discord.ButtonStyle.Primary)
                .WithButton("Title", "edit_title:" + target, Discord.ButtonStyle.Secondary)
                .WithButton("\uD83D\uDCF7 Image", "edit_image:" + target, Discord.ButtonStyle.Secondary)
                .WithButton("Logo", "edit_logo:" + target, Discord.ButtonStyle.Secondary)
                .WithButton("Footer", "edit_footer:" + target, Discord.ButtonStyle.Secondary)
                .WithButton("\uD83D\uDC41 Preview", "edit_preview:" + target, Discord.ButtonStyle.Success)
                .WithButton("\u21A9 Reset", "edit_reset:" + target, Discord.ButtonStyle.Danger)
                .Build();
        }

        private static async Task SendEditor(SocketSlashCommand cmd, string target)
        {
            if (!IsAdminUser(cmd)) { await cmd.RespondAsync("\u26D4 Admin role required.", ephemeral: true); return; }
            await cmd.RespondAsync(embed: BuildEditorEmbed(target), components: EditorButtons(target), ephemeral: true);
        }

        private static async Task OnModal(SocketModal modal)
        {
            try
            {
                string value = modal.Data.Components.FirstOrDefault()?.Value;

                // ----- /message builder modals: msgmodal_<part> -----
                if (modal.Data.CustomId.StartsWith("msgmodal_"))
                {
                    if (!_drafts.TryGetValue(modal.User.Id, out var d)) d = _drafts[modal.User.Id] = new MsgDraft();
                    switch (modal.Data.CustomId.Substring(9))
                    {
                        case "title": d.Title = value ?? ""; break;
                        case "body": d.Body = value ?? ""; break;
                        case "image": d.Image = value ?? ""; break;
                    }
                    await modal.RespondAsync(embed: BuildMsgBuilder(d), components: MsgBuilderButtons(), ephemeral: true);
                    return;
                }

                // ----- /edit editor modals: editmodal_<part>:<target> -----
                var parts = modal.Data.CustomId.Split(':');
                string target = parts.Length > 1 ? parts[1] : "dtf";
                string part = modal.Data.CustomId.StartsWith("editmodal_") ? modal.Data.CustomId.Substring(10).Split(':')[0] : "";

                EditStore.Set(target, part, value);
                await modal.RespondAsync(
                    embed: BuildEditorEmbed(target),
                    components: EditorButtons(target),
                    ephemeral: true);
            }
            catch (Exception ex)
            {
                try { await modal.RespondAsync($"\u2717 {ex.Message}", ephemeral: true); } catch { }
            }
        }

        // ---------------- ticket panel (Ticket-Tool style) ----------------

        private static Discord.Embed BuildTicketPanel()
        {
            return new Discord.EmbedBuilder()
                .WithColor(new Discord.Color(124, 58, 237))
                .WithTitle(EditStore.Get("ticketpanel", "title") ?? "MENU")
                .WithDescription(EditStore.Get("ticketpanel", "msg") ??
                    "To create a ticket use the Create ticket button below.")
                .WithThumbnailUrl(EditStore.Get("ticketpanel", "logo") ?? "https://dtf-license.onrender.com/assets/logo.png?v=2")
                .WithImageUrl(string.IsNullOrWhiteSpace(EditStore.Get("ticketpanel", "image")) ? null : EditStore.Get("ticketpanel", "image"))
                .WithFooter(string.IsNullOrWhiteSpace(EditStore.Get("ticketpanel", "footer")) ? null : EditStore.Get("ticketpanel", "footer"))
                .Build();
        }

        private static Discord.MessageComponent TicketPanelButtons()
        {
            return new Discord.ComponentBuilder()
                .WithButton("\uD83C\uDFAB Create ticket", "dtf_ticket_direct", Discord.ButtonStyle.Primary)
                .Build();
        }

        private static async Task CreateTicketAsync(SocketMessageComponent cmd)
        {
            try
            {
                var guild = _client.GetGuild(Cfg.GuildId);
                if (guild == null) { await cmd.RespondAsync("⛔ Bot is not connected to the server.", ephemeral: true); return; }

                // discord sanitizes channel names (lowercase, strips odd chars) — do it ourselves so dedupe matches
                var safeName = new string(cmd.User.Username.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
                if (safeName.Length == 0) safeName = "user";
                if (safeName.Length > 80) safeName = safeName.Substring(0, 80);
                string ticketName = $"ticket-{safeName}";

                // one open ticket per user
                var existing = guild.TextChannels.FirstOrDefault(c => c.Name == ticketName);
                if (existing != null)
                {
                    await cmd.RespondAsync($"You already have an open ticket: {existing.Mention}", ephemeral: true);
                    return;
                }

                await cmd.DeferAsync(ephemeral: true);

                // private channel: only buyer + admin role + bot can see it
                var open = new Discord.OverwritePermissions(
                    viewChannel: Discord.PermValue.Allow,
                    sendMessages: Discord.PermValue.Allow,
                    readMessageHistory: Discord.PermValue.Allow);

                var overwrites = new List<Discord.Overwrite>
                {
                    new Discord.Overwrite(guild.EveryoneRole.Id, Discord.PermissionTarget.Role,
                        new Discord.OverwritePermissions(viewChannel: Discord.PermValue.Deny)),
                    new Discord.Overwrite(cmd.User.Id, Discord.PermissionTarget.User, open),
                };
                if (Cfg.AdminRoleId != 0)
                    overwrites.Add(new Discord.Overwrite(Cfg.AdminRoleId, Discord.PermissionTarget.Role, open));
                // bot runs with Administrator permission — it can see every channel; no overwrite needed

                var category = guild.CategoryChannels.FirstOrDefault(c => c.Name.ToLower().Contains("ticket"));
                var channel = await guild.CreateTextChannelAsync(ticketName, p =>
                {
                    if (category != null) p.CategoryId = category.Id;
                    p.PermissionOverwrites = overwrites;
                });

                var welcome = new Discord.EmbedBuilder()
                    .WithColor(new Discord.Color(139, 92, 246))
                    .WithTitle($"🎫 Ticket — {cmd.User.Username}")
                    .WithDescription(
                        $"Welcome {cmd.User.Mention}!\n\n" +
                        "**How to buy DTF:**\n" +
                        "1️⃣ Tell us which plan you want:\n" +
                        "• `₱400` — 30 days\n• `₱700` — 90 days\n• `₱1200` — Lifetime\n\n" +
                        "2️⃣ Send your payment (GCash / Maya) and screenshot the receipt here.\n" +
                        "3️⃣ Staff verifies and drops your key in this ticket.\n\n" +
                        "Your key activates on ONE PC. Keep it private!")
                    .Build();

                await channel.SendMessageAsync(
                    embed: welcome,
                    components: new Discord.ComponentBuilder()
                        .WithButton("🔒 Close ticket", "dtf_ticket_close", Discord.ButtonStyle.Danger)
                        .Build());

                await cmd.FollowupAsync($"✅ Ticket created: {channel.Mention}", ephemeral: true);
            }
            catch (Exception ex)
            {
                try { await cmd.RespondAsync($"✗ Could not create ticket: {ex.Message}", ephemeral: true); } catch { }
            }
        }

        private static async Task CloseTicketAsync(SocketMessageComponent cmd)
        {
            try
            {
                var ch = cmd.Channel as SocketTextChannel;
                if (ch == null || !ch.Name.StartsWith("ticket-")) return;

                bool isOwner = ch.Name == $"ticket-{cmd.User.Username}";
                bool isAdmin = Cfg.AdminRoleId != 0 && cmd.User is SocketGuildUser gu && gu.Roles.Any(r => r.Id == Cfg.AdminRoleId);
                if (!isOwner && !isAdmin)
                {
                    await cmd.RespondAsync("⛔ Only the ticket owner or staff can close this.", ephemeral: true);
                    return;
                }

                await cmd.RespondAsync($"🔒 Closing ticket... deleted in 10s.");
                await Task.Delay(10_000);
                await ch.DeleteAsync();
            }
            catch { }
        }
    }
}
