#!/usr/bin/env bash
# ============================================================
# phobri_test.sh — Full integration test script
# ============================================================
# Runs the Phobri desktop server in headless mode and
# executes the integration test suite against it.
#
# Usage:
#   ./phobri_test.sh              # All tests
#   ./phobri_test.sh --unit-only  # Unit tests only
#   ./phobri_test.sh --quick      # Skip emulator tests
# ============================================================

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$SCRIPT_DIR"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
NC='\033[0m' # No Color

echo -e "${GREEN}╔══════════════════════════════════════╗${NC}"
echo -e "${GREEN}║      Phobri Integration Tests       ║${NC}"
echo -e "${GREEN}╚══════════════════════════════════════╝${NC}"
echo ""

MODE="${1:-all}"

# --- Desktop Unit Tests ---
run_desktop_unit_tests() {
    echo -e "${YELLOW}[Desktop] Running unit tests...${NC}"
    cd "$PROJECT_ROOT/desktop/Phobri.Desktop.Tests"
    dotnet test --verbosity minimal 2>&1 | tail -5
    echo -e "${GREEN}[Desktop] Unit tests complete.${NC}"
}

# --- Desktop Integration Tests ---
run_desktop_integration_tests() {
    echo -e "${YELLOW}[Desktop] Running integration tests (full protocol)...${NC}"
    cd "$PROJECT_ROOT/desktop/Phobri.Desktop.IntegrationTests"
    dotnet test --verbosity minimal 2>&1 | tail -5
    echo -e "${GREEN}[Desktop] Integration tests complete.${NC}"
}

# --- Android Unit Tests (Robolectric) ---
run_android_tests() {
    echo -e "${YELLOW}[Android] Running unit tests (Robolectric)...${NC}"
    cd "$PROJECT_ROOT/android"
    export ANDROID_HOME="$HOME/android-sdk"
    ./gradlew test 2>&1 | tail -10
    echo -e "${GREEN}[Android] Unit tests complete.${NC}"
}

# --- Headless Server Smoke Test ---
run_server_smoke_test() {
    echo -e "${YELLOW}[Server] Smoke-testing headless server...${NC}"
    cd "$PROJECT_ROOT/desktop/Phobri.Desktop"

    # Start headless server in background
    dotnet run -- --headless --port 19876 &
    SERVER_PID=$!
    sleep 3

    # Check if server responds to ping
    if curl -sk "https://127.0.0.1:19876/api/v1/ping" 2>/dev/null | grep -q '"status":"ok"'; then
        echo -e "${GREEN}[Server] Headless server is responding!${NC}"
        curl -sk "https://127.0.0.1:19876/api/v1/ping" 2>/dev/null | head -1
    else
        echo -e "${RED}[Server] Server did not respond.${NC}"
        kill $SERVER_PID 2>/dev/null || true
        exit 1
    fi

    # Stop server
    kill $SERVER_PID 2>/dev/null || true
    wait $SERVER_PID 2>/dev/null || true
    echo -e "${GREEN}[Server] Smoke test passed.${NC}"
}

# --- Android Emulator Test (if available) ---
run_emulator_test() {
    echo -e "${YELLOW}[Emulator] Checking Android emulator...${NC}"

    export ANDROID_HOME="$HOME/android-sdk"
    EMULATOR="$ANDROID_HOME/emulator/emulator"
    ADB="$ANDROID_HOME/platform-tools/adb"

    if [ ! -f "$EMULATOR" ]; then
        echo -e "${YELLOW}[Emulator] Not installed — skipping.${NC}"
        return
    fi

    AVD_NAME="phobri_test"
    if ! "$EMULATOR" -list-avds | grep -q "$AVD_NAME"; then
        echo -e "${YELLOW}[Emulator] AVD '$AVD_NAME' not found — skipping.${NC}"
        return
    fi

    echo -e "${YELLOW}[Emulator] Starting headless emulator...${NC}"

    # Start emulator headless
    "$EMULATOR" \
        -avd "$AVD_NAME" \
        -no-window \
        -no-audio \
        -no-boot-anim \
        -accel off \
        -gpu swiftshader_indirect \
        -memory 2048 \
        -cores 2 &
    EMU_PID=$!

    # Wait for boot
    echo -n "Waiting for emulator to boot"
    for i in $(seq 1 180); do
        if "$ADB" devices 2>/dev/null | grep -q "emulator.*device"; then
            echo ""
            echo -e "${GREEN}[Emulator] Booted!${NC}"
            break
        fi
        echo -n "."
        sleep 2
    done

    # Build and install APK
    echo -e "${YELLOW}[Emulator] Building APK...${NC}"
    cd "$PROJECT_ROOT/android"
    ./gradlew assembleDebug 2>&1 | tail -5

    echo -e "${YELLOW}[Emulator] Installing APK...${NC}"
    "$ADB" install -r app/build/outputs/apk/debug/app-debug.apk 2>&1

    # Grant permissions
    echo -e "${YELLOW}[Emulator] Granting permissions...${NC}"
    "$ADB" shell pm grant com.phobri.android android.permission.READ_SMS
    "$ADB" shell pm grant com.phobri.android android.permission.READ_CALL_LOG
    "$ADB" shell pm grant com.phobri.android android.permission.READ_PHONE_STATE
    "$ADB" shell pm grant com.phobri.android android.permission.READ_CONTACTS
    "$ADB" shell pm grant com.phobri.android android.permission.POST_NOTIFICATIONS

    echo -e "${GREEN}[Emulator] APK installed and permissions granted.${NC}"

    # Cleanup
    kill $EMU_PID 2>/dev/null || true
}

# --- Main ---
case "$MODE" in
    --unit-only)
        run_desktop_unit_tests
        echo ""
        echo -e "${GREEN}╔══════════════════════════════════════╗${NC}"
        echo -e "${GREEN}║        All unit tests passed!       ║${NC}"
        echo -e "${GREEN}╚══════════════════════════════════════╝${NC}"
        ;;
    --quick)
        run_desktop_unit_tests
        run_desktop_integration_tests
        echo ""
        echo -e "${GREEN}╔══════════════════════════════════════╗${NC}"
        echo -e "${GREEN}║      All quick tests passed!        ║${NC}"
        echo -e "${GREEN}╚══════════════════════════════════════╝${NC}"
        ;;
    --full)
        run_desktop_unit_tests
        run_desktop_integration_tests
        run_android_tests
        run_server_smoke_test
        echo ""
        echo -e "${GREEN}╔══════════════════════════════════════╗${NC}"
        echo -e "${GREEN}║       All full tests passed!        ║${NC}"
        echo -e "${GREEN}╚══════════════════════════════════════╝${NC}"
        ;;
    *)
        run_desktop_unit_tests
        run_desktop_integration_tests
        run_android_tests
        echo ""
        echo -e "${GREEN}╔══════════════════════════════════════╗${NC}"
        echo -e "${GREEN}║       All tests passed!             ║${NC}"
        echo -e "${GREEN}╚══════════════════════════════════════╝${NC}"
        ;;
esac
