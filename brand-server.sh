#!/bin/bash
# DTF server branding via curl (PowerShell chokes on some endpoints with 40333)
# Token is read from config.json - never hardcoded
TOKEN=$(powershell -NoProfile -Command "(Get-Content \"$PSScriptRoot/config.json\" | ConvertFrom-Json).token" | tr -d '\r')
G="1548307046200385678"
API="https://discord.com/api/v10"
ASSETS="C:/Users/Echague/Documents/coding/DTF-License/wwwroot/assets"
TMP=$(mktemp -d)
AUTH="Authorization: Bot $TOKEN"
CT="Content-Type: application/json"

api() { # method path jsonfile -> response body
  local m=$1 p=$2 f=$3
  for t in 1 2 3; do
    R=$(curl -s --max-time 30 -X "$m" -H "$AUTH" -H "$CT" --data-binary @"$f" "$API$p")
    if ! echo "$R" | grep -q '"code": 40333'; then echo "$R"; return; fi
    echo "  retry $t (40333)" >&2; sleep 3
  done
  echo "$R"
}

getid() { # extract .id from JSON via powershell
  echo "$1" | powershell -NoProfile -Command '$j = $input | Out-String | ConvertFrom-Json; $j.id' | tr -d '\r'
}

echo "== 1. server icon + description =="
B64=$(base64 -w0 "$ASSETS/logo.png")
printf '{"icon":"data:image/png;base64,%s","description":"DTF. Wipe forensic traces, browser history, memory. Buy in a ticket - dtf-license.onrender.com"}' "$B64" > "$TMP/icon.json"
R=$(api PATCH "/guilds/$G" "$TMP/icon.json")
if [ -z "$R" ] || echo "$R" | grep -q '"id"'; then echo "server icon + description: OK"; else echo "server icon: $R"; fi

echo "== 2. server banner (Boost L2 needed) =="
B64B=$(base64 -w0 "$ASSETS/banner.png")
printf '{"banner":"data:image/png;base64,%s"}' "$B64B" > "$TMP/banner.json"
R=$(curl -s --max-time 30 -X PATCH -H "$AUTH" -H "$CT" --data-binary @"$TMP/banner.json" "$API/guilds/$G")
if [ -z "$R" ] || echo "$R" | grep -q '"id"'; then echo "server banner: OK (boost unlocked!)"; else echo "server banner: not applied - $R" | head -c 200; echo; fi

echo "== 3. channels =="
CH=$(curl -s --max-time 30 -H "$AUTH" "$API/guilds/$G/channels")
ensure_ch() { # name topic -> id
  local n=$1 topic=$2 id
  id=$(echo "$CH" | powershell -NoProfile -Command "\$j = \$input | Out-String | ConvertFrom-Json; (\$j | Where-Object { \$_.name -eq '$n' -and \$_.type -eq 0 } | Select-Object -First 1).id" | tr -d '\r')
  if [ -n "$id" ] && [ "$id" != "" ]; then echo "#$n: exists ($id)"; echo "$id"; return; fi
  printf '{"name":"%s","type":0,"topic":"%s"}' "$n" "$topic" > "$TMP/ch.json"
  R=$(api POST "/guilds/$G/channels" "$TMP/ch.json")
  id=$(getid "$R")
  if [ -n "$id" ]; then echo "#$n: created ($id)"; else echo "#$n: FAILED - $R"; fi
  echo "$id"
}
RULES=$(ensure_ch rules "Server rules - read before posting" | tail -1)
DL=$(ensure_ch downloads "Latest DTF build + update info" | tail -1)
SUP=$(ensure_ch support "Questions and help (purchases go in tickets)" | tail -1)
echo "ids: rules=$RULES downloads=$DL support=$SUP"
sleep 1

echo "== 4. roles =="
ROLES=$(curl -s --max-time 30 -H "$AUTH" "$API/guilds/$G/roles")
ensure_role() { # name color hoist
  local n=$1 c=$2 h=$3 id
  id=$(echo "$ROLES" | powershell -NoProfile -Command "\$j = \$input | Out-String | ConvertFrom-Json; (\$j | Where-Object { \$_.name -eq '$n' } | Select-Object -First 1).id" | tr -d '\r')
  if [ -n "$id" ]; then echo "role $n: exists"; return; fi
  printf '{"name":"%s","color":%s,"hoist":%s,"mentionable":true}' "$n" "$c" "$h" > "$TMP/role.json"
  R=$(api POST "/guilds/$G/roles" "$TMP/role.json")
  if echo "$R" | grep -q '"id"'; then echo "role $n: created"; else echo "role $n: FAILED - $R"; fi
  sleep 1
}
ensure_role "DTF Owner"  15840287 true
ensure_role "DTF Admin"  8141549  true
ensure_role "DTF Client" 9133302  true

