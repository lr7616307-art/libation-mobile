#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LIBS="$ROOT/src/Libation.Mobile.Android/libs"
PINNED_COMMIT="d3c1b09993ab78def6d541b191ebd15390da6aae"
EXPECTED_GIT_BLOB="33f571d0e8bdda68156a34cd89098f2510b20ad4"

mkdir -p "$LIBS"
curl --fail --location --retry 5 --retry-all-errors \
  "https://raw.githubusercontent.com/Promises/LibriSync/$PINNED_COMMIT/build-assets/ffmpeg-kit.aar" \
  --output "$LIBS/ffmpeg-kit.aar"

actual_blob="$(git hash-object "$LIBS/ffmpeg-kit.aar")"
if [[ "$actual_blob" != "$EXPECTED_GIT_BLOB" ]]; then
  echo "::error::Pinned LibriSync FFmpegKit archive did not match its verified Git blob."
  exit 1
fi

curl --fail --location --retry 5 --retry-all-errors \
  "https://repo.maven.apache.org/maven2/com/arthenica/smart-exception-java/0.1.1/smart-exception-java-0.1.1.jar" \
  --output "$LIBS/smart-exception-java-0.1.1.jar"
curl --fail --location --retry 5 --retry-all-errors \
  "https://repo.maven.apache.org/maven2/com/arthenica/smart-exception-common/0.1.1/smart-exception-common-0.1.1.jar" \
  --output "$LIBS/smart-exception-common-0.1.1.jar"

test -s "$LIBS/ffmpeg-kit.aar"
test -s "$LIBS/smart-exception-java-0.1.1.jar"
test -s "$LIBS/smart-exception-common-0.1.1.jar"
sha256sum "$LIBS/"*
