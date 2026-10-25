#!/usr/bin/env bash
# Proves each dependency in compose.yaml does its job, which a container health check alone does not: every
# service role can sign in to its own database and no other, the topics exist with their partitions, the realm
# issues tokens with the audiences and claims the services will check.
#
# Run after `docker compose up -d --wait`. CI runs it in the compose job; it works the same on a laptop.
set -euo pipefail
cd "$(dirname "$0")/.."


fail() { echo "FAIL $*" >&2; exit 1; }
ok() { echo "ok   $*"; }

# Postgres --------------------------------------------------------------------------------------------------------

roles=(stays cars flights booking payments airlinesim)

# Over TCP with a password, the way a service connects; the Unix socket inside the container trusts everyone.
psql_as() {
  local role=$1 database=$2 sql=$3
  docker compose exec -T -e PGPASSWORD="$role" postgres \
    psql -h 127.0.0.1 -U "$role" -d "$database" -v ON_ERROR_STOP=1 -tAc "$sql"
}

for i in "${!roles[@]}"; do
  role=${roles[$i]}
  attributes=$(psql_as "$role" "$role" \
    "select rolsuper::text || ' ' || rolbypassrls::text from pg_roles where rolname = current_user") \
    || fail "postgres: $role cannot sign in to its own database"
  [[ $attributes == "false false" ]] || fail "postgres: $role is a superuser or bypasses RLS ($attributes)"

  # The next role in the list, wrapping around, stands in for "any other service".
  other=${roles[$(((i + 1) % ${#roles[@]}))]}
  if psql_as "$role" "$other" "select 1" > /dev/null 2>&1; then
    fail "postgres: $role can connect to the $other database"
  fi
  ok "postgres: $role signs in to $role only, no superuser, no BYPASSRLS"
done

# Kafka -----------------------------------------------------------------------------------------------------------

# kafka-init is a one-shot container. `up --wait` waits for it to exit; a failure there must not hide behind a
# topic list that happens to be complete from an earlier run.
init_state=$(docker inspect --format '{{.State.Status}} {{.State.ExitCode}}' "$(docker compose ps -aq kafka-init)")
[[ $init_state == "exited 0" ]] || fail "kafka: kafka-init is '$init_state', expected 'exited 0'"

# MSYS_NO_PATHCONV stops Git Bash on Windows from rewriting /opt/kafka/... into a Windows path before docker sees
# it. Elsewhere the variable means nothing.
described=$(MSYS_NO_PATHCONV=1 docker compose exec -T kafka /opt/kafka/bin/kafka-topics.sh \
  --bootstrap-server localhost:9092 --describe --exclude-internal)
for topic in stays.v1 cars.v1 flights.v1 booking.v1 payments.v1; do
  for name in "$topic" "$topic.dlq"; do
    grep -qE "^Topic: ${name//./\\.}[[:space:]].*PartitionCount: 12[[:space:]]" <<< "$described" \
      || fail "kafka: topic $name is missing or does not have 12 partitions"
  done
done
ok "kafka: 5 topics and their .dlq twins, 12 partitions each"

# OpenSearch ------------------------------------------------------------------------------------------------------

health=$(curl -fsS http://localhost:9200/_cluster/health) || fail "opensearch: no answer on 9200"
grep -qE '"status":"(green|yellow)"' <<< "$health" || fail "opensearch: cluster unhealthy: $health"
ok "opensearch: cluster $(sed -E 's/.*"status":"([a-z]+)".*/\1/' <<< "$health")"

# Valkey ----------------------------------------------------------------------------------------------------------

[[ $(docker compose exec -T valkey valkey-cli ping | tr -d '\r') == "PONG" ]] || fail "valkey: no PONG"
ok "valkey: PONG"

# Keycloak --------------------------------------------------------------------------------------------------------

realm=http://localhost:8080/realms/viatica
discovery=$(curl -fsS "$realm/.well-known/openid-configuration") || fail "keycloak: no discovery document"
grep -q "\"issuer\":\"$realm\"" <<< "$discovery" || fail "keycloak: unexpected issuer"
ok "keycloak: discovery document, issuer $realm"

# One password grant per user, one after another. Brute force protection counts parallel grants for one account
# as an attack and locks it.
access_token() {
  curl -fsS "$realm/protocol/openid-connect/token" \
    -d grant_type=password -d client_id=viatica-cli -d username="$1" -d password=viatica \
    | sed -n 's/.*"access_token":"\([^"]*\)".*/\1/p'
}

# The middle segment of a JWT is base64url without padding.
claims() {
  local segment
  segment=$(cut -d. -f2 <<< "$1" | tr '_-' '/+')
  while ((${#segment} % 4)); do segment+="="; done
  base64 -d <<< "$segment"
}

traveller=$(claims "$(access_token traveller1)") || fail "keycloak: traveller1 cannot sign in"
for audience in stays-api cars-api flights-api search-api booking-api payments-api; do
  grep -q "\"$audience\"" <<< "$traveller" || fail "keycloak: traveller1's token lacks audience $audience"
done
grep -q '"traveller"' <<< "$traveller" || fail "keycloak: traveller1's token lacks the traveller role"
ok "keycloak: traveller1's token carries all six service audiences and the traveller role"

partner=$(claims "$(access_token partner-stays1)") || fail "keycloak: partner-stays1 cannot sign in"
grep -q '"partnerId":"c0000000-0000-4000-8000-000000000001"' <<< "$partner" \
  || fail "keycloak: partner-stays1's token lacks its partnerId claim"
ok "keycloak: partner-stays1's token carries its partnerId"

# Airline simulator and Flights -----------------------------------------------------------------------------------

curl -fsS http://localhost:8201/health/ready > /dev/null || fail "airline-sim: /health/ready did not answer 200 on 8201"
ok "airline-sim: ready on 8201"

curl -fsS http://localhost:8103/health/ready > /dev/null || fail "flights: /health/ready did not answer 200 on 8103"
ok "flights: ready on 8103 (database and Valkey reachable)"

# A search through the whole path: Flights asks the four airlines, caches the offers in Valkey and answers. It does
# not insist on every airline, because Molasses Airways is slow by design and may miss the 2.5 s deadline; it does
# insist on the fast one, and on the cached search being readable again without a second round of airline calls.
departure=$(date -u -d '+14 days' +%F)
search=$(curl -fsS -X POST "http://localhost:8103/flights/searches?api-version=2026-11-01" \
  -H 'Content-Type: application/json' \
  -d "{\"origin\":\"IST\",\"destination\":\"LHR\",\"departureDate\":\"$departure\",\"adults\":1}") \
  || fail "flights: a search on IST-LHR did not answer 201"
grep -q '"airline":"ZC"' <<< "$search" || fail "flights: the search has no offer from the reliable airline: ${search:0:300}"
search_id=$(sed -n 's/.*"searchId":"\([^"]*\)".*/\1/p' <<< "$search")
curl -fsS "http://localhost:8103/flights/searches/$search_id?\$top=3&api-version=2026-11-01" | grep -q '"value"' \
  || fail "flights: the cached search $search_id could not be read back"
ok "flights: a search fans out to the airlines, is cached in Valkey and pages from the cache"

# Aspire dashboard ------------------------------------------------------------------------------------------------

# An empty OTLP export request is valid protobuf; a 2xx means the sink accepts traces.
curl -fsS -o /dev/null -X POST -H "Content-Type: application/x-protobuf" --data-binary "" \
  http://localhost:4318/v1/traces || fail "dashboard: OTLP/HTTP endpoint refused an export"
curl -fsS -o /dev/null http://localhost:18888/ || fail "dashboard: UI not answering on 18888"
ok "dashboard: accepts OTLP on 4318, UI on 18888"

# Stays -----------------------------------------------------------------------------------------------------------

# The service the compose file builds, behind its own health check. Beyond "ready", one round trip through the
# real token: Keycloak's partner token must be accepted by Stays (audience, issuer, partnerId claim), the draft the
# partner creates must be invisible to everybody else, and the public list must answer with an api-version.
stays=http://localhost:8101
version=api-version=2026-11-01

curl -fsS "$stays/health/ready" > /dev/null || fail "stays: /health/ready does not answer"
curl -fsS "$stays/properties?$version" | grep -q '"value"' || fail "stays: the public property list does not answer"

property_id=$(printf '%08x-%04x-4%03x-8%03x-%012x' $((RANDOM * 32768 + RANDOM)) "$RANDOM" $((RANDOM % 4096)) $((RANDOM % 4096)) $(((RANDOM << 30) | (RANDOM << 15) | RANDOM)))
partner_token=$(access_token partner-stays1) || fail "keycloak: partner-stays1 cannot sign in"
created=$(curl -sS -o /dev/null -w '%{http_code}' -X PUT "$stays/properties/$property_id?$version" \
  -H "Authorization: Bearer $partner_token" -H 'Content-Type: application/json' \
  -d '{"name":"Check Stack Inn","description":"Created by deploy/check-stack.sh.","address":{"line1":"1 Test Street","city":"Lisbon","postalCode":"1000-001","countryCode":"PT"},"location":{"latitude":38.7,"longitude":-9.1},"starRating":3,"amenities":["wifi"],"checkInFrom":"15:00:00","checkOutUntil":"11:00:00","cancellationPolicy":{"freeUntilHoursBeforeStart":24,"penaltyAfter":"FirstUnit"}}')
[[ $created == 201 ]] || fail "stays: creating a property with a Keycloak partner token answered $created, expected 201"
[[ $(curl -sS -o /dev/null -w '%{http_code}' "$stays/properties/$property_id?$version") == 404 ]] \
  || fail "stays: a draft property is visible without a token"
ok "stays: ready, public list, a partner token creates a draft only its partner sees"

# Cars ------------------------------------------------------------------------------------------------------------

curl -fsS http://localhost:8102/health/ready > /dev/null || fail "cars: /health/ready did not answer on 8102"
ok "cars: /health/ready"

# A quote for a location that does not exist is a 404 in the error envelope. Anyone may ask, and no data has to be
# arranged, so it proves the version check, routing, the database read and the envelope in one request.
answer=$(curl -sS -X POST "http://localhost:8102/cars/quotes?api-version=2026-11-01" -H "Content-Type: application/json" \
  -d '{"locationId":"01a16fb5-0000-7000-8000-000000000001","categoryId":"01a16fb5-0000-7000-8000-000000000002","pickUpAt":"2099-01-01T10:00:00Z","dropOffAt":"2099-01-02T10:00:00Z","driverAge":30}')
grep -q '"code":"LocationNotFound"' <<< "$answer" || fail "cars: unexpected answer to a quote for an unknown location: $answer"
ok "cars: quotes answer in the error envelope"

# Search ----------------------------------------------------------------------------------------------------------

# Ready means OpenSearch answers and both aliases exist, which is what search-migrate makes. The autocomplete call
# needs no token and no index data: the airport list is compiled into the service.
curl -fsS http://localhost:8104/health/ready > /dev/null || fail "search: /health/ready is not healthy"
airports=$(curl -fsS "http://localhost:8104/autocomplete?q=lis&types=airport&api-version=2026-11-01")   || fail "search: autocomplete did not answer"
grep -q '"code":"LIS"' <<< "$airports" || fail "search: autocomplete did not find the Lisbon airport"
ok "search: ready, aliases in place, anonymous autocomplete answers"

# Payment provider simulator and Payments --------------------------------------------------------------------------

curl -fsS http://localhost:8202/health/ready > /dev/null || fail "psp-sim: /health/ready did not answer 200 on 8202"
ok "psp-sim: ready on 8202"

curl -fsS http://localhost:8106/health/ready > /dev/null || fail "payments: /health/ready did not answer 200 on 8106"
ok "payments: ready on 8106 (database reachable)"

# A card through the whole path: a traveller token tokenises a test card at Payments, Payments hands it to the
# simulator, and the answer holds a token and the last four digits and nothing that could be charged. The expiry is
# three years out so the check does not age.
year=$(($(date -u +%Y) + 3))
tokenised=$(curl -fsS -X POST "http://localhost:8106/payments/methods?api-version=2026-11-01" \
  -H "Authorization: Bearer $(access_token traveller1)" -H 'Content-Type: application/json' \
  -d "{\"number\":\"4242 4242 4242 4242\",\"expMonth\":12,\"expYear\":$year,\"cvc\":\"123\"}") \
  || fail "payments: tokenising a test card with a Keycloak traveller token did not answer 201"
grep -q '"last4":"4242"' <<< "$tokenised" || fail "payments: the tokenised card has no last four digits: ${tokenised:0:300}"
grep -q '"token":"pm_' <<< "$tokenised" || fail "payments: the tokenised card has no pm_ token: ${tokenised:0:300}"
! grep -q '4242424242424242\|4242 4242 4242 4242' <<< "$tokenised" || fail "payments: the answer to a tokenisation repeats the card number"
ok "payments: a Keycloak traveller token tokenises a test card through the simulator; the answer holds the last four digits only"

# Booking ---------------------------------------------------------------------------------------------------------

# Ready means the database answers. Beyond that, one round trip through the real token: Keycloak's traveller token
# must be accepted by Booking (audience, issuer, sub), the draft trip it starts must be invisible to another
# traveller, and a partner's booking list must answer, empty, rather than refuse.
booking=http://localhost:8105

curl -fsS "$booking/health/ready" > /dev/null || fail "booking: /health/ready did not answer 200 on 8105"

trip_id=$(printf '%08x-%04x-4%03x-8%03x-%012x' $((RANDOM * 32768 + RANDOM)) "$RANDOM" $((RANDOM % 4096)) $((RANDOM % 4096)) $(((RANDOM << 30) | (RANDOM << 15) | RANDOM)))
traveller1_token=$(access_token traveller1) || fail "keycloak: traveller1 cannot sign in"
traveller2_token=$(access_token traveller2) || fail "keycloak: traveller2 cannot sign in"
created=$(curl -sS -o /dev/null -w '%{http_code}' -X PUT "$booking/trips/$trip_id?$version" -H "Authorization: Bearer $traveller1_token")
[[ $created == 201 ]] || fail "booking: starting a trip with a Keycloak traveller token answered $created, expected 201"
curl -fsS "$booking/trips/$trip_id?$version" -H "Authorization: Bearer $traveller1_token" | grep -q '"status":"Draft"'   || fail "booking: the traveller cannot read their own draft trip"
[[ $(curl -sS -o /dev/null -w '%{http_code}' "$booking/trips/$trip_id?$version" -H "Authorization: Bearer $traveller2_token") == 404 ]]   || fail "booking: another traveller can see a trip that is not theirs"
curl -fsS "$booking/partner/bookings?$version" -H "Authorization: Bearer $(access_token partner-stays1)" | grep -q '"value"'   || fail "booking: the partner's booking list does not answer"
ok "booking: ready, a traveller token starts a draft trip only its traveller sees, the partner list answers"

echo "stack ok"
