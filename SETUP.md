# DTF Bot — Discord showcase + security bot

One bot that does two jobs:

**Showcase** — `/dtf` posts a purple embed like the WTP SHOP example: features, pricelist, PURCHASE button.

**Security** — live alerts in a private channel every time someone tries to activate DTF, plus slash commands to manage keys from Discord (no website needed).

---

## 1. Create the bot (5 min)

1. Go to **discord.com/developers/applications** → **New Application** → name it `DTF Bot`
2. Left menu **Bot** → **Reset Token** → copy the token (you'll paste it in config.json)
3. Same page, scroll to **Privileged Gateway Intents** → leave both OFF (this bot doesn't need them)
4. Left menu **OAuth2 → URL Generator**:
   - Scopes: ✅ `bot` + ✅ `applications.commands`
   - Bot permissions: ✅ Send Messages, ✅ Embed Links, ✅ Use Slash Commands (≈ 549755880448 if it asks for a number)
5. Copy the generated URL at the bottom → open it in your browser → pick **your DTF server** → authorize

## 2. Get the 3 IDs (2 min)

In Discord: **Settings → Advanced → Developer Mode → ON**. Then right-click:

| What | How to get it |
|---|---|
| **Server ID** (`guildId`) | right-click your server name → Copy Server ID |
| **Security channel** (`securityChannelId`) | right-click a **private staff-only** channel → Copy Channel ID |
| **Admin role** (`adminRoleId`) | right-click your admin role → Copy Role ID |

## 3. Configure + run

Create `config.json` **next to DTF-Bot.exe** (same folder):

```json
{
  "token": "YOUR_BOT_TOKEN",
  "guildId": "YOUR_SERVER_ID",
  "securityChannelId": "YOUR_PRIVATE_CHANNEL_ID",
  "adminRoleId": "YOUR_ADMIN_ROLE_ID",
  "licenseServer": "http://localhost:5080",
  "adminPassword": "Spring$24"
}
```

Run **START-BOT.bat** (or double-click DTF-Bot.exe). Console says `[bot] connected` + `slash commands registered` → done.

> `licenseServer` must point at the machine running the license server.
> Local testing: keep localhost + have START-SERVER.bat running.
> Later when deployed to Render: change it to `https://dtf-license-xxxx.onrender.com` — then the bot works even when your PC is off.

## 4. Use it

**Everyone:**
- `/dtf` — posts the showcase embed with features, pricelist and PURCHASE button

**You + admins (only visible to the caller):**
- `/key name:juan days:30` — generates a key instantly → paste in ticket
- `/key name:juan days:90 hwid:6956-FDF8-1BA1-D821` — pre-bound to their PC
- `/keys` — first 15 keys with owner, expiry, banned state
- `/log` — last 15 activation attempts
- `/ban key:DTF-XXXX-...` / `/unban` — kill a leaked key on the spot
- `/reset key:DTF-XXXX-...` — client changed PC, unlock their HWID
- `/stats` — keys / active / banned / open requests

**Automatic:** every activation attempt (success, wrong-key, wrong-pc, banned, rate-limited) is posted as a color-coded alert in your security channel within ~15 seconds — purple = success, red = threat. You'll see key-sharers and crackers in real time.

## Notes

- The bot needs the **license server running** to answer. Local: start START-SERVER.bat first.
- To run 24/7 without your PC: deploy the license server to Render (see ../DTF-License/DEPLOY.md), set `licenseServer` to the Render URL, and host the bot free on a service like Render background worker or your own VPS.
- Security channel MUST be private (staff only) — it contains IPs and HWIDs.
