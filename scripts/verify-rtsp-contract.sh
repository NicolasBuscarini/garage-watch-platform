#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
export RTSP_CONTRACT_RUN_ID="$(date +%s)-$$"
export AUTH_USERNAME="rtsp-contract-test"
export AUTH_PASSWORD="$(python3 -c 'import secrets; print(secrets.token_urlsafe(32))')"
export AUTH_ROLE="Viewer"
spool_directory="data/rtsp-contract-${RTSP_CONTRACT_RUN_ID}"
cookie_file="/tmp/garage-watch-rtsp-${RTSP_CONTRACT_RUN_ID}.cookies"
cleanup() {
    docker compose --profile rtsp-contract rm -sf api-rtsp-contract rtsp-test-publisher rtsp-test-server >/dev/null || true
    rm -rf -- "$spool_directory"
    rm -f -- "$cookie_file"
}
trap cleanup EXIT
docker compose --profile rtsp-contract up -d --build rtsp-test-server rtsp-test-publisher api-rtsp-contract
service_ready=false
for _ in $(seq 1 45); do
    if curl --fail --silent "http://127.0.0.1:${RTSP_CONTRACT_PORT:-8082}/health/live" >/dev/null; then
        service_ready=true
        break
    fi
    sleep 1
done
if [[ "$service_ready" != true ]]; then
    docker compose --profile rtsp-contract logs --no-color --tail 30 api-rtsp-contract >&2
    echo "API test service did not start." >&2
    exit 1
fi
python3 -c 'import json,os; print(json.dumps({"username":os.environ["AUTH_USERNAME"],"password":os.environ["AUTH_PASSWORD"]}))' \
    | curl --fail --silent --show-error --cookie-jar "$cookie_file" -H 'Content-Type: application/json' \
        --data-binary @- "http://127.0.0.1:${RTSP_CONTRACT_PORT:-8082}/api/v1/auth/login" >/dev/null

ready=false
for _ in $(seq 1 45); do
    if curl --fail --silent --cookie "$cookie_file" "http://127.0.0.1:${RTSP_CONTRACT_PORT:-8082}/health/ready" > /tmp/garage-watch-rtsp-health.json \
        && grep -q '"adapter":"ffmpeg-rtsp-h264"' /tmp/garage-watch-rtsp-health.json \
        && grep -q '"h264-validation"' /tmp/garage-watch-rtsp-health.json \
        && grep -q '"aac-audio"' /tmp/garage-watch-rtsp-health.json \
        && grep -q '"state":"capturing"' /tmp/garage-watch-rtsp-health.json; then
        ready=true
        break
    fi
    sleep 1
done

if [[ "$ready" != true ]]; then
    cat /tmp/garage-watch-rtsp-health.json 2>/dev/null || true
    echo "RTSP contract source did not become ready." >&2
    exit 1
fi

initial_epoch=$(python3 -c 'import json; print(json.load(open("/tmp/garage-watch-rtsp-health.json"))["capture"]["streamEpoch"])')
export RTSP_INITIAL_EPOCH="$initial_epoch"
sleep 12
docker compose stop rtsp-test-publisher
degraded=false
for _ in $(seq 1 20); do
    http_status=$(curl --silent --cookie "$cookie_file" --output /tmp/garage-watch-rtsp-health.json --write-out '%{http_code}' \
        "http://127.0.0.1:${RTSP_CONTRACT_PORT:-8082}/health/ready" || true)
    if [[ "$http_status" == 503 ]] && grep -q '"state":"reconnecting"' /tmp/garage-watch-rtsp-health.json; then
        degraded=true
        break
    fi
    sleep 1
done
if [[ "$degraded" != true ]]; then
    echo "Capture did not report a gap after stopping the synthetic publisher." >&2
    exit 1
fi
docker compose start rtsp-test-publisher

recovered=false
for _ in $(seq 1 45); do
    if curl --fail --silent --cookie "$cookie_file" "http://127.0.0.1:${RTSP_CONTRACT_PORT:-8082}/health/ready" > /tmp/garage-watch-rtsp-health.json \
        && grep -q '"state":"capturing"' /tmp/garage-watch-rtsp-health.json \
        && [[ $(python3 -c 'import json; print(json.load(open("/tmp/garage-watch-rtsp-health.json"))["capture"]["streamEpoch"])') != "$initial_epoch" ]]; then
        recovered=true
        break
    fi
    sleep 1
done
if [[ "$recovered" != true ]]; then
    cat /tmp/garage-watch-rtsp-health.json 2>/dev/null || true
    echo "Capture did not recover into a new stream epoch." >&2
    exit 1
fi
export RTSP_RECOVERED_EPOCH="$(python3 -c 'import json; print(json.load(open("/tmp/garage-watch-rtsp-health.json"))["capture"]["streamEpoch"])')"

python3 - <<'PY'
import hashlib
import json
import os
import subprocess
from pathlib import Path

