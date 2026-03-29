#!/usr/bin/env bash
# =============================================================================
# production-scenario.sh — Simulates real-world API rate limit scenarios
#
# Users and their limits (mimicking real APIM token claims):
#   "premium-user"    → burst=10, rps=2  (high-tier API consumer)
#   "standard-user"   → burst=5,  rps=1  (standard tier)
#   "trial-user"      → burst=2,  rps=0.5 (heavily restricted)
#   "bot-attacker"    → burst=3,  rps=1  (malicious rapid fire)
#   "batch-job"       → burst=20, rps=5  (internal service, high limit)
#
# Run this while watching the dashboard at:
#   open src/RateLimiter.Function/dashboard.html
# =============================================================================

BASE_URL="http://127.0.0.1:7071/api/rate-limit/check"
STATS_URL="http://127.0.0.1:7071/api/rate-limit/stats"

# Color helpers
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
CYAN='\033[0;36m'; BOLD='\033[1m'; RESET='\033[0m'

print_response() {
  local user=$1 resp=$2 req_num=$3
  local allowed=$(echo $resp | python3 -c "import sys,json; d=json.load(sys.stdin); print('✅' if d['allowed'] else '❌')" 2>/dev/null)
  local remaining=$(echo $resp | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['remaining'])" 2>/dev/null)
  local retry=$(echo $resp | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('retryAfterMs',0))" 2>/dev/null)

  if echo "$resp" | grep -q '"allowed":true'; then
    echo -e "${GREEN}  [req $req_num] $user → $allowed remaining=$remaining${RESET}"
  else
    echo -e "${RED}  [req $req_num] $user → $allowed THROTTLED  retry=${retry}ms${RESET}"
  fi
}

send() {
  local oid=$1 burst=$2 rps=$3 req_num=$4
  curl -s -X POST "$BASE_URL" \
    -H "Content-Type: application/json" \
    -d "{\"oid\":\"$oid\",\"burst\":$burst,\"rps\":$rps}"
}

print_stats() {
  echo ""
  echo -e "${CYAN}${BOLD}── Live Redis State ──────────────────────────────────${RESET}"
  curl -s "$STATS_URL" | python3 -c "
import sys, json
data = json.load(sys.stdin)
if not data:
    print('  (no active users)')
else:
    print(f'  {'OID':<22} {'TOKENS':>8}   {'TTL':>5}')
    print('  ' + '-'*44)
    for u in sorted(data, key=lambda x: x['tokens']):
        bar_len = int(u['tokens'] * 4)
        bar = '█' * bar_len + '░' * (20 - bar_len)
        status = 'THROTTLED' if u['tokens'] < 1 else 'ok'
        print(f\"  {u['oid']:<22} {u['tokens']:>6.2f}T   {u['ttlSeconds']:>3}s  {status}\")
" 2>/dev/null
  echo ""
}

echo ""
echo -e "${BOLD}╔══════════════════════════════════════════════════════╗${RESET}"
echo -e "${BOLD}║   Rate Limiter — Production Scenario Test            ║${RESET}"
echo -e "${BOLD}╚══════════════════════════════════════════════════════╝${RESET}"
echo -e "  Dashboard: ${CYAN}open src/RateLimiter.Function/dashboard.html${RESET}"
echo ""

# ── PHASE 1: Normal traffic, no throttling expected ─────────────────────────
echo -e "${BOLD}▶ PHASE 1: Normal Traffic (within limits)${RESET}"
echo -e "  premium-user  → 5 requests (burst=10, rps=2) — should all pass"
for i in {1..5}; do
  r=$(send "premium-user" 10 2 $i); print_response "premium-user" "$r" $i; done
echo ""
echo -e "  standard-user → 3 requests (burst=5, rps=1) — should all pass"
for i in {1..3}; do
  r=$(send "standard-user" 5 1 $i); print_response "standard-user" "$r" $i; done

print_stats
sleep 1

# ── PHASE 2: Trial user + bot attack ────────────────────────────────────────
echo -e "${BOLD}▶ PHASE 2: Trial User Exhaustion + Bot Rapid-Fire Attack${RESET}"
echo -e "  trial-user  → 6 rapid requests (burst=2) — should throttle after 2"
for i in {1..6}; do
  r=$(send "trial-user" 2 0.5 $i); print_response "trial-user" "$r" $i; done

echo ""
echo -e "  bot-attacker → 10 rapid requests (burst=3, rps=1) — throttle after 3"
for i in {1..10}; do
  r=$(send "bot-attacker" 3 1 $i); print_response "bot-attacker" "$r" $i; done

print_stats
sleep 1

# ── PHASE 3: Internal batch job + standard user continues ───────────────────
echo -e "${BOLD}▶ PHASE 3: Internal Batch Job (high limits) + Standard User Resume${RESET}"
echo -e "  batch-job → 15 requests (burst=20, rps=5) — should all pass"
for i in {1..15}; do
  r=$(send "batch-job" 20 5 $i); print_response "batch-job" "$r" $i; done

echo ""
echo -e "  standard-user → 5 more rapid requests (burst=5) — may throttle"
for i in {4..8}; do
  r=$(send "standard-user" 5 1 $i); print_response "standard-user" "$r" $i; done

print_stats
sleep 1

# ── PHASE 4: All users hammer simultaneously ─────────────────────────────────
echo -e "${BOLD}▶ PHASE 4: Everyone fires 5 requests simultaneously${RESET}"
for user_config in "premium-user:10:2" "standard-user:5:1" "trial-user:2:0.5" "bot-attacker:3:1" "batch-job:20:5"; do
  oid=$(echo $user_config | cut -d: -f1)
  burst=$(echo $user_config | cut -d: -f2)
  rps=$(echo $user_config | cut -d: -f3)
  for i in {1..5}; do
    r=$(send "$oid" $burst $rps $i)
    print_response "$oid" "$r" $i
  done
done

print_stats

echo -e "${BOLD}✅ Production scenario complete. Check the dashboard!${RESET}"
echo ""
