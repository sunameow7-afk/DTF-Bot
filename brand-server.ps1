# DTF server branding — one-shot via Discord API using the bot token from config.json
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Text.Encoding 2>$null

$root = $PSScriptRoot
$cfg = Get-Content (Join-Path $root 'config.json') -Raw | ConvertFrom-Json
$token = $cfg.token
$guild = [string]$cfg.guildId

$h = @{ Authorization = "Bot $token" }
$base = 'https://discord.com/api/v10'
$PURPLE = 8141549     # 0x7C3AED
$LILAC  = 9133302     # 0x8B5CF6
$GOLD   = 15840287    # 0xF1C40F

function Api($method, $path, $body) {
    $json = if ($null -ne $body) { $body | ConvertTo-Json -Depth 8 } else { $null }
    for ($try = 1; $try -le 4; $try++) {
        try {
            return Invoke-RestMethod -Uri ($base + $path) -Headers $h -Method $method -ContentType 'application/json; charset=utf-8' -Body $json
        } catch {
            if ($try -eq 4) { throw }
            Write-Output ("  retry $try after error " + $_.Exception.Response.StatusCode.value__ + '...')
            Start-Sleep -Seconds (2 * $try)
        }
    }
}
function DataUri($p) { 'data:image/png;base64,' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($p)) }
function E([int]$cp) { [string][char]::ConvertFromUtf32($cp) }
$assets = 'C:/Users/Echague/Documents/coding/DTF-License/wwwroot/assets'

# ---------- 1. server icon + description (free, no boost) ----------
try {
    Api PATCH "/guilds/$guild" @{
        icon = (DataUri "$assets/logo.png")
        description = 'DTF. Wipe forensic traces, browser history, memory. Buy in a ticket - dtf-license.onrender.com'
    } | Out-Null
    Write-Output 'server icon + description: OK'
} catch { Write-Output ("server icon FAILED: " + $_.ErrorDetails.Message) }

# ---------- 2. server banner (needs Boost Level 2) ----------
try {
    Api PATCH "/guilds/$guild" @{ banner = (DataUri "$assets/banner.png") } | Out-Null
    Write-Output 'server banner: OK (boost unlocked)'
} catch { Write-Output ("server banner: needs Boost Level 2 - " + ($_.ErrorDetails.Message | Out-String).Substring(0, [Math]::Min(120, ($_.ErrorDetails.Message | Out-String).Length))) }

# ---------- 3. bot profile: avatar + banner (free for bots) ----------
try { Api PATCH '/users/@me' @{ avatar = (DataUri "$assets/logo.png") } | Out-Null; Write-Output 'bot avatar: OK' } catch { Write-Output ("bot avatar FAILED: " + $_.ErrorDetails.Message) }
try { Api PATCH '/users/@me' @{ banner = (DataUri "$assets/banner.png") } | Out-Null; Write-Output 'bot profile banner: OK' } catch { Write-Output ("bot banner FAILED: " + $_.ErrorDetails.Message) }

# ---------- 4. ensure channels ----------
$channels = @()
try { $channels = Api GET "/guilds/$guild/channels" } catch { Write-Output "channels list FAILED (will retry sections individually)" }
function EnsureChannel($name, $topic) {
    $existing = $script:channels | Where-Object { $_.name -eq $name -and $_.type -eq 0 }
    if ($existing) { Write-Output "channel #$name : exists"; return [string]$existing[0].id }
    $c = Api POST "/guilds/$guild/channels" @{ name = $name; type = 0; topic = $topic }
    Write-Output "channel #$name : created"
    return [string]$c.id
}
$rulesId = EnsureChannel 'rules'          'Server rules - read before posting'
$dlId    = EnsureChannel 'downloads'      'Latest DTF build + update info'
$supId   = EnsureChannel 'support'        'Questions and help (purchases go in tickets)'
Start-Sleep -Milliseconds 300

# ---------- 5. ensure roles ----------
$roles = @()
try { $roles = Api GET "/guilds/$guild/roles" } catch { Write-Output "roles list FAILED" }
function EnsureRole($name, $colorDec, $hoist) {
    $existing = $script:roles | Where-Object { $_.name -eq $name }
    if ($existing) { Write-Output "role $name : exists"; return }
    Api POST "/guilds/$guild/roles" @{ name = $name; color = $colorDec; hoist = $hoist; mentionable = $true } | Out-Null
    Write-Output "role $name : created"
}
EnsureRole 'DTF Owner'  $GOLD   $true
EnsureRole 'DTF Admin'  $PURPLE $true
EnsureRole 'DTF Client' $LILAC  $true
Start-Sleep -Milliseconds 300