root = Path("data") / ("rtsp-contract-" + __import__("os").environ["RTSP_CONTRACT_RUN_ID"])
manifests = sorted((root / "manifests").glob("*.json"))
assert manifests, "no segment manifests were persisted"
payloads = []
for manifest_path in manifests:
    manifest = json.loads(manifest_path.read_text())
    payloads.append(manifest)
    assert manifest["schemaVersion"] == "1.0"
    assert manifest["cameraId"] == "rtsp-contract"
    assert manifest["fileName"] == f'{manifest["segmentId"]}.ts'
    segment = root / "segments" / manifest["fileName"]
    data = segment.read_bytes()
    assert len(data) == manifest["sizeBytes"]
    assert hashlib.sha256(data).hexdigest() == manifest["sha256"]
print(f"{len(manifests)} synthetic RTSP segment manifest(s) and SHA-256 verified")

same_epoch = sorted((item for item in payloads if item["streamEpoch"] == os.environ["RTSP_INITIAL_EPOCH"]),
                    key=lambda item: item["startedAt"])
assert len(same_epoch) >= 2, "need two segments from one uninterrupted RTSP epoch to verify timestamp continuity"
start_times = []
for item in same_epoch[:2]:
    segment_path = f'/data/rtsp-contract-{os.environ["RTSP_CONTRACT_RUN_ID"]}/segments/{item["fileName"]}'
    probe = subprocess.check_output([
        "docker", "compose", "exec", "-T", "api-rtsp-contract", "ffprobe", "-v", "error",
        "-show_entries", "format=start_time", "-of", "json", segment_path
    ], text=True)
    start_times.append(float(json.loads(probe)["format"]["start_time"]))
assert start_times[1] > start_times[0], f"MPEG-TS timestamps reset or overlap between consecutive segments: {start_times}"
print(f"consecutive MPEG-TS timestamps advance within an epoch: {start_times[0]:.3f}s → {start_times[1]:.3f}s")
print(manifests[0].name)
gaps = sorted((root / "gaps").glob("*.json"))
assert gaps, "no gap was persisted after the source interruption"
gap = json.loads(gaps[-1].read_text())
assert gap["schemaVersion"] == "1.0"
assert gap["reasonCode"] == "capture_source_unavailable"
assert gap["previousStreamEpoch"] == os.environ["RTSP_INITIAL_EPOCH"]
assert os.environ["RTSP_RECOVERED_EPOCH"] != os.environ["RTSP_INITIAL_EPOCH"]
assert any(json.loads(path.read_text())["streamEpoch"] == os.environ["RTSP_RECOVERED_EPOCH"] for path in manifests)
print(f"{len(gaps)} gap manifest(s) persisted")
PY

manifest_name=$(python3 -c 'import os; from pathlib import Path; print(sorted((Path("data") / ("rtsp-contract-" + os.environ["RTSP_CONTRACT_RUN_ID"]) / "manifests").glob("*.json"))[0].name)')
segment_id=${manifest_name%.json}
codec=$(docker compose exec -T api-rtsp-contract ffprobe -v error -select_streams v:0 \
    -show_entries stream=codec_name -of default=noprint_wrappers=1:nokey=1 "/data/rtsp-contract-${RTSP_CONTRACT_RUN_ID}/segments/${segment_id}.ts")
profile=$(docker compose exec -T api-rtsp-contract ffprobe -v error -select_streams v:0 \
    -show_entries stream=profile -of default=noprint_wrappers=1:nokey=1 "/data/rtsp-contract-${RTSP_CONTRACT_RUN_ID}/segments/${segment_id}.ts")
pixel_format=$(docker compose exec -T api-rtsp-contract ffprobe -v error -select_streams v:0 \
    -show_entries stream=pix_fmt -of default=noprint_wrappers=1:nokey=1 "/data/rtsp-contract-${RTSP_CONTRACT_RUN_ID}/segments/${segment_id}.ts")
audio_codec=$(docker compose exec -T api-rtsp-contract ffprobe -v error -select_streams a:0 \
    -show_entries stream=codec_name -of default=noprint_wrappers=1:nokey=1 "/data/rtsp-contract-${RTSP_CONTRACT_RUN_ID}/segments/${segment_id}.ts" | sort -u)
if [[ -z "$codec" ]] || [[ $(printf '%s\n' "$codec" | sort -u) != h264 ]] \
    || [[ $(printf '%s\n' "$profile" | sort -u) != "Constrained Baseline" ]] \
    || [[ $(printf '%s\n' "$pixel_format" | sort -u) != yuv420p ]]; then
    echo "Expected H.264 Constrained Baseline / yuv420p; ffprobe profile/pixel format did not match." >&2
    exit 1
fi
if [[ "$audio_codec" != aac ]]; then
    echo "Expected AAC audio in the persisted MPEG-TS segment; ffprobe reported: ${audio_codec:-none}." >&2
    exit 1
fi

echo "RTSP H.264/AAC contract and recovery smoke passed (synthetic MediaMTX source; not camera validation)."
