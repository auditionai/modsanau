#!/bin/bash
# PLAN 108 — Edge Functions Verification Script
# Test các Edge Functions đã deployed trên Supabase hosted

set -e

SUPABASE_URL="https://plvuutsjwsawkkrmvigz.supabase.co"
SUPABASE_ANON_KEY="sb_publishable_wSAdCCfFmDGPVOxblESoRQ_1ol-e-PG"

echo "=================================================="
echo "PLAN 108 — Edge Functions Verification"
echo "Target: $SUPABASE_URL"
echo "=================================================="
echo ""

# Color codes
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Test function
test_endpoint() {
    local name="$1"
    local url="$2"
    local method="${3:-GET}"
    local expected_status="${4:-200}"

    echo -n "Testing $name... "

    response=$(curl -s -o /dev/null -w "%{http_code}" -X "$method" \
        -H "apikey: $SUPABASE_ANON_KEY" \
        -H "Authorization: Bearer $SUPABASE_ANON_KEY" \
        "$url" 2>&1)

    if [ "$response" == "$expected_status" ]; then
        echo -e "${GREEN}✓ PASS${NC} (HTTP $response)"
        return 0
    else
        echo -e "${RED}✗ FAIL${NC} (Expected $expected_status, got $response)"
        return 1
    fi
}

# Test function with detailed response
test_endpoint_verbose() {
    local name="$1"
    local url="$2"
    local method="${3:-GET}"
    local body="${4:-}"

    echo "========================================"
    echo "Testing: $name"
    echo "URL: $url"
    echo "Method: $method"
    echo "========================================"

    if [ -n "$body" ]; then
        response=$(curl -s -w "\n\nHTTP_STATUS:%{http_code}" -X "$method" \
            -H "apikey: $SUPABASE_ANON_KEY" \
            -H "Authorization: Bearer $SUPABASE_ANON_KEY" \
            -H "Content-Type: application/json" \
            -d "$body" \
            "$url")
    else
        response=$(curl -s -w "\n\nHTTP_STATUS:%{http_code}" -X "$method" \
            -H "apikey: $SUPABASE_ANON_KEY" \
            -H "Authorization: Bearer $SUPABASE_ANON_KEY" \
            "$url")
    fi

    http_status=$(echo "$response" | grep -o "HTTP_STATUS:[0-9]*" | cut -d':' -f2)
    body_response=$(echo "$response" | sed '/HTTP_STATUS:/d')

    echo "Status: $http_status"
    echo "Response:"
    echo "$body_response" | head -20
    echo ""

    return 0
}

echo "1. Testing Edge Function: gpti2-image"
echo "----------------------------------------"
test_endpoint "gpti2-image (OPTIONS)" \
    "$SUPABASE_URL/functions/v1/gpti2-image" \
    "OPTIONS" \
    "204"

# Note: GET without auth should return 401
test_endpoint "gpti2-image (GET without valid auth)" \
    "$SUPABASE_URL/functions/v1/gpti2-image" \
    "GET" \
    "401"

echo ""

echo "2. Testing Edge Function: payments"
echo "----------------------------------------"
test_endpoint "payments (OPTIONS)" \
    "$SUPABASE_URL/functions/v1/payments" \
    "OPTIONS" \
    "204"

# Catalog endpoint should work with anon key
echo ""
echo "Testing payments/catalog endpoint (verbose):"
test_endpoint_verbose "payments/catalog" \
    "$SUPABASE_URL/functions/v1/payments/catalog" \
    "GET"

echo ""

echo "3. Testing Edge Function: sepay-webhook"
echo "----------------------------------------"
test_endpoint "sepay-webhook (OPTIONS)" \
    "$SUPABASE_URL/functions/v1/sepay-webhook" \
    "OPTIONS" \
    "204"

# POST without signature should return 401 or 400
test_endpoint "sepay-webhook (POST without signature)" \
    "$SUPABASE_URL/functions/v1/sepay-webhook" \
    "POST" \
    "401"

echo ""
echo "=================================================="
echo "Edge Functions Verification Complete"
echo "=================================================="
echo ""
echo "NOTES:"
echo "- gpti2-image requires authenticated user JWT"
echo "- payments/catalog works with anon key"
echo "- payments/orders requires authenticated user JWT"
echo "- sepay-webhook requires SePay signature"
echo ""
echo "NEXT STEPS:"
echo "1. Run PLAN_108_VERIFICATION_SCRIPT.sql in Supabase Dashboard"
echo "2. Verify Admin Portal at https://modsanau.netlify.app/admin"
echo "3. Obtain Gateway URL + ECDSA public key from Product Owner"
echo "4. Begin desktop E2E testing with actual user account"
echo ""