# ---------- 6. welcome screen ----------
try {
    Api PATCH "/guilds/$guild/welcome-screen" @{
        enabled = $true
        description = 'Your PC remembers everything. DTF makes it forget. Read the rules, grab the build, buy in a ticket.'
        welcome_channels = @(
            @{ channel_id = $rulesId; description = 'Read before posting';          emoji_name = (E 0x1F4DC) },
            @{ channel_id = $dlId;    description = 'Latest DTF build';             emoji_name = (E 0x2B07)  },
            @{ channel_id = $supId;   description = 'Help and questions';           emoji_name = (E 0x1F4AC) }
        )
    } | Out-Null
    Write-Output 'welcome screen: OK'
} catch { Write-Output ("welcome screen FAILED: " + $_.ErrorDetails.Message) }

# ---------- 7. pinned rules embed ----------
$rulesText = (
    '1. No key sharing or reselling - keys are locked to one PC. Sharing = permanent ban, blacklisted HWID, no refund.' + "`n" +
    '2. No fake payment receipts - scamming = instant ban, no appeals.' + "`n" +
    '3. Purchases and support in tickets only - click the Create ticket button in the ticket channel and a private ticket opens. Do not DM staff.' + "`n" +
    '4. No spam, no begging for free keys.' + "`n" +
    '5. One PC per key - new PC? Open a ticket for a HWID reset.' + "`n" +
    '6. Chargebacks = permanent blacklist.' + "`n" +
    '7. Discord ToS applies.'
)
$rolesText = (
    (E 0xF1C40F) + ' **DTF Owner** - that is the boss.' + "`n" +
    (E 0x1F7E3)  + ' **DTF Admin** - staff, runs tickets and keys.' + "`n" +
    (E 0x1F49C)  + ' **DTF Client** - you, after buying. Shows you bought access.'
)
$rulesBody = @{
    embeds = @(@{
        title = 'DTF SERVER RULES'
        color = $PURPLE
        description = 'Keep it clean. One strike for scams, sharing keys or chargebacks.'
        fields = @(
            @{ name = 'The Rules'; value = $rulesText; inline = $false },
            @{ name = 'Roles'; value = $rolesText; inline = $false }
        )
        footer = @{ text = 'DTF - Deep Trace Fix - dtf-license.onrender.com' }
    })
}
try {
    $old = Api GET "/channels/$rulesId/messages"
    $mine = $old | Where-Object { $_.author.bot -and $_.embeds.Count -gt 0 -and $_.embeds[0].title -eq 'DTF SERVER RULES' }
    if (-not $mine) {
        $msg = Api POST "/channels/$rulesId/messages" $rulesBody
        Api PUT "/channels/$rulesId/pins/$($msg.id)" $null | Out-Null
        Write-Output 'rules embed: posted + pinned'
    } else { Write-Output 'rules embed: already there' }
} catch { Write-Output ("rules embed FAILED: " + $_.ErrorDetails.Message) }

# ---------- 8. downloads embed ----------
$dlBody = @{
    embeds = @(@{
        title = 'HOW TO GET DTF'
        color = $LILAC
        description = '1. Buy in a ticket - click the Create ticket button in the ticket channel' + "`n" +
            '2. Pay GCash / Maya, send the receipt in your ticket' + "`n" +
            '3. Staff drops your key + the DTF build in the ticket' + "`n" +
            '4. Paste the key in DTF - it binds to your PC' + "`n`n" +
            '**Updates:** DTF updates itself on launch - when a new build ships you get an UPDATE banner, one click and you are current. Never download DTF from anywhere else.'
        footer = @{ text = 'dtf-license.onrender.com' }
    })
}
try {
    $old = Api GET "/channels/$dlId/messages"
    $mine = $old | Where-Object { $_.author.bot -and $_.embeds.Count -gt 0 -and $_.embeds[0].title -eq 'HOW TO GET DTF' }
    if (-not $mine) { Api POST "/channels/$dlId/messages" $dlBody | Out-Null; Write-Output 'downloads embed: posted' }
    else { Write-Output 'downloads embed: already there' }
} catch { Write-Output ("downloads embed FAILED: " + $_.ErrorDetails.Message) }

Write-Output '--- branding done ---'