echo "== 5. welcome screen =="
printf '{"enabled":true,"description":"Your PC remembers everything. DTF makes it forget. Read the rules, get the build, buy in a ticket.","welcome_channels":[{"channel_id":"%s","description":"Read before posting","emoji_name":"\\ud83d\\udcdc"},{"channel_id":"%s","description":"Get the build + updates","emoji_name":"\\u2b07"},{"channel_id":"%s","description":"Help and questions","emoji_name":"\\ud83d\\udcac"}]}' "$RULES" "$DL" "$SUP" > "$TMP/ws.json"
R=$(api PATCH "/guilds/$G/welcome-screen" "$TMP/ws.json")
if [ -z "$R" ] || echo "$R" | grep -q '"enabled"'; then echo "welcome screen: OK"; else echo "welcome screen: $R" | head -c 200; echo; fi

echo "== 6. rules embed =="
EXISTS=$(curl -s --max-time 30 -H "$AUTH" "$API/channels/$RULES/messages?limit=20" | powershell -NoProfile -Command "\$j = \$input | Out-String | ConvertFrom-Json; if (\$j | Where-Object { \$_.author.bot -and \$_.embeds.Count -gt 0 -and \$_.embeds[0].title -eq 'DTF SERVER RULES' }) { 'yes' } else { 'no' }" | tr -d '\r')
if [ "$EXISTS" = "yes" ]; then
  echo "rules embed: already there"
else
  cat > "$TMP/rules.json" <<'EOF'
{"embeds":[{"title":"DTF SERVER RULES","color":8141549,
"description":"Keep it clean. One strike for scams, key sharing or chargebacks.",
"fields":[
{"name":"The Rules","value":"1. **No key sharing or reselling** - keys are locked to one PC. Sharing = permanent ban + blacklisted HWID, no refund.\n2. **No fake payment receipts** - scamming = instant ban, no appeals.\n3. **Purchases in tickets only** - click the Create ticket button in the ticket channel; a private ticket opens. Do not DM staff.\n4. **No spam or begging** for free keys.\n5. **One PC per key** - new PC? Open a ticket for a HWID reset.\n6. **Chargebacks = permanent blacklist.**\n7. Discord ToS applies.","inline":false}
]}
]}
EOF
  R=$(api POST "/channels/$RULES/messages" "$TMP/rules.json")
  MID=$(getid "$R")
  if [ -n "$MID" ]; then
    curl -s --max-time 20 -X PUT -H "$AUTH" "$API/channels/$RULES/pins/$MID" > /dev/null
    echo "rules embed: posted + pinned"
  else echo "rules embed FAILED: $R"; fi
fi

echo "== 7. downloads embed =="
EXISTS=$(curl -s --max-time 30 -H "$AUTH" "$API/channels/$DL/messages?limit=20" | powershell -NoProfile -Command "\$j = \$input | Out-String | ConvertFrom-Json; if (\$j | Where-Object { \$_.author.bot -and \$_.embeds.Count -gt 0 -and \$_.embeds[0].title -eq 'HOW TO GET DTF' }) { 'yes' } else { 'no' }" | tr -d '\r')
if [ "$EXISTS" = "yes" ]; then
  echo "downloads embed: already there"
else
  cat > "$TMP/dl.json" <<'EOF'
{"embeds":[{"title":"HOW TO GET DTF","color":9133302,
"description":"1. Buy in a ticket - click the Create ticket button in the ticket channel\n2. Pay GCash / Maya, send the receipt in your ticket\n3. Staff drops your key + the DTF build in the ticket\n4. Paste the key in DTF - it binds to your PC\n\n**Updates:** DTF updates itself silently on launch - you are always on the latest build.\n\n**Never download DTF from anything else.**"
}]}
EOF
  R=$(api POST "/channels/$DL/messages" "$TMP/dl.json")
  if echo "$R" | grep -q '"id"'; then echo "downloads embed: posted"; else echo "downloads embed FAILED: $R"; fi
fi

rm -rf "$TMP"
echo "--- branding done ---"
